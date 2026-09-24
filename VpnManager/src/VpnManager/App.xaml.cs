using System.Diagnostics;
using System.IO;
using System.Reflection;
using MessageBox = System.Windows.MessageBox;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using VpnManager.Core;

namespace VpnManager;
public partial class App : System.Windows.Application
{
    public static string BuildVersion => typeof(App).Assembly.GetName().Version?.ToString() ?? "unknown";
    private Mutex? _mutex;
    private EventWaitHandle? _activationEvent, _shutdownEvent;
    private RegisteredWaitHandle? _activationWait, _shutdownWait;
    private bool _ownsMutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        OperationLog.Configure(VpnPaths.Default.StateDirectory);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => OperationLog.Write($"未处理异常：{args.ExceptionObject}");
        DispatcherUnhandledException += (_, args) => OperationLog.Write($"界面异常：{args.Exception}");
        try
        {
            if (e.Args.Length == 2 && e.Args[0] == "--diagnose")
            {
                var output = Path.GetFullPath(e.Args[1]);
                var state = Path.Combine(VpnPaths.Default.StateDirectory, "vpn-status.json");
                File.WriteAllText(output, JsonSerializer.Serialize(new { Version = BuildVersion, Executable = Environment.ProcessPath, StatePath = state, ProcessId = Environment.ProcessId, Package = PackageName(), ObservedAt = DateTimeOffset.Now }, new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(); return;
            }
            if (e.Args.Contains("--shutdown")) { Signal("Local\\VpnManager.Shutdown"); Shutdown(); return; }
            _mutex = new Mutex(true, "Local\\VpnManager.SingleInstance", out _ownsMutex);
            if (!_ownsMutex)
            {
                AllowSetForegroundWindow(-1);
                if (!Signal("Local\\VpnManager.Activate")) MessageBox.Show("管理器正在启动，请稍后再打开。", "VPN 管理器");
                Shutdown(); return;
            }
            _activationEvent = new(false, EventResetMode.AutoReset, "Local\\VpnManager.Activate");
            _shutdownEvent = new(false, EventResetMode.AutoReset, "Local\\VpnManager.Shutdown");
            _activationWait = ThreadPool.RegisterWaitForSingleObject(_activationEvent, (_, _) => PostToWindow(w => w.ShowFromActivationRequest()), null, Timeout.Infinite, false);
            _shutdownWait = ThreadPool.RegisterWaitForSingleObject(_shutdownEvent, (_, _) => PostToWindow(w => w.RequestExit()), null, Timeout.Infinite, false);
            base.OnStartup(e);
            MainWindow = new MainWindow();
            MainWindow.Show();
        }
        catch (Exception ex)
        {
            OperationLog.Write($"启动失败：{ex}");
            MessageBox.Show($"管理器启动失败：{ex.Message}\n日志：{OperationLog.Path}", "VPN 管理器");
            Shutdown(1);
        }
    }
    private void PostToWindow(Action<MainWindow> action)
    {
        if (Dispatcher.HasShutdownStarted) return;
        Dispatcher.BeginInvoke(() => { if (MainWindow is MainWindow window) action(window); });
    }
    private static bool Signal(string name)
    {
        for (var i = 0; i < 10; i++)
        {
            try { using var signal = EventWaitHandle.OpenExisting(name); return signal.Set(); }
            catch (WaitHandleCannotBeOpenedException) { Thread.Sleep(50); }
        }
        return false;
    }
    private static string? PackageName()
    {
        var length = 0;
        return GetCurrentPackageFullName(ref length, IntPtr.Zero) == 15700 ? null : "packaged-process";
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetCurrentPackageFullName(ref int length, IntPtr name);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
    protected override void OnExit(ExitEventArgs e)
    {
        _activationWait?.Unregister(null); _shutdownWait?.Unregister(null);
        _activationEvent?.Dispose(); _shutdownEvent?.Dispose();
        if (_ownsMutex) _mutex?.ReleaseMutex();
        _mutex?.Dispose(); base.OnExit(e);
    }
}
