using System.IO;
using System.Windows;
namespace LocalSearchStudio;
public partial class App : Application
{
    private Mutex? instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalSearchStudio");
        var test = Array.IndexOf(e.Args, "--data-dir");
        if(test >= 0 && test + 1 < e.Args.Length) root = System.IO.Path.GetFullPath(e.Args[test + 1]);
        var name = "Local\\LocalSearchStudio-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(root)))[..20];
        instance = new Mutex(true, name, out var created);
        if(!created) { MessageBox.Show("FolderHunt is already open. Switch to its existing window.", "FolderHunt"); Shutdown(); return; }
        DispatcherUnhandledException += (_, args) => { MessageBox.Show(args.Exception.Message, "FolderHunt", MessageBoxButton.OK, MessageBoxImage.Error); args.Handled = true; };
        var window = new MainWindow(root, e.Args);
        MainWindow = window; window.Show();
    }
    protected override void OnExit(ExitEventArgs e) { instance?.Dispose(); base.OnExit(e); }
}
