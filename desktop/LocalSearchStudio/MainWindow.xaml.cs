using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace LocalSearchStudio;
public partial class MainWindow : Window
{
    private readonly StudioStore store;
    private readonly EngineService engine;
    private readonly string[] arguments;
    private List<Library> libraries = [];
    private CancellationTokenSource? searchCancellation;
    private CancellationTokenSource? indexCancellation;
    private readonly CancellationTokenSource lifetime = new();
    private bool ready, closing, allowClose, refreshing;
    private long searchSerial;
    private SearchHit? selected;
    private sealed record FolderOption(string? Id, string Name);

    public MainWindow(string root, string[] args)
    {
        InitializeComponent();
        arguments = args;
        store = new(root);
        engine = new(System.IO.Path.Combine(AppContext.BaseDirectory, "engine", "xerj.exe"), root);
        engine.Progress += line => Dispatcher.BeginInvoke(() => ShowProgress(line));
    }
    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            libraries = store.Load(); UpdateLibraries();
            await engine.StartAsync(lifetime.Token);
            ready = true; AddButton.IsEnabled = true; Activity.Visibility = Visibility.Collapsed;
            Status.Text = "●  Search is ready · Files and indexes stay on this PC";
            var demo = Array.IndexOf(arguments, "--demo-folder");
            if(demo >= 0 && demo + 1 < arguments.Length)
            {
                var library = StudioStore.NewLibrary(arguments[demo + 1]);
                if(libraries.All(l => l.Id != library.Id)) { libraries.Add(library); store.Save(libraries); UpdateLibraries(); }
                await RunIndex(library);
                Query.Text = "authentication";
                await RunSearch(false);
                var capture = Array.IndexOf(arguments, "--capture");
                if(capture >= 0 && capture + 1 < arguments.Length)
                {
                    await Dispatcher.InvokeAsync(() => { UpdateLayout(); var visual = (FrameworkElement)Content; var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(arguments[capture + 1]); encoder.Save(stream); }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                }
            }
        }
        catch(OperationCanceledException) { }
        catch(Exception ex) { ShowFailure(ex.Message); }
    }
    private void UpdateLibraries()
    {
        refreshing = true;
        var prior = (FolderFilter.SelectedItem as FolderOption)?.Id;
        LibrariesList.ItemsSource = libraries.ToArray();
        var options = new List<FolderOption> { new(null, "All folders") };
        options.AddRange(libraries.Select(l => new FolderOption(l.Id, l.Name)));
        FolderFilter.ItemsSource = options;
        FolderFilter.SelectedItem = options.FirstOrDefault(o => o.Id == prior) ?? options[0];
        SidebarCount.Text = libraries.Count == 1 ? "1 folder" : $"{libraries.Count} folders";
        refreshing = false;
    }
    private async void AddFolder(object sender, RoutedEventArgs e)
    {
        if(!ready || indexCancellation != null) return;
        var dialog = new OpenFolderDialog { Title = "Choose a folder to make searchable", Multiselect = false };
        if(dialog.ShowDialog(this) == true) await AddPath(dialog.FolderName);
    }
    private async void FolderDrop(object sender, DragEventArgs e)
    {
        if(!ready || indexCancellation != null) return;
        if(e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length == 1 && Directory.Exists(paths[0])) await AddPath(paths[0]);
        else Status.Text = "Drop one folder to add it to your library.";
    }
    private async Task AddPath(string path)
    {
        try
        {
            var library = StudioStore.NewLibrary(path);
            if(library.Path.StartsWith(store.Root, StringComparison.OrdinalIgnoreCase) || store.Root.StartsWith(library.Path + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            { Status.Text = "Choose a project or document folder outside the app’s own data folder."; return; }
            if(libraries.Any(l => l.Id == library.Id)) { Status.Text = "That folder is already in My folders. Choose Refresh to update it."; ShowLibraries(this, new RoutedEventArgs()); return; }
            libraries.Add(library); store.Save(libraries); UpdateLibraries();
            await RunIndex(library);
        }
        catch(Exception ex) { ShowFailure(ex.Message); }
    }
    private async void RefreshFolder(object sender, RoutedEventArgs e)
    {
        if(indexCancellation is not null) { Status.Text = "Wait for the current folder or choose Stop indexing."; return; }
        if(sender is Button { Tag: Library library }) await RunIndex(library);
    }
    private async Task RunIndex(Library library)
    {
        if(!ready || indexCancellation != null) return;
        indexCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = indexCancellation.Token;
        AddButton.IsEnabled = false; CancelButton.Visibility = Visibility.Visible;
        Activity.Visibility = Visibility.Visible; Activity.IsIndeterminate = true;
        var state = "Interrupted · refresh to resume";
        try
        {
            Status.Text = $"Checking {library.Name}…";
            var estimate = await engine.IndexAsync(library, true, token);
            if(!estimate.Success && estimate.ExitCode != 4) throw new IOException($"The folder could not be scanned (exit {estimate.ExitCode}). " + ShortError(estimate.Output) + "\n" + ShortError(engine.Diagnostics));
            Status.Text = $"Indexing {library.Name}…";
            var result = await engine.IndexAsync(library, false, token);
            if(result.ExitCode == 4)
            {
                Activity.Visibility = Visibility.Collapsed;
                if(MessageBox.Show(this, "This folder may take more than 10 minutes to index.\n\n" + ShortError(result.Output) + "\n\nContinue? You can stop and resume later.", "Larger folder", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                Activity.Visibility = Visibility.Visible;
                result = await engine.IndexAsync(library, false, token, approved:true);
            }
            if(!result.Success) throw new IOException("Indexing did not finish. " + ShortError(result.Output) + "\n" + ShortError(engine.Diagnostics));
            state = result.ExitCode == 3 ? "Indexed with skipped items" : "Indexed";
            libraries = libraries.Select(l => l.Id == library.Id ? l with { Updated = DateTime.Now, State = state } : l).ToList();
            store.Save(libraries); UpdateLibraries();
            Status.Text = $"{library.Name} · {state} in {result.Seconds:F1}s · Ready to search";
            if(result.ExitCode == 3) ShowText("Indexing details", "Some files or datasets were skipped. The available content remains searchable.\n\n" + result.Output + "\n\nRecent activity:\n" + engine.Diagnostics);
            await RunSearch(false);
        }
        catch(OperationCanceledException) { Status.Text = "Indexing stopped. Refresh this folder to resume; original files are unchanged."; }
        catch(Exception ex) { state = "Needs attention · refresh to retry"; ShowFailure(ex.Message); }
        finally
        {
            if(!state.StartsWith("Indexed")) { libraries = libraries.Select(l => l.Id == library.Id ? l with { State = state } : l).ToList(); store.Save(libraries); UpdateLibraries(); }
            indexCancellation?.Dispose(); indexCancellation = null;
            CancelButton.Visibility = Visibility.Collapsed; Activity.Visibility = Visibility.Collapsed;
            AddButton.IsEnabled = ready && !closing;
        }
    }
    private static string ShortError(string output) => output.Length <= 1800 ? output : output[^1800..];
    private void ShowProgress(string line)
    {
        if(indexCancellation is null || closing) return;
        if(line.StartsWith("xerj-bar ")) Status.Text = line[9..];
        if(line.StartsWith("xerj-progress "))
        {
            var phase = Regex.Match(line, @"phase=(\S+)").Groups[1].Value;
            var match = Regex.Match(line, @"pct=([0-9.]+)");
            if(double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var percent)) { Activity.IsIndeterminate = false; Activity.Value = percent; }
            else Activity.IsIndeterminate = true;
            if(!string.IsNullOrEmpty(phase)) Status.Text = "Indexing · " + phase.Replace('-', ' ') + (match.Success ? " · " + match.Groups[1].Value + "% of this step" : "…");
        }
    }
    private void StopIndexing(object sender, RoutedEventArgs e) { indexCancellation?.Cancel(); Status.Text = "Stopping indexing…"; }
    private async void QueryChanged(object sender, TextChangedEventArgs e)
    {
        if(QueryPlaceholder is not null) QueryPlaceholder.Visibility = string.IsNullOrEmpty(Query.Text) ? Visibility.Visible : Visibility.Collapsed;
        if(ready) await RunSearch(true);
    }
    private async void FilterChanged(object sender, SelectionChangedEventArgs e) { if(ready && !refreshing) await RunSearch(false); }
    private async Task RunSearch(bool debounce)
    {
        searchCancellation?.Cancel(); searchCancellation?.Dispose();
        searchCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = searchCancellation.Token;
        var serial = ++searchSerial;
        var text = Query.Text.Trim();
        if(text.Length == 0) { ResultList.ItemsSource = null; ResultsSummary.Text = "Ready when you are"; EmptyTitle.Text = libraries.Count == 0 ? "Start with a folder." : "What are you looking for?"; EmptyDescription.Text = libraries.Count == 0 ? "Choose a project, your notes, or a document folder. We’ll make its contents searchable on this PC." : "Type a few keywords. Put an exact phrase in quotes to narrow your search."; EmptyState.Visibility = Visibility.Visible; return; }
        try
        {
            if(debounce) await Task.Delay(220, token);
            ResultsSummary.Text = "Searching…";
            var result = await engine.SearchAsync(text, libraries, (FolderFilter.SelectedItem as FolderOption)?.Id, token);
            if(serial != searchSerial || token.IsCancellationRequested) return;
            ResultList.ItemsSource = result.Hits;
            ResultsSummary.Text = $"{result.Total:N0} matches · " + (result.Cached ? "cached" : $"{result.Milliseconds:F0} ms") + (result.Total > 60 ? " · showing first 60" : "");
            EmptyState.Visibility = result.Hits.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyTitle.Text = "No matches yet."; EmptyDescription.Text = "Try fewer words, choose another folder, or refresh your files in My folders.";
            if(result.Hits.Count > 0) ResultList.SelectedIndex = 0;
        }
        catch(OperationCanceledException) { }
        catch(Exception ex) { if(serial == searchSerial) { ResultsSummary.Text = "Search needs attention"; ShowFailure(ex.Message); } }
    }
    private void ResultSelected(object sender, SelectionChangedEventArgs e)
    {
        selected = ResultList.SelectedItem as SearchHit;
        PreviewTitle.Text = selected?.Title ?? "A closer look";
        PreviewPath.Text = selected?.Path ?? "Select a result to read its indexed text.";
        PreviewText.Text = selected?.Text ?? "";
        RevealButton.IsEnabled = selected != null; CopyButton.IsEnabled = selected != null;
    }
    private void RevealFile(object sender, RoutedEventArgs e)
    {
        if(selected is null) return;
        var library = libraries.FirstOrDefault(l => l.Id == selected.LibraryId);
        var path = library is null ? null : StudioStore.SafeResultPath(library, selected.Path);
        if(path is null || !File.Exists(path)) { Status.Text = "This file has moved or is unavailable. Refresh the folder to update the index."; return; }
        var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        info.ArgumentList.Add("/select,"); info.ArgumentList.Add(path); Process.Start(info);
    }
    private void CopyText(object sender, RoutedEventArgs e) { if(selected != null) { Clipboard.SetText(selected.Text); Status.Text = "Preview copied to clipboard."; } }
    private void ShowSearch(object sender, RoutedEventArgs e) { SearchView.Visibility = Visibility.Visible; SearchBar.Visibility = Visibility.Visible; LibraryView.Visibility = Visibility.Collapsed; PageTitle.Text = "Find your next answer."; PageSubtitle.Text = "Search across your code, notes, and documents."; SearchNav.Background = new SolidColorBrush(Color.FromRgb(44,57,81)); LibraryNav.Background = Brushes.Transparent; Query.Focus(); }
    private void ShowLibraries(object sender, RoutedEventArgs e) { SearchView.Visibility = Visibility.Collapsed; SearchBar.Visibility = Visibility.Collapsed; LibraryView.Visibility = Visibility.Visible; PageTitle.Text = "Your folders, organized."; PageSubtitle.Text = "Choose what’s searchable. Keep the rest private."; LibraryNav.Background = new SolidColorBrush(Color.FromRgb(44,57,81)); SearchNav.Background = Brushes.Transparent; }
    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if(Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.K) { ShowSearch(this, new RoutedEventArgs()); Query.SelectAll(); e.Handled = true; }
        if(Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O) { AddFolder(this, new RoutedEventArgs()); e.Handled = true; }
        if(e.Key == Key.Escape && Query.IsFocused) { Query.Clear(); e.Handled = true; }
    }
    private void ShowFailure(string message) { Status.Text = message.Replace('\n', ' '); Activity.Visibility = Visibility.Collapsed; Status.ToolTip = message; if(!closing) ShowText("Something needs attention", message); }
    private void ShowHelp(object sender, RoutedEventArgs e) => ShowText("How it works", "1. Add a folder\nChoose a project or a document folder, or drop it into this window. Only the folder you select is indexed. Hidden files and common build outputs are skipped by XERJ.\n\n2. Search your content\nType keywords, a function name, or a phrase in quotes. Search uses XERJ’s local keyword index; no AI model or cloud account is required. Up to 60 passages are shown per query.\n\n3. Keep it fresh\nAfter editing, adding, moving, or deleting files, open My folders and choose Refresh. Unchanged content is reused. You can stop an index job and resume it with Refresh.\n\nShortcuts\nCtrl+K: focus search\nCtrl+O: add a folder\nEscape: clear search\n\nPrivacy\nFiles, indexes, and folder settings remain on this PC. The desktop edition does not enable cloud reranking, analytics, or neural-model downloads. Search previews are indexed snapshots; refresh to see edits.\n\nApp data\n" + store.Root);
    private void ShowAbout(object sender, RoutedEventArgs e)
    {
        var dir = System.IO.Path.Combine(AppContext.BaseDirectory, "notices");
        ShowText("About & credits", "Local Search Studio 1.0.0\nIndependent desktop edition powered by XERJ 1.0.0-rc.95.\nNot affiliated with or endorsed by the XERJ project.\n\nXERJ\nCopyright 2026 The XERJ Authors\nThis product includes software developed by the XERJ project (https://xerj.org).\nSource: https://github.com/xerj-org/xerj\nEngine release: v1.0.0-rc.95 (unmodified).\n\nDesktop additions: native Windows interface, authenticated local-engine management, folder library, incremental keyword indexing, cancellable search, bounded previews, and session query caching. Added 15 October 2026.\n\nLicensed under Apache License 2.0.\nThe full license, upstream NOTICE, and dependency notices are included below and in the installed notices folder.\n\n" + (File.Exists(System.IO.Path.Combine(dir,"XERJ-LICENSE.txt")) ? File.ReadAllText(System.IO.Path.Combine(dir,"XERJ-LICENSE.txt")) : "License file missing"), dir);
    }
    private void ShowText(string title, string text, string? licensesFolder = null)
    {
        var dialog = new Window { Title = title, Owner = this, Width = 720, Height = 580, MinWidth = 480, MinHeight = 320, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = new SolidColorBrush(Color.FromRgb(247,248,252)) };
        var panel = new DockPanel { Margin = new Thickness(26) };
        var heading = new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,0,0,20) }; DockPanel.SetDock(heading,Dock.Top); panel.Children.Add(heading);
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,18,0,0), IsCancel = true }; close.Click += (_,_)=>dialog.Close(); DockPanel.SetDock(close,Dock.Bottom); panel.Children.Add(close);
        if(licensesFolder != null) { var licenses = new Button { Content = "Open all licenses and source notices", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0,12,0,0) }; licenses.Click += (_,_)=>Process.Start(new ProcessStartInfo(licensesFolder) { UseShellExecute = true }); DockPanel.SetDock(licenses,Dock.Bottom); panel.Children.Add(licenses); }
        panel.Children.Add(new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(16), BorderThickness = new Thickness(0), FontSize = 13 }); dialog.Content = panel; dialog.ShowDialog();
    }
    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if(allowClose) return;
        e.Cancel = true; if(closing) return;
        closing = true; IsEnabled = false; Status.Text = "Saving indexes and closing…";
        lifetime.Cancel(); indexCancellation?.Cancel(); searchCancellation?.Cancel();
        try { await engine.DisposeAsync(); }
        finally { allowClose = true; lifetime.Dispose(); Close(); }
    }
}

