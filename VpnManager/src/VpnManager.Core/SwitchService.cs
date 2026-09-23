namespace VpnManager.Core;

public sealed class SwitchService
{
    private static readonly string[] ProxyNames = ["HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY"];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ISystemGateway _system;
    private readonly VpnPaths _paths;
    private readonly StatusCollector _collector;
    private readonly SnapshotStore _store;
    public SwitchService(ISystemGateway system, VpnPaths paths, StatusCollector collector, SnapshotStore store)
    { _system = system; _paths = paths; _collector = collector; _store = store; }

    public async Task<SwitchResult> SwitchAsync(VpnMode target, CancellationToken token)
    {
        if (target is not (VpnMode.Clash or VpnMode.TiziGo or VpnMode.Direct)) throw new ArgumentOutOfRangeException(nameof(target));
        if (!await _gate.WaitAsync(0, token)) return new(false, "另一次切换正在进行，请等待完成。", _collector.Collect(), false, false);
        try { return await SwitchCoreAsync(target, token); }
        finally { _gate.Release(); }
    }

    private async Task<SwitchResult> SwitchCoreAsync(VpnMode target, CancellationToken token)
    {
        var initial = _collector.Collect(forceRouteRefresh: true);
        if (initial.CodexRunning) return new(false, "检测到 Codex 正在运行。为避免中断，未执行切换。", initial, false, false);
        if (initial.Mode == VpnMode.Unknown) return new(false, initial.Warning ?? "VPN 状态未知，未执行网络变更。", initial, false, false);
        var environment = CaptureEnvironment();
        if (target != VpnMode.Clash && new[] { environment.MachineHttpProxy, environment.MachineHttpsProxy, environment.MachineAllProxy }.Any(v => !string.IsNullOrWhiteSpace(v)))
            return new(false, "检测到机器级代理变量，清除用户变量后仍会生效。请先手动处理冲突，未执行切换。", initial, false, false);
        var touched = false;
        OperationLog.Write($"切换开始：{initial.Mode} -> {target}；Clash={_paths.ClashExe}");
        try
        {
            token.ThrowIfCancellationRequested();
            if (target != VpnMode.Direct) EnsureFilesExist(target);
            if (_system.IsCodexRunning()) return new(false, "Codex 已重新启动，尚未切换网络。请重新确认退出后再切换。", initial, false, false);
            touched = true;
            if (target == VpnMode.Direct) { await StopAsync(VpnMode.Clash, token); await StopAsync(VpnMode.TiziGo, token); }
            else
            {
                // The two VPNs can interfere with each other's startup and HTTPS probes.
                // Remove the old transport before starting or probing the target; recovery
                // restores the captured state if any later step fails.
                await StopAsync(target == VpnMode.Clash ? VpnMode.TiziGo : VpnMode.Clash, token);
                if (_system.IsCodexRunning()) throw new InvalidOperationException("等待期间 Codex 已启动，停止切换并尝试恢复原状态。");
                await StartAndVerifyAsync(target, token);
            }
            EnsureFinalTransport(target, _collector.Collect(forceRouteRefresh: true));
            if (_system.IsCodexRunning()) throw new InvalidOperationException("切换期间 Codex 已启动，停止切换并尝试恢复原状态。");
            if (!await _system.ProbePathAsync(target, token)) throw new IOException("目标独立路径的基础 HTTPS 检查失败。");
            if (_system.IsCodexRunning()) throw new InvalidOperationException("验证期间 Codex 已启动，停止切换并尝试恢复原状态。");
            foreach (var name in ProxyNames) _system.SetUserEnvironment(name, target == VpnMode.Clash ? $"http://127.0.0.1:{VpnPaths.ClashPort}" : null);
            _system.BroadcastEnvironmentChanged();
            var final = _collector.Collect(forceRouteRefresh: true);
            EnsureFinalTransport(target, final);
            if (target == VpnMode.Clash ? !final.ProxyMatchesClash : final.AnyUserProxy) throw new IOException("代理变量复核未通过。");
            TryWrite(final);
            OperationLog.Write($"切换完成：{final.Mode}");
            return new(true, $"已切换到 {(target == VpnMode.Direct ? "普通直连" : target.ToString())}。本机路径与基础 HTTPS 可达性已确认；Codex 实际请求尚未验证。", final, false, false);
        }
        catch (Exception ex)
        {
            OperationLog.Write($"切换失败：{ex.GetType().Name}。尝试恢复原状态。");
            var restored = !touched || await RestoreAsync(initial, environment);
            ObservedState final;
            try { final = _collector.Collect(forceRouteRefresh: true); }
            catch { final = initial with { Mode = VpnMode.Unknown, Warning = "无法读取恢复后的状态。" }; restored = false; }
            var summary = restored ? $"切换未完成，已复核原状态：{ex.Message}" : $"切换失败，需要手动恢复：{ex.Message}";
            TryWrite(final, summary);
            OperationLog.Write(summary);
            return new(false, summary, final, touched, restored);
        }
    }

    public EnvironmentSnapshot CaptureEnvironment() => new(ProxyNames.ToDictionary(x => x, _system.GetUserEnvironment), _system.GetMachineEnvironment("HTTP_PROXY"), _system.GetMachineEnvironment("HTTPS_PROXY"), _system.GetMachineEnvironment("ALL_PROXY"), _system.GetUserEnvironment("NO_PROXY"));
    public void RestoreEnvironment(EnvironmentSnapshot snapshot)
    {
        foreach (var pair in snapshot.UserValues) _system.SetUserEnvironment(pair.Key, pair.Value);
        // Switching does not modify NO_PROXY; do not overwrite independent user edits.
        _system.BroadcastEnvironmentChanged();
    }
    private bool OwnedClashPort() => _system.IsPortOwnedBy(VpnPaths.ClashPort, _paths.ClashDirectory, VpnPaths.ClashCoreImageNames);
    private bool HasCompleteTun() => _system.IsAdapterUp(VpnPaths.TiziGoAdapter) && new[] { "0.0.0.0/1", "128.0.0.0/1", "::/1", "8000::/1" }.All(_system.GetRoutesForAdapter(VpnPaths.TiziGoAdapter, true).Contains);
    private async Task StartAndVerifyAsync(VpnMode mode, CancellationToken token)
    {
        if (mode == VpnMode.Clash)
        {
            if (_system.IsPortListening(VpnPaths.ClashPort) && !OwnedClashPort()) throw new InvalidOperationException("7890 已被其他程序占用或无法确认所属进程。");
            if (!_system.IsPortListening(VpnPaths.ClashPort) && !_system.IsProcessRunningAtPath(_paths.ClashExe)) _system.Start(_paths.ClashExe);
            if (!await WaitLoggedAsync(() => _system.IsPortListening(VpnPaths.ClashPort) && OwnedClashPort(), TimeSpan.FromSeconds(120), "Clash 端口及进程身份", token))
                throw new InvalidOperationException("Clash 未在 120 秒内就绪。");
        }
        else
        {
            if (!_system.IsAdapterUp(VpnPaths.TiziGoAdapter) && !_system.IsProcessRunningAtPath(_paths.TiziGoExe)) _system.Start(_paths.TiziGoExe);
            if (!await WaitLoggedAsync(HasCompleteTun, TimeSpan.FromSeconds(240), "TiziGo 完整 TUN 路由", token)) throw new InvalidOperationException("TiziGo 未在 240 秒内建立完整 TUN 路由。");
        }
    }
    private async Task StopAsync(VpnMode mode, CancellationToken token)
    {
        var clash = mode == VpnMode.Clash;
        var exe = clash ? _paths.ClashExe : _paths.TiziGoExe;
        var directory = clash ? _paths.ClashDirectory : _paths.TiziGoDirectory;
        bool TransportOff() => clash ? !_system.IsPortListening(VpnPaths.ClashPort) : !_system.IsAdapterUp(VpnPaths.TiziGoAdapter);
        bool Stopped() => TransportOff() && !_system.IsProcessRunningAtPath(exe);
        if (Stopped()) return;
        if (clash && !TransportOff() && !OwnedClashPort()) throw new InvalidOperationException("无法核实 7890 所属进程，未停止任何程序。");
        if (_system.IsProcessRunningAtPath(exe))
        {
            // Electron's CloseMainWindow often only hides the GUI in the tray.
            // A missing window is also normal after it has already been hidden.
            if (_system.RequestCloseAtPath(exe) && await WaitLoggedAsync(Stopped, TimeSpan.FromSeconds(5), $"{mode} 正常退出", token)) return;
        }
        if (Stopped()) return;
        if (clash && !TransportOff() && !OwnedClashPort()) throw new InvalidOperationException("7890 所属进程已变化，已停止操作。");
        var guiNames = clash ? VpnPaths.ClashGuiImageNames : new[] { "TiziGo" };
        var guiCount = _system.TerminateProcessesInDirectory(directory, guiNames);
        OperationLog.Write($"停止 {mode} 已核实路径的残留界面进程：{guiCount} 个。");
        if (clash && !TransportOff() && !OwnedClashPort()) throw new InvalidOperationException("7890 所属进程已变化，已停止操作。");
        if (!TransportOff())
        {
            var coreNames = clash ? VpnPaths.ClashCoreImageNames : new[] { "sing-box" };
            var coreCount = _system.TerminateProcessesInDirectory(directory, coreNames);
            OperationLog.Write($"停止 {mode} 已核实路径的残留内核：{coreCount} 个。");
        }
        if (!await WaitLoggedAsync(Stopped, TimeSpan.FromSeconds(20), $"{mode} 端口/网卡释放", token)) throw new InvalidOperationException($"{mode} 尚未完全退出，未提交代理变量。");
    }
    private async Task<bool> WaitLoggedAsync(Func<bool> condition, TimeSpan timeout, string label, CancellationToken token)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var next = TimeSpan.FromSeconds(10);
        var result = await _system.WaitUntilAsync(() =>
        {
            if (watch.Elapsed >= next) { OperationLog.Write($"等待 {label}：{watch.Elapsed.TotalSeconds:F0}/{timeout.TotalSeconds:F0} 秒"); next += TimeSpan.FromSeconds(10); }
            return condition();
        }, timeout, token);
        OperationLog.Write($"{label}：{(result ? "完成" : "超时")}，{watch.Elapsed.TotalSeconds:F1} 秒");
        return result;
    }
    private void EnsureFinalTransport(VpnMode target, ObservedState state)
    {
        var valid = target switch
        {
            VpnMode.Clash => state.ClashPortListening && OwnedClashPort() && !state.TunAdapterUp && !state.TiziGoProcess,
            VpnMode.TiziGo => state.TunAdapterUp && state.TunRoutesComplete && !state.ClashPortListening && !state.ClashProcess,
            VpnMode.Direct => !state.ClashPortListening && !state.TunAdapterUp && !state.ClashProcess && !state.TiziGoProcess,
            _ => false
        };
        if (!valid) throw new InvalidOperationException("最终端口、网卡或进程状态不完整。");
    }
    private async Task<bool> RestoreAsync(ObservedState initial, EnvironmentSnapshot environment)
    {
        // Recovery must still run when the original request has been cancelled.
        using var recovery = new CancellationTokenSource(TimeSpan.FromMinutes(6));
        var failures = false;
        try { RestoreEnvironment(environment); } catch { failures = true; }
        // Stop the newly started transport first, then restore the original one.
        foreach (var mode in new[] { VpnMode.Clash, VpnMode.TiziGo })
        {
            var wasActive = mode == VpnMode.Clash ? initial.ClashProcess || initial.ClashPortListening : initial.TiziGoProcess || initial.TunAdapterUp;
            if (wasActive) continue;
            try { await StopAsync(mode, recovery.Token); }
            catch { failures = true; }
        }
        foreach (var mode in new[] { VpnMode.Clash, VpnMode.TiziGo })
        {
            var wasActive = mode == VpnMode.Clash ? initial.ClashProcess || initial.ClashPortListening : initial.TiziGoProcess || initial.TunAdapterUp;
            if (!wasActive) continue;
            try { EnsureFilesExist(mode); await StartAndVerifyAsync(mode, recovery.Token); }
            catch { failures = true; }
        }
        try
        {
            var final = _collector.Collect(forceRouteRefresh: true);
            var same = final.Mode == initial.Mode && final.ClashPortListening == initial.ClashPortListening && final.TunAdapterUp == initial.TunAdapterUp && final.TunRoutesComplete == initial.TunRoutesComplete;
            var sameEnvironment = environment.UserValues.All(p => _system.GetUserEnvironment(p.Key) == p.Value);
            return !failures && same && sameEnvironment;
        }
        catch { return false; }
    }
    private void TryWrite(ObservedState state, string? error = null)
    {
        try { _store.Write(_collector.ToSnapshot(state, error)); }
        catch (Exception ex) { OperationLog.Write($"状态快照写入失败：{ex.GetType().Name}"); }
    }
    private void EnsureFilesExist(VpnMode target)
    {
        if (!File.Exists(target == VpnMode.Clash ? _paths.ClashExe : _paths.TiziGoExe)) throw new IOException($"未找到 {target} 主程序。");
    }
}
