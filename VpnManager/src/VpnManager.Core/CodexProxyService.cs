namespace VpnManager.Core;

public sealed record CodexProxyResult(bool Success, string Summary, bool RestoreAttempted, bool RestoreSucceeded);

/// <summary>
/// Updates only the proxy variables inherited by a newly launched Codex process.
/// VPN processes, adapters, routes, NO_PROXY, and the Windows system proxy are untouched.
/// </summary>
public sealed class CodexProxyService
{
    private static readonly string[] ProxyNames = ["HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY"];
    private readonly ISystemGateway _system;
    private readonly StatusCollector _collector;
    private readonly VpnPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CodexProxyService(ISystemGateway system, StatusCollector collector, VpnPaths paths)
    {
        _system = system;
        _collector = collector;
        _paths = paths;
    }

    public CodexProxyResult Apply(VpnMode target)
    {
        if (target is not (VpnMode.Clash or VpnMode.TiziGo or VpnMode.Direct))
            return new(false, "仅能同步已确认的 Clash、TiziGo 或普通直连状态。", false, false);
        if (!_gate.Wait(0)) return new(false, "另一项 Codex 代理设置正在进行。", false, false);
        try { return ApplyCore(target); }
        finally { _gate.Release(); }
    }

    private CodexProxyResult ApplyCore(VpnMode target)
    {
        if (_system.IsCodexRunning())
            return new(false, "检测到 Codex 仍在运行。请先自行完全退出，然后再同步代理设置。", false, false);

        var observed = _collector.Collect(forceRouteRefresh: true);
        if (observed.Mode != target)
            return new(false, $"当前检测到 {Describe(observed.Mode)}，未写入代理变量。请先手动切好 VPN，再点对应按钮。", false, false);
        if (target == VpnMode.Clash && !_system.IsPortOwnedBy(VpnPaths.ClashPort, _paths.ClashDirectory, VpnPaths.ClashCoreImageNames))
            return new(false, "无法核实 7890 端口属于 Clash，未写入代理变量。", false, false);
        if (target != VpnMode.Clash && ProxyNames.Any(name => !string.IsNullOrWhiteSpace(_system.GetMachineEnvironment(name))))
            return new(false, "检测到机器级代理变量。清除用户变量后它仍会生效；请先处理冲突。", false, false);

        var previous = ProxyNames.ToDictionary(name => name, _system.GetUserEnvironment);
        var desired = target == VpnMode.Clash ? $"http://127.0.0.1:{VpnPaths.ClashPort}" : null;
        if (previous.Values.All(value => string.Equals(value, desired, StringComparison.OrdinalIgnoreCase)))
            return new(true, $"Codex 代理已与 {Describe(target)} 匹配；重新打开 Codex 即可继承。", false, false);

        try
        {
            if (_system.IsCodexRunning()) throw new InvalidOperationException("Codex 在设置前重新启动");
            foreach (var name in ProxyNames) _system.SetUserEnvironment(name, desired);
            _system.BroadcastEnvironmentChanged();
            if (_system.IsCodexRunning()) throw new InvalidOperationException("Codex 在设置期间重新启动");
            if (ProxyNames.Any(name => !string.Equals(_system.GetUserEnvironment(name), desired, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("代理变量复核未通过");
            return new(true, $"已同步 Codex 代理：{Describe(target)}。现在可以重新打开 Codex；实际请求尚未验证。", false, false);
        }
        catch (Exception ex)
        {
            var restored = true;
            foreach (var name in ProxyNames)
            {
                try { _system.SetUserEnvironment(name, previous[name]); }
                catch { restored = false; }
            }
            try { _system.BroadcastEnvironmentChanged(); }
            catch { restored = false; }
            foreach (var name in ProxyNames)
            {
                try { if (_system.GetUserEnvironment(name) != previous[name]) restored = false; }
                catch { restored = false; }
            }
            return new(false, restored
                ? $"代理设置失败，原变量已恢复：{ex.Message}"
                : $"代理设置失败且恢复未完成，请手动核对 HTTP_PROXY、HTTPS_PROXY、ALL_PROXY：{ex.Message}", true, restored);
        }
    }

    private static string Describe(VpnMode mode) => mode switch
    {
        VpnMode.Clash => "Clash 127.0.0.1:7890",
        VpnMode.TiziGo => "TiziGo TUN（无需本地代理端口）",
        VpnMode.Direct => "普通直连（不使用本地代理）",
        VpnMode.BothActive => "两个 VPN 同时活跃",
        _ => "未确认的网络状态"
    };
}
