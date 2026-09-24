using System.Diagnostics;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace CodexRadarHost;

public partial class App : Application
{
    private const string MutexName = "Local\\CodexRadarTrafficMonitor.SingleInstance";
    private const string ShowEventName = "Local\\CodexRadarTrafficMonitor.ShowWindow";
    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private RadarCoordinator? _coordinator;
    private CancellationTokenSource? _parentWatchCancellation;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var showRequested = e.Args.Contains("--show", StringComparer.OrdinalIgnoreCase);
        _instanceMutex = new Mutex(true, MutexName, out _ownsMutex);
        if (!_ownsMutex)
        {
            if (showRequested) SignalExistingWindow();
            Shutdown();
            return;
        }

        try
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            var store = new RadarCacheStore();
            _coordinator = new RadarCoordinator(store, new RadarService());
            var window = new MainWindow(_coordinator);
            MainWindow = window;
            _showWait = ThreadPool.RegisterWaitForSingleObject(
                _showEvent,
                (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => ShowWindow(window))),
                null,
                Timeout.Infinite,
                executeOnlyOnce: false);

            if (showRequested) ShowWindow(window);
            _coordinator.Start();
            StartParentWatch(e.Args);
        }
        catch (Exception ex)
        {
            RadarLog.Write("启动失败", ex);
            MessageBox.Show($"GPT 雷达助手启动失败：{ex.Message}", "GPT 雷达", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static void ShowWindow(Window window)
    {
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private static void SignalExistingWindow()
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            try
            {
                using var signal = EventWaitHandle.OpenExisting(ShowEventName);
                signal.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException) { Thread.Sleep(50); }
        }
    }

    private void StartParentWatch(string[] args)
    {
        var argument = args.FirstOrDefault(value => value.StartsWith("--parent-pid=", StringComparison.OrdinalIgnoreCase));
        if (argument is null || !int.TryParse(argument["--parent-pid=".Length..], out var parentId)) return;
        _parentWatchCancellation = new CancellationTokenSource();
        var token = _parentWatchCancellation.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                using var parent = Process.GetProcessById(parentId);
                await parent.WaitForExitAsync(token).ConfigureAwait(false);
                await Dispatcher.InvokeAsync(StopApplication);
            }
            catch (ArgumentException) { await Dispatcher.InvokeAsync(StopApplication); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { RadarLog.Write("TrafficMonitor 生命周期监视失败", ex); }
        }, token);
    }

    private void StopApplication()
    {
        if (MainWindow is MainWindow window) window.BeginShutdown();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _parentWatchCancellation?.Cancel();
        _showWait?.Unregister(null);
        _showEvent?.Dispose();
        _coordinator?.Dispose();
        if (_ownsMutex) _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
