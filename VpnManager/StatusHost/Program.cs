using System.Diagnostics;
using System.Net;
using VpnManager.Core;

var parentArgument = args.FirstOrDefault(value => value.StartsWith("--parent-pid=", StringComparison.Ordinal));
if (parentArgument is null || !int.TryParse(parentArgument[13..], out var parentPid) || parentPid <= 0) return;
using var mutex = new Mutex(true, "Local\\VpnStatusPlugin.Host", out var ownsMutex);
if (!ownsMutex) return;

var paths = VpnPaths.Default;
var publisher = new StatusPublisher(new WindowsSystemGateway(), paths, parentPid);
await publisher.RunAsync();

internal sealed class StatusPublisher
{
    private readonly WindowsSystemGateway _system;
    private readonly VpnPaths _paths;
    private readonly StatusCollector _collector;
    private readonly SnapshotStore _store;
    private readonly ClashControllerResolver _resolver;
    private readonly int _parentPid;
    private readonly object _detailsLock = new();
    private readonly string _preferencePath;
    private readonly EventWaitHandle _refresh = new(false, EventResetMode.AutoReset, "Local\\VpnStatusPlugin.Refresh");
    private (string? Ip, string? Country, string? Location, string? Node, string? NodeCountry, DateTimeOffset? CapturedAt) _details;
    private VpnMode _mode = VpnMode.Unknown;
    private long _generation;
    private DateTimeOffset _lastDetailsAttempt = DateTimeOffset.MinValue;
    private Task? _detailsTask;
    private string? _lastError;

    public StatusPublisher(WindowsSystemGateway system, VpnPaths paths, int parentPid)
    {
        _system = system;
        _paths = paths;
        _parentPid = parentPid;
        _collector = new(system, paths);
        _store = new(paths.StateDirectory);
        _resolver = new(paths.ClashConfig);
        _preferencePath = Path.Combine(paths.StateDirectory, "exit-ip-enabled.txt");
    }

    public async Task RunAsync()
    {
        try
        {
            while (ParentAlive())
            {
                try { Publish(); }
                catch (Exception ex) { _lastError = $"插件状态采集失败：{ex.GetType().Name}"; }
                if (_refresh.WaitOne(TimeSpan.FromSeconds(2))) _lastDetailsAttempt = DateTimeOffset.MinValue;
            }
        }
        finally { _refresh.Dispose(); }
        if (_detailsTask is { IsCompleted: false })
            try { await _detailsTask.WaitAsync(TimeSpan.FromSeconds(1)); } catch { }
    }

    private bool ParentAlive()
    {
        try
        {
            using var process = Process.GetProcessById(_parentPid);
            return !process.HasExited && process.ProcessName.Equals("TrafficMonitor", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private void Publish()
    {
        var state = _collector.Collect();
        if (state.Mode != _mode)
        {
            lock (_detailsLock) _details = default;
            _mode = state.Mode;
            _generation++;
            _lastDetailsAttempt = DateTimeOffset.MinValue;
            _lastError = null;
        }
        if ((_mode is VpnMode.Clash or VpnMode.TiziGo or VpnMode.Direct) &&
            (_detailsTask is null || _detailsTask.IsCompleted) &&
            DateTimeOffset.Now - _lastDetailsAttempt >= TimeSpan.FromSeconds(60))
        {
            _lastDetailsAttempt = DateTimeOffset.Now;
            var enabled = !File.Exists(_preferencePath) || File.ReadAllText(_preferencePath).Trim() == "true";
            if (!enabled)
            {
                lock (_detailsLock) _details = (null, null, null, _details.Node, _details.NodeCountry, null);
                _lastError = null;
            }
            _detailsTask = RefreshDetailsAsync(_mode, _generation, enabled);
        }
        (string? Ip, string? Country, string? Location, string? Node, string? NodeCountry, DateTimeOffset? CapturedAt) details;
        lock (_detailsLock) details = _details;
        state = state with
        {
            ExitIp = details.Ip, ExitCountry = details.Country, ExitLocation = details.Location,
            ClashNode = details.Node, ClashCountry = details.NodeCountry
        };
        var snapshot = _collector.ToSnapshot(state, _lastError);
        if (details.CapturedAt is { } captured)
        {
            var age = DateTimeOffset.Now - captured;
            snapshot = snapshot with { Tooltip = snapshot.Tooltip + $"\n出口采集：{captured:HH:mm:ss}（{(age.TotalSeconds > 90 ? "已过期，上次结果" : "仅代表该次探测出口")}）" };
        }
        _store.Write(snapshot);
    }

    private async Task RefreshDetailsAsync(VpnMode mode, long generation, bool readExit)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            string? node = null, nodeCountry = null;
            if (mode == VpnMode.Clash)
            {
                (node, nodeCountry) = await _resolver.TryResolveAsync(timeout.Token);
                if (generation != _generation) return;
                lock (_detailsLock)
                {
                    if (_details.Node != node) _details = default;
                    _details.Node = node;
                    _details.NodeCountry = nodeCountry;
                }
            }
            if (!readExit) return;
            using var handler = new HttpClientHandler
            {
                UseProxy = mode == VpnMode.Clash,
                Proxy = mode == VpnMode.Clash ? new WebProxy($"http://127.0.0.1:{VpnPaths.ClashPort}") : null
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            var geo = Ping0GeoParser.Parse(await client.GetStringAsync("https://ping0.cc/geo", timeout.Token));
            if (generation != _generation) return;
            if (geo is null) throw new InvalidDataException("ping0.cc 返回了无法识别的地区内容。");
            lock (_detailsLock)
                _details = (geo.Ip, geo.Country, geo.Location, node, nodeCountry, DateTimeOffset.Now);
            _lastError = null;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (generation == _generation) _lastError = $"出口地区更新失败，保留上次结果：{ex.GetType().Name}"; }
    }
}
