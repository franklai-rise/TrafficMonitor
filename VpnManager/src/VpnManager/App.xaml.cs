using System.Windows;
using System.Diagnostics;
using System.IO;

namespace VpnManager;
public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private EventWaitHandle? _activationEvent;
    private bool _ownsMutex;
    private bool _isExiting;
    protected override void OnStartup(StartupEventArgs e)
    {
        var installedDirectory = Path.Combine(Environment.ExpandEnvironmentVariables("%USERPROFILE%"), "AppData", "Local", "VpnManager");
        var currentDirectory = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(currentDirectory, installedDirectory, StringComparison.OrdinalIgnoreCase))
        {
            var installedExe = Path.Combine(installedDirectory, "VpnManager.exe");
            if (File.Exists(installedExe)) Process.Start(new ProcessStartInfo(installedExe) { UseShellExecute = true });
            Shutdown(); return;
        }
        _mutex = new Mutex(true, "Local\\VpnManager.SingleInstance", out var first);
        _ownsMutex = first;
        if (!first)
        {
            try { using var existing = EventWaitHandle.OpenExisting("Local\\VpnManager.Activate"); existing.Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
            Shutdown(); return;
        }
        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\VpnManager.Activate");
        var startupDirect = e.Args.Any(arg => string.Equals(arg, "--startup-direct", StringComparison.OrdinalIgnoreCase));
        base.OnStartup(e); new MainWindow(startupDirect).Show(); ListenForActivationRequests();
    }
    private void ListenForActivationRequests()
    {
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                while (_activationEvent?.WaitOne() == true)
                {
                    if (_isExiting) return;
                    Dispatcher.BeginInvoke(() => { if (Current.MainWindow is MainWindow window) window.ShowFromActivationRequest(); });
                }
            }
            catch (ObjectDisposedException) { }
        });
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _isExiting = true;
        _activationEvent?.Set();
        _activationEvent?.Dispose();
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
