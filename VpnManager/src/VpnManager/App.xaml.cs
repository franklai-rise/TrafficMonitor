using System.Windows;

namespace VpnManager;
public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "Local\\VpnManager.SingleInstance", out var first);
        if (!first) { Shutdown(); return; }
        base.OnStartup(e); new MainWindow().Show();
    }
    protected override void OnExit(ExitEventArgs e) { _mutex?.ReleaseMutex(); _mutex?.Dispose(); base.OnExit(e); }
}
