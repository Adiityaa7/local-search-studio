using System.Diagnostics;
using System.Net;
using System.Text.Json;
using LocalSearchStudio;

var executable = Path.GetFullPath(args[0]);
var root = Path.GetFullPath(args[1]);
Directory.CreateDirectory(root);
var corpus = Path.Combine(root,"fixture"); Directory.CreateDirectory(corpus);
await File.WriteAllTextAsync(Path.Combine(corpus,"authentication.md"), "# Authentication guide\nAuthentication uses short-lived access tokens. Rotate credentials on expiration.\nThe unique test term is ambercobalt.");
await File.WriteAllTextAsync(Path.Combine(corpus,"cache.md"), "# Cache design\nCache invalidation follows each completed indexing generation.\nAn LRU cache avoids repeated search requests.");
await File.WriteAllTextAsync(Path.Combine(corpus,".env"), "SECRET=shouldneverappear");
var library = StudioStore.NewLibrary(corpus);
Check(StudioStore.SafeResultPath(library,"../outside.txt") is null,"path traversal refused");
Check(StudioStore.SafeResultPath(library,"authentication.md") == Path.Combine(corpus,"authentication.md"),"selected-root resolution");
var store = new StudioStore(Path.Combine(root,"state")); store.Save([library]); Check(store.Load().Single().Id == library.Id,"atomic library persistence");
var timings = new Dictionary<string,object>();
await using(var engine = new EngineService(executable,store.Root))
{
 var boot = Stopwatch.StartNew(); await engine.StartAsync(); timings["startup_ms"]=boot.Elapsed.TotalMilliseconds;
 using var unauthenticated = new HttpClient(); var denied=await unauthenticated.GetAsync(engine.Endpoint+"/_cat/indices"); Check(denied.StatusCode==HttpStatusCode.Unauthorized,"unauthenticated API refused");
 engine.Progress += line => {if(line.StartsWith("xerj-bar ")) Console.WriteLine(line);};
 var dry=await engine.IndexAsync(library,true,CancellationToken.None); Check(dry.Success,"folder dry run");
 var first=await engine.IndexAsync(library,false,CancellationToken.None); Check(first.Success,"first index: "+first.Output); timings["initial_index_seconds"]=first.Seconds;
 var page=await engine.SearchAsync("ambercobalt",[library],null,CancellationToken.None); Check(page.Hits.Any(h=>h.Text.Contains("ambercobalt")),"content search retrieves passage");
 var requests=engine.NetworkSearches; var watch=Stopwatch.StartNew(); var cached=await engine.SearchAsync("ambercobalt",[library],null,CancellationToken.None); timings["cache_hit_ms"]=watch.Elapsed.TotalMilliseconds; Check(cached.Cached && engine.NetworkSearches==requests,"repeat query makes no HTTP request");
 var hidden=await engine.SearchAsync("shouldneverappear",[library],null,CancellationToken.None); Check(hidden.Hits.Count==0,"hidden secrets excluded");
 var samples=new List<double>(); for(var i=0;i<20;i++) {var s=await engine.SearchAsync("authentication",[library],null,CancellationToken.None,false); samples.Add(s.Milliseconds);} samples.Sort(); timings["uncached_search_median_ms"]=samples[10]; timings["uncached_search_p95_ms"]=samples[18];
 var unchanged=await engine.IndexAsync(library,false,CancellationToken.None); Check(unchanged.Success,"unchanged incremental refresh"); timings["unchanged_refresh_seconds"]=unchanged.Seconds;
 await File.WriteAllTextAsync(Path.Combine(corpus,"authentication.md"),"# Updated authentication\nReplacement term is violetquartz. Old content was removed.");
 var changed=await engine.IndexAsync(library,false,CancellationToken.None); Check(changed.Success,"changed-file refresh");
 var old=await engine.SearchAsync("ambercobalt",[library],null,CancellationToken.None); var fresh=await engine.SearchAsync("violetquartz",[library],null,CancellationToken.None); Check(old.Hits.Count==0 && fresh.Hits.Count>0,"cache invalidated and replacement searchable");
 File.Delete(Path.Combine(corpus,"cache.md")); var deleted=await engine.IndexAsync(library,false,CancellationToken.None); Check(deleted.Success,"deleted-file refresh"); Check((await engine.SearchAsync("LRU",[library],null,CancellationToken.None)).Hits.Count==0,"deleted file removed from search");
 var endpoint=engine.Endpoint; await engine.StopAsync(); await engine.StartAsync(); Check(engine.Endpoint==endpoint,"endpoint remains stable across restart"); Check((await engine.SearchAsync("violetquartz",[library],null,CancellationToken.None)).Hits.Count>0,"index survives engine restart");
 var afterRestart=await engine.IndexAsync(library,false,CancellationToken.None); Check(afterRestart.Success,"incremental refresh after engine restart: "+engine.Diagnostics);
 using var cancelled=new CancellationTokenSource(); cancelled.Cancel(); try {await engine.SearchAsync("cancelled",[library],null,cancelled.Token); throw new Exception("Cancellation ignored");} catch(OperationCanceledException) {Console.WriteLine("PASS cancelled search");}
 var cancelCorpus=Path.Combine(root,"cancel-fixture"); Directory.CreateDirectory(cancelCorpus);
 for(var i=0;i<200;i++) await File.WriteAllTextAsync(Path.Combine(cancelCorpus,$"document-{i}.md"),$"# Document {i}\n"+string.Join(' ',Enumerable.Repeat("Cancellation shutdown indexing fixture",200)));
 var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
 engine.Progress += _=>started.TrySetResult();
 var pending=engine.IndexAsync(StudioStore.NewLibrary(cancelCorpus),false,CancellationToken.None);
 await started.Task.WaitAsync(TimeSpan.FromSeconds(20));
 await engine.StopAsync().WaitAsync(TimeSpan.FromSeconds(25));
 try {await pending;} catch(OperationCanceledException) {Console.WriteLine("PASS shutdown cancels active indexer");}
 Check(!engine.Running && !engine.IsIndexing,"shutdown leaves no active child");
}
await File.WriteAllTextAsync(Path.Combine(root,"test-results.json"),JsonSerializer.Serialize(new {passed=true,timings},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine(JsonSerializer.Serialize(timings));
static void Check(bool ok,string label) {if(!ok)throw new Exception("FAIL "+label);Console.WriteLine("PASS "+(label.Length>160?label[..160]:label));}
