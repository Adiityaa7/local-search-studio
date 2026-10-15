// Local Search Studio desktop integration, added 2026-10-15. Apache-2.0.
// XERJ engine remains unmodified; attribution and license are in ../notices.
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace LocalSearchStudio;

public sealed record Library(string Id, string Name, string Path, DateTime? Updated = null, string State = "Ready to index")
{
    public string Prefix => "lss" + Id;
    public string Detail => Updated is null ? State : $"{State} · {Updated:dd MMM, HH:mm}";
}
public sealed record SearchHit(string Title, string Path, string Folder, string Text, string Index, string Id, string LibraryId)
{
    public string Kind => System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant() is { Length: > 0 } ext ? ext : "TEXT";
    public string Snippet { get { var compact = System.Text.RegularExpressions.Regex.Replace(Text, @"\s+", " ").Trim(); return compact.Length > 240 ? compact[..240] + "…" : compact; } }
}
public sealed record SearchPage(IReadOnlyList<SearchHit> Hits, long Total, double Milliseconds, bool Cached = false);
public sealed record IndexResult(int ExitCode, string Output, double Seconds)
{
    public bool Success => ExitCode is 0 or 3;
}

public sealed class StudioStore
{
    public string Root { get; }
    private string FileName => System.IO.Path.Combine(Root, "libraries.json");
    public StudioStore(string root) { Root = System.IO.Path.GetFullPath(root); Directory.CreateDirectory(Root); }
    public List<Library> Load()
    {
        if (!File.Exists(FileName)) return [];
        try { return JsonSerializer.Deserialize<List<Library>>(File.ReadAllText(FileName)) ?? []; }
        catch (JsonException ex) { throw new InvalidDataException("The library list could not be read. It has been preserved in the app data folder.", ex); }
    }
    public void Save(IEnumerable<Library> libraries)
    {
        var temp = FileName + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(libraries, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FileName, true);
    }
    public static Library NewLibrary(string path)
    {
        var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException("This folder is no longer available.");
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToUpperInvariant())))[..12].ToLowerInvariant();
        return new(id, System.IO.Path.GetFileName(full) is { Length: > 0 } name ? name : full, full);
    }
    public static string? SafeResultPath(Library lib, string path)
    {
        try
        {
            var root = System.IO.Path.GetFullPath(lib.Path).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
            var full = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.Combine(root, path));
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
            // Do not follow a link introduced after indexing outside the selected folder.
            for (var current = full; current.Length >= root.Length; current = System.IO.Path.GetDirectoryName(current) ?? "")
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return null;
            return full;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { return null; }
    }
}

public sealed class EngineService : IAsyncDisposable
{
    private readonly string executable;
    private readonly string root;
    private Process? server;
    private Process? indexer;
    private HttpClient? client;
    private FileStream? ownership;
    private readonly Queue<string> diagnostics = new();
    private readonly Dictionary<string, SearchPage> cache = new();
    private readonly Queue<string> cacheOrder = new();
    private readonly SemaphoreSlim indexing = new(1);
    private readonly SemaphoreSlim stopping = new(1);
    private CancellationTokenSource shutdown = new();
    private readonly ChildProcessJob job = new();
    private long generation;
    public string Endpoint { get; private set; } = "";
    public bool Running => server is { HasExited: false } && client != null;
    public bool IsIndexing => indexer is { HasExited: false };
    public event Action<string>? Progress;
    public int NetworkSearches { get; private set; }
    public EngineService(string executable, string root) { this.executable = executable; this.root = root; }

    private ProcessStartInfo StartInfo(params string[] arguments)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
        foreach (var name in info.Environment.Keys.Where(k => k.StartsWith("XERJ_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("JEV_", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(name);
        info.Environment["RUST_LOG"] = "warn";
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        return info;
    }
    private void Log(string? value)
    {
        if (value is null) return;
        lock (diagnostics) { diagnostics.Enqueue(value); while (diagnostics.Count > 60) diagnostics.Dequeue(); }
    }
    public string Diagnostics { get { lock(diagnostics) return string.Join(Environment.NewLine, diagnostics); } }

    public async Task StartAsync(CancellationToken cancellation = default)
    {
        if (Running) return;
        if(shutdown.IsCancellationRequested) { shutdown.Dispose(); shutdown = new(); }
        if (!File.Exists(executable)) throw new FileNotFoundException("The bundled XERJ engine is missing. Reinstall Local Search Studio.");
        if (!System.Runtime.Intrinsics.X86.Avx2.IsSupported) throw new PlatformNotSupportedException("This XERJ build requires a 64-bit Intel or AMD processor with AVX2 support (typically 2013 or newer).");
        Directory.CreateDirectory(root);
        // User-only state, including engine API key; never placed in a URL or command line.
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(root).SetAccessControl(security);
        try { ownership = new FileStream(System.IO.Path.Combine(root, "engine.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new IOException("Local Search Studio is already open. Switch to its existing window."); }
        try
        {
            // XERJ's incremental journal binds to its endpoint. Keep assigned ports
            // stable across restarts so Refresh can reuse that journal safely.
            var ports = new List<TcpListener>();
            var portFile = System.IO.Path.Combine(root,"ports.json");
            var saved = File.Exists(portFile) ? JsonSerializer.Deserialize<int[]>(File.ReadAllText(portFile)) : null;
            if(saved != null && (saved.Length != 3 || saved.Any(p=>p<1024 || p>65535) || saved.Distinct().Count()!=3)) throw new IOException("The saved local ports are invalid. Restore the app data backup or contact support.");
            int[] selected;
            try
            {
                for(var i = 0; i < 3; i++) { var listener = new TcpListener(IPAddress.Loopback, saved?[i] ?? 0); listener.Start(); ports.Add(listener); }
                selected = ports.Select(p => ((IPEndPoint)p.LocalEndpoint).Port).ToArray();
                if(saved is null) File.WriteAllText(portFile,JsonSerializer.Serialize(selected));
            }
            catch(SocketException ex) { throw new IOException("Another program is using this app’s saved local port. Close other copies of Local Search Studio, then retry.",ex); }
            finally { foreach(var listener in ports)listener.Stop(); }
            var data = System.IO.Path.Combine(root, "data").Replace('\\', '/');
            var config = $"[server]\nbind_address = \"127.0.0.1\"\nes_compat_port = {selected[0]}\nrest_port = {selected[1]}\ngrpc_port = {selected[2]}\ndata_dir = {JsonSerializer.Serialize(data)}\n[auth]\nenabled = true\n[tls]\nenabled = false\n[rerank]\nenabled = false\n";
            var configPath = System.IO.Path.Combine(root, "desktop.toml");
            await File.WriteAllTextAsync(configPath, config, cancellation);
            foreach(var p in ports) p.Stop();
            Endpoint = "http://127.0.0.1:" + selected[0];
            server = new Process { StartInfo = StartInfo("--config", configPath, "--embed-mode", "lexical"), EnableRaisingEvents = true };
            server.OutputDataReceived += (_, e) => Log(e.Data);
            server.ErrorDataReceived += (_, e) => Log(e.Data);
            server.Start(); server.BeginOutputReadLine(); server.BeginErrorReadLine();
            job.Add(server);
            client = new HttpClient(new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromSeconds(3) }) { BaseAddress = new Uri(Endpoint), Timeout = TimeSpan.FromSeconds(30) };
            var keyPath = System.IO.Path.Combine(root, "data", "admin.key");
            for(var i = 0; i < 150; i++)
            {
                cancellation.ThrowIfCancellationRequested();
                if (server.HasExited) throw new IOException("The local engine could not start. " + Diagnostics);
                if (File.Exists(keyPath))
                {
                    var key = (await File.ReadAllTextAsync(keyPath, cancellation)).Trim();
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("ApiKey", key);
                    try { using var response = await client.GetAsync("/", cancellation); if(response.IsSuccessStatusCode) return; } catch(HttpRequestException) { }
                }
                await Task.Delay(100, cancellation);
            }
            throw new TimeoutException("The local search engine took too long to start. Close the app and try again.");
        }
        catch { await StopAsync(); throw; }
    }

    public async Task<IndexResult> IndexAsync(Library library, bool dryRun, CancellationToken cancellation, bool approved = false)
    {
        await indexing.WaitAsync(cancellation);
        try
        {
            if (!Running) throw new InvalidOperationException("The search engine is not running.");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, shutdown.Token);
            cancellation = linked.Token;
            cancellation.ThrowIfCancellationRequested();
            var state = System.IO.Path.Combine(root, "journals", library.Id);
            var info = StartInfo("autoindex", library.Path, "--url", Endpoint, "--prefix", library.Prefix, "--state-dir", state, "--no-semantic", "--no-graph", "--progress", "plain", "--progress-interval", "1", "--json", "--max-minutes", "10");
            if (dryRun) info.ArgumentList.Add("--dry-run");
            if (approved) { info.ArgumentList.Add("--approve"); info.ArgumentList.Add("proceed"); }
            info.Environment["XERJ_API_KEY"] = client!.DefaultRequestHeaders.Authorization!.Parameter;
            var output = new StringBuilder();
            var watch = Stopwatch.StartNew();
            using var process = new Process { StartInfo = info };
            process.OutputDataReceived += (_, e) => { if(e.Data != null) lock(output) { if(output.Length < 2_000_000) output.AppendLine(e.Data); } };
            process.ErrorDataReceived += (_, e) => { if(e.Data is not null) { Log(e.Data); Progress?.Invoke(e.Data); } };
            process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
            job.Add(process); indexer = process;
            Interlocked.Increment(ref generation); ClearCache();
            try { await process.WaitForExitAsync(cancellation); process.WaitForExit(); }
            catch(OperationCanceledException) { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); throw; }
            finally { indexer = null; Interlocked.Increment(ref generation); ClearCache(); }
            return new(process.ExitCode, output.ToString(), watch.Elapsed.TotalSeconds);
        }
        finally { indexing.Release(); }
    }

    public void ClearCache() { lock(cache) { cache.Clear(); cacheOrder.Clear(); } }
    public async Task<SearchPage> SearchAsync(string query, IReadOnlyList<Library> libraries, string? filter, CancellationToken cancellation, bool useCache = true)
    {
        if (!Running) throw new InvalidOperationException("The local search engine is not running.");
        var selected = libraries.Where(l => filter is null || l.Id == filter).ToArray();
        if(selected.Length == 0 || string.IsNullOrWhiteSpace(query)) return new([], 0, 0);
        var version = Interlocked.Read(ref generation);
        var key = version + "|" + string.Join(',', selected.Select(l => l.Id)) + "|" + query;
        if(useCache && !IsIndexing) lock(cache) if(cache.TryGetValue(key, out var prior)) return prior with { Cached = true };
        var body = new { size = 60, track_total_hits = true, _source = new[] { "title", "name", "body", "text", "content", "code", "message", "ax_path", "path", "file_path" }, query = new { simple_query_string = new { query, fields = new[] { "*" }, default_operator = "and", lenient = true } } };
        var indices = string.Join(',', selected.Select(l => l.Prefix + "-*"));
        var timer = Stopwatch.StartNew();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/{indices}/_search?ignore_unavailable=true&allow_no_indices=true") { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        NetworkSearches++;
        using var response = await client!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        using var bounded = new MemoryStream();
        var buffer = new byte[16_384];
        int read;
        while((read = await stream.ReadAsync(buffer,cancellation)) > 0)
        {
            if(bounded.Length + read > 8_000_000) throw new IOException("These results are too large to preview together. Try a more specific phrase or select one folder.");
            bounded.Write(buffer,0,read);
        }
        var json = Encoding.UTF8.GetString(bounded.GetBuffer(),0,(int)bounded.Length);
        if(!response.IsSuccessStatusCode) throw new IOException("Search failed (" + (int)response.StatusCode + "). Try a shorter phrase or refresh the folder.");
        using var document = JsonDocument.Parse(json);
        var result = document.RootElement;
        if(result.TryGetProperty("_shards", out var shards) && shards.TryGetProperty("failed", out var failed) && failed.GetInt32() > 0) throw new IOException("Some indexes could not be searched. Refresh the folder and try again.");
        var hits = result.GetProperty("hits");
        var total = hits.GetProperty("total");
        var rows = new List<SearchHit>();
        foreach(var hit in hits.GetProperty("hits").EnumerateArray())
        {
            var index = hit.GetProperty("_index").GetString()!;
            var library = selected.FirstOrDefault(l => index.StartsWith(l.Prefix + "-", StringComparison.Ordinal));
            if(library is null) continue;
            var source = hit.GetProperty("_source");
            var relative = GetString(source, "ax_path", "path", "file_path");
            var full = StudioStore.SafeResultPath(library, relative);
            if(full is null) continue;
            var text = GetString(source, "body", "text", "content", "code", "message");
            if(string.IsNullOrWhiteSpace(text)) text = string.Join(Environment.NewLine, source.EnumerateObject().Where(p => !p.Name.StartsWith("ax_")).Select(p => p.Name + ": " + p.Value));
            if(text.Length > 32_000) text = text[..32_000] + "\n[Preview limited to 32,000 characters]";
            rows.Add(new(GetString(source, "title", "name") is { Length: > 0 } title ? title : System.IO.Path.GetFileName(full), full, library.Name, text, index, hit.GetProperty("_id").GetString()!, library.Id));
        }
        var page = new SearchPage(rows, total.ValueKind == JsonValueKind.Number ? total.GetInt64() : total.GetProperty("value").GetInt64(), timer.Elapsed.TotalMilliseconds);
        // Do not cache a read that overlaps a committed index generation.
        if(useCache && !IsIndexing && version == Interlocked.Read(ref generation)) lock(cache)
        {
            if(!cache.ContainsKey(key)) cacheOrder.Enqueue(key);
            cache[key] = page;
            while(cacheOrder.Count > 24) cache.Remove(cacheOrder.Dequeue());
        }
        return page;
    }
    private static string GetString(JsonElement value, params string[] names)
    {
        foreach(var name in names) if(value.TryGetProperty(name, out var text) && text.ValueKind == JsonValueKind.String) return text.GetString() ?? "";
        return "";
    }
    public async Task StopAsync()
    {
        await stopping.WaitAsync();
        shutdown.Cancel();
        await indexing.WaitAsync();
        try
        {
        if(server is { HasExited: false })
        {
            // Flush acknowledged writes before terminating our private child; WAL handles crash recovery.
            try { if(client != null) { using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)); using var response = await client.PostAsync("/_flush", null, cts.Token); response.EnsureSuccessStatusCode(); } }
            catch(Exception e) { Log("Shutdown flush: " + e.Message); }
            if(!server.HasExited) server.Kill(true);
            await server.WaitForExitAsync();
        }
        server?.Dispose(); server = null;
        client?.Dispose(); client = null;
        ownership?.Dispose(); ownership = null;
        ClearCache();
        }
        finally { indexing.Release(); stopping.Release(); }
    }
    public async ValueTask DisposeAsync() { await StopAsync(); job.Dispose(); }
}

// Windows releases this handle if the desktop process crashes: no orphan engine
// or indexer remains listening after the owning app has gone away.
internal sealed class ChildProcessJob : IDisposable
{
    private readonly Microsoft.Win32.SafeHandles.SafeFileHandle handle;
    public ChildProcessJob()
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        if(handle.IsInvalid) throw new System.ComponentModel.Win32Exception();
        var limits = new ExtendedLimit { Basic = new BasicLimit { LimitFlags = 0x2000 } };
        if(!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimit>())) throw new System.ComponentModel.Win32Exception();
    }
    public void Add(Process process)
    {
        if(!AssignProcessToJobObject(handle, process.Handle)) { if(!process.HasExited)process.Kill(true); throw new System.ComponentModel.Win32Exception(); }
    }
    public void Dispose() => handle.Dispose();
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimit { public long PerProcessUserTimeLimit,PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinimumWorkingSetSize,MaximumWorkingSetSize; public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass,SchedulingClass; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount,WriteOperationCount,OtherOperationCount,ReadTransferCount,WriteTransferCount,OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimit { public BasicLimit Basic; public IoCounters Io; public UIntPtr ProcessMemoryLimit,JobMemoryLimit,PeakProcessMemoryUsed,PeakJobMemoryUsed; }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateJobObject(IntPtr attributes,string? name);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool SetInformationJobObject(Microsoft.Win32.SafeHandles.SafeFileHandle job,int informationClass,ref ExtendedLimit info,uint size);
    [DllImport("kernel32.dll",SetLastError=true)] private static extern bool AssignProcessToJobObject(Microsoft.Win32.SafeHandles.SafeFileHandle job,IntPtr process);
}
