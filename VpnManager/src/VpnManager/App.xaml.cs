using System.Windows;
using System.Diagnostics;
using System.IO;

namespace VpnManager;
public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
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
        if (!first) { Shutdown(); return; }
        base.OnStartup(e); new MainWindow().Show();
    }
    protected override void OnExit(ExitEventArgs e) { _mutex?.ReleaseMutex(); _mutex?.Dispose(); base.OnExit(e); }
}
