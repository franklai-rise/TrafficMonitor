using System.Text.Json;
using System.Text.Encodings.Web;

namespace VpnManager.Core;

public sealed class SnapshotStore
{
    private readonly string _path;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public SnapshotStore(string directory) { Directory.CreateDirectory(directory); _path = System.IO.Path.Combine(directory, "vpn-status.json"); }
    public string Path => _path;
    public void Write(StatusSnapshot snapshot)
    {
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, _json));
        File.Move(temporary, _path, true);
    }
    public StatusSnapshot? Read()
    {
        try { return JsonSerializer.Deserialize<StatusSnapshot>(File.ReadAllText(_path), _json); }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }
}

public sealed class StatusCollector
{
    private static readonly string[] ProxyNames = ["HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY"];
    private readonly ISystemGateway _system; private readonly VpnPaths _paths;
    public StatusCollector(ISystemGateway system, VpnPaths paths) { _system = system; _paths = paths; }
    public ObservedState Collect(string? exitIp = null, string? exitCountry = null, string? exitLocation = null, string? clashNode = null, string? clashCountry = null, bool forceRouteRefresh = false)
    {
        var clashProcess = _system.IsProcessRunningAtPath(_paths.ClashExe);
        var port = _system.IsPortListening(VpnPaths.ClashPort);
        var tizi = _system.IsProcessRunningAtPath(_paths.TiziGoExe);
        var adapter = _system.IsAdapterUp(VpnPaths.TiziGoAdapter);
        var routes = adapter ? _system.GetRoutesForAdapter(VpnPaths.TiziGoAdapter, forceRouteRefresh) : Array.Empty<string>();
        var complete = new[] { "0.0.0.0/1", "128.0.0.0/1", "::/1", "8000::/1" }.All(routes.Contains);
        var expected = $"http://127.0.0.1:{VpnPaths.ClashPort}";
        var values = ProxyNames.Select(_system.GetUserEnvironment).ToArray();
        var proxyMatches = values.All(x => string.Equals(x, expected, StringComparison.OrdinalIgnoreCase));
        var anyProxy = values.Any(x => !string.IsNullOrWhiteSpace(x));
        var mode = port && adapter ? VpnMode.BothActive : port ? VpnMode.Clash : adapter && complete ? VpnMode.TiziGo : !clashProcess && !tizi ? VpnMode.Direct : VpnMode.Unknown;
        var region = File.Exists(_paths.TiziGoRegionFile) ? File.ReadAllText(_paths.TiziGoRegionFile).Trim().ToLowerInvariant() : null;
        var warning = mode == VpnMode.BothActive ? "两个 VPN 同时活跃；出口结果不能归属到目标 VPN。" : adapter && !complete ? "TiziGo 网卡存在但 TUN 路由不完整。" : null;
        return new(mode, clashProcess, port, tizi, adapter, complete, _system.IsCodexRunning(), proxyMatches, anyProxy, region, clashNode, clashCountry, exitIp, exitCountry, exitLocation, DateTimeOffset.Now, warning);
    }
    public StatusSnapshot ToSnapshot(ObservedState s, string? error = null)
    {
        var (country, source) = s.Mode switch {
            VpnMode.TiziGo => (s.ExitLocation ?? (RegionName(s.TiziGoRegion) is not "未知" ? RegionName(s.TiziGoRegion) : s.ExitCountry ?? "未知"), s.ExitLocation is not null ? "ping0.cc 出口 IP 地理信息" : RegionName(s.TiziGoRegion) is not "未知" ? "TiziGo 当前区域" : s.ExitCountry is null ? "未读取区域" : "ping0.cc 出口 IP 地理信息"),
            VpnMode.Clash => (s.ExitLocation ?? s.ClashCountry ?? s.ExitCountry ?? "未知", s.ExitLocation is not null ? "ping0.cc 出口 IP 地理信息" : s.ClashCountry is not null ? "Clash 节点名称" : s.ExitCountry is null ? "未读取节点" : "ping0.cc 出口 IP 地理信息"),
            _ => ("未知", "无可用 VPN 状态") };
        var access = s.Mode == VpnMode.Clash ? $"HTTP/SOCKS5 :{VpnPaths.ClashPort}" : s.Mode == VpnMode.TiziGo ? "TUN" : "未连接";
        var software = s.Mode == VpnMode.Clash ? "Clash" : s.Mode == VpnMode.TiziGo ? "TiziGo" : s.Mode == VpnMode.BothActive ? "冲突" : "VPN";
        var text = s.Mode is VpnMode.Clash or VpnMode.TiziGo ? $"{country}\n{access} · {software}" : s.Mode == VpnMode.BothActive ? "VPN 状态冲突" : "VPN 未连接";
        var exit = s.ExitIp is null ? "未刷新" : string.IsNullOrWhiteSpace(s.ExitLocation) ? s.ExitIp : $"{s.ExitIp}（{s.ExitLocation}）";
        var tip = $"{text}\n观察时间：{s.ObservedAt:yyyy-MM-dd HH:mm:ss}\n国家来源：{source}\n出口 IP：{exit}\n代理变量：{(s.ProxyMatchesClash ? "匹配 Clash" : s.AnyUserProxy ? "存在非预期值" : "未设置")}";
        if (!string.IsNullOrWhiteSpace(s.Warning ?? error)) tip += $"\n提示：{s.Warning ?? error}";
        return new(StatusSnapshot.CurrentSchema, text, tip, s.Mode.ToString(), access, software, country, source, s.ExitIp, s.ObservedAt, true, error ?? s.Warning);
    }
    private static string RegionName(string? code) => code?.ToLowerInvariant() switch { "us" => "美国", "jp" => "日本", "hk" => "香港", "de" => "德国", "nl" => "荷兰", _ => "未知" };
}
