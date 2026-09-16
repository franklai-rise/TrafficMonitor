namespace VpnManager.Core;

public sealed class SwitchService
{
    private static readonly string[] ProxyNames = ["HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY"];
    private readonly ISystemGateway _system; private readonly VpnPaths _paths; private readonly StatusCollector _collector; private readonly SnapshotStore _store;
    public SwitchService(ISystemGateway system, VpnPaths paths, StatusCollector collector, SnapshotStore store) { _system = system; _paths = paths; _collector = collector; _store = store; }
    public async Task<SwitchResult> SwitchAsync(VpnMode target, CancellationToken token)
    {
        if (target is not (VpnMode.Clash or VpnMode.TiziGo or VpnMode.Direct)) throw new ArgumentOutOfRangeException(nameof(target));
        var initial = _collector.Collect(forceRouteRefresh: true);
        if (initial.CodexRunning) return new(false, "检测到 Codex 正在运行。为避免中断，未执行切换。", initial, false, false);
        if (initial.Mode == VpnMode.Unknown) return new(false, "VPN 状态未知，未执行网络变更。请先手动检查 VPN 状态。", initial, false, false);
        var environment = CaptureEnvironment();
        var restoreAttempted = false; var restoreSucceeded = false;
        try
        {
            if (target != VpnMode.Direct) { EnsureFilesExist(target); await StartAndVerifyTargetAsync(target, token); await StopOppositeAsync(target, token); }
            else await StopAllAsync(token);
            var finalBeforeEnv = _collector.Collect(forceRouteRefresh: true);
            EnsureFinalTransport(target, finalBeforeEnv);
            ApplyEnvironment(target, environment.NoProxy);
            var final = _collector.Collect(forceRouteRefresh: true);
            _store.Write(_collector.ToSnapshot(final));
            return new(true, target == VpnMode.Direct ? "已关闭已核实的 VPN 并进入直连模式。重新打开 Codex 后的实际请求仍需验收。" : $"已切换到 {target}。基础网络状态已确认；重新打开 Codex 后的实际请求仍需验收。", final, false, false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            restoreAttempted = true;
            restoreSucceeded = await RestoreAsync(initial, environment, token);
            var final = _collector.Collect(forceRouteRefresh: true);
            _store.Write(_collector.ToSnapshot(final, restoreSucceeded ? $"切换失败，已尝试恢复：{ex.Message}" : $"切换失败，恢复未完成：{ex.Message}"));
            return new(false, restoreSucceeded ? $"切换失败，已恢复原状态：{ex.Message}" : $"切换失败且自动恢复未完成：{ex.Message}。请查看日志并手动检查。", final, restoreAttempted, restoreSucceeded);
        }
    }
    public EnvironmentSnapshot CaptureEnvironment() => new(ProxyNames.ToDictionary(x => x, _system.GetUserEnvironment), _system.GetMachineEnvironment("HTTP_PROXY"), _system.GetMachineEnvironment("HTTPS_PROXY"), _system.GetMachineEnvironment("ALL_PROXY"), _system.GetUserEnvironment("NO_PROXY"));
    public void RestoreEnvironment(EnvironmentSnapshot snapshot)
    {
        foreach (var pair in snapshot.UserValues) _system.SetUserEnvironment(pair.Key, pair.Value);
        _system.SetUserEnvironment("NO_PROXY", snapshot.NoProxy); _system.BroadcastEnvironmentChanged();
    }
    private async Task StartAndVerifyTargetAsync(VpnMode target, CancellationToken token)
    {
        if (target == VpnMode.Clash)
        {
            if (!_system.IsPortListening(VpnPaths.ClashPort)) _system.Start(_paths.ClashExe);
            if (!await _system.WaitUntilAsync(() => _system.IsPortListening(VpnPaths.ClashPort), TimeSpan.FromSeconds(90), token)) throw new InvalidOperationException("Clash 未在 90 秒内监听 7890。旧 VPN 保持不变。");
        }
        else
        {
            if (!_system.IsAdapterUp(VpnPaths.TiziGoAdapter)) _system.Start(_paths.TiziGoExe);
            if (!await _system.WaitUntilAsync(HasCompleteTun, TimeSpan.FromSeconds(90), token)) throw new InvalidOperationException("TiziGo 未在 90 秒内建立完整 TUN 路由。旧 VPN 保持不变。");
        }
    }
    private async Task StopAllAsync(CancellationToken token)
    {
        foreach (var entry in new[] { (_paths.ClashExe, (Func<bool>)(() => !_system.IsPortListening(VpnPaths.ClashPort))), (_paths.TiziGoExe, (Func<bool>)(() => !_system.IsAdapterUp(VpnPaths.TiziGoAdapter))) })
        {
            if (!_system.IsProcessRunningAtPath(entry.Item1)) continue;
            if (!_system.RequestCloseAtPath(entry.Item1)) throw new InvalidOperationException("无法向已核实的 VPN 主进程发送正常退出请求；没有强制结束任何进程。");
            if (!await _system.WaitUntilAsync(entry.Item2, TimeSpan.FromSeconds(25), token)) throw new InvalidOperationException("VPN 未在等待期内正常退出；直连操作已停止，未强制结束进程。");
        }
    }
    private async Task StopOppositeAsync(VpnMode target, CancellationToken token)
    {
        var executable = target == VpnMode.Clash ? _paths.TiziGoExe : _paths.ClashExe;
        if (!_system.IsProcessRunningAtPath(executable)) return;
        if (!_system.RequestCloseAtPath(executable)) throw new InvalidOperationException("无法向旧 VPN 的已核实主进程发送正常退出请求；没有强制结束任何进程。");
        var stopped = target == VpnMode.Clash
            ? await _system.WaitUntilAsync(() => !_system.IsAdapterUp(VpnPaths.TiziGoAdapter), TimeSpan.FromSeconds(25), token)
            : await _system.WaitUntilAsync(() => !_system.IsPortListening(VpnPaths.ClashPort), TimeSpan.FromSeconds(25), token);
        if (!stopped) throw new InvalidOperationException("旧 VPN 未在等待期内正常退出；已停止切换，未强制结束进程。");
    }
    private void EnsureFinalTransport(VpnMode target, ObservedState state)
    {
        if (target == VpnMode.Clash && (!state.ClashPortListening || state.TunAdapterUp)) throw new InvalidOperationException("Clash 最终状态不完整，未提交代理环境变量。");
        if (target == VpnMode.TiziGo && (!state.TunAdapterUp || !state.TunRoutesComplete || state.ClashPortListening)) throw new InvalidOperationException("TiziGo 最终状态不完整，未提交代理环境变量。");
        if (target == VpnMode.Direct && (state.ClashPortListening || state.TunAdapterUp)) throw new InvalidOperationException("VPN 尚未完全退出，未提交直连环境变量。");
    }
    private void ApplyEnvironment(VpnMode target, string? preservedNoProxy)
    {
        var value = target == VpnMode.Clash ? $"http://127.0.0.1:{VpnPaths.ClashPort}" : null;
        foreach (var name in ProxyNames) _system.SetUserEnvironment(name, value);
        _system.SetUserEnvironment("NO_PROXY", preservedNoProxy);
        _system.BroadcastEnvironmentChanged();
    }
    private async Task<bool> RestoreAsync(ObservedState initial, EnvironmentSnapshot environment, CancellationToken token)
    {
        try
        {
            RestoreEnvironment(environment);
            if (initial.Mode == VpnMode.Clash && !_system.IsPortListening(VpnPaths.ClashPort)) { _system.Start(_paths.ClashExe); return await _system.WaitUntilAsync(() => _system.IsPortListening(VpnPaths.ClashPort), TimeSpan.FromSeconds(90), token); }
            if (initial.Mode == VpnMode.TiziGo && !HasCompleteTun()) { _system.Start(_paths.TiziGoExe); return await _system.WaitUntilAsync(HasCompleteTun, TimeSpan.FromSeconds(90), token); }
            if (initial.Mode == VpnMode.BothActive)
            {
                if (!_system.IsPortListening(VpnPaths.ClashPort)) _system.Start(_paths.ClashExe);
                if (!_system.IsAdapterUp(VpnPaths.TiziGoAdapter)) _system.Start(_paths.TiziGoExe);
                return await _system.WaitUntilAsync(() => _system.IsPortListening(VpnPaths.ClashPort) && HasCompleteTun(), TimeSpan.FromSeconds(90), token);
            }
            return initial.Mode is not VpnMode.Unknown and not VpnMode.BothActive;
        }
        catch { return false; }
    }
    private bool HasCompleteTun()
    {
        if (!_system.IsAdapterUp(VpnPaths.TiziGoAdapter)) return false;
        var routes = _system.GetRoutesForAdapter(VpnPaths.TiziGoAdapter, true);
        return new[] { "0.0.0.0/1", "128.0.0.0/1", "::/1", "8000::/1" }.All(routes.Contains);
    }
    private void EnsureFilesExist(VpnMode target)
    {
        var executable = target == VpnMode.Clash ? _paths.ClashExe : _paths.TiziGoExe;
        if (!File.Exists(executable)) throw new IOException($"未找到已核实的 VPN 程序：{executable}");
    }
}
