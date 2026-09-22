namespace VpnManager.Core;

public sealed record CodexProcessInfo(int Id, long StartedAt, string ImagePath, bool Verified);
public sealed record CodexExitResult(bool Ready, string Summary);

public interface ICodexProcessController
{
    IReadOnlyList<CodexProcessInfo> Read();
    void RequestClose(CodexProcessInfo process);
    void Terminate(CodexProcessInfo process);
    Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken token);
}

/// This gate never changes the network. The existing switch service still checks
/// Codex again after this gate, including races with a new launch.
public sealed class CodexExitService(ICodexProcessController processes)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<CodexExitResult> PrepareAsync(bool interactive, Func<Task<bool>> confirm,
        Action<string> progress, CancellationToken token)
    {
        if (!await _gate.WaitAsync(0, token)) return new(false, "正在等待 Codex 退出，请勿重复操作。");
        try
        {
            var found = await Task.Run(processes.Read, token);
            if (found.Count == 0) return new(true, "Codex 已退出。");
            if (!interactive) return new(false, "开机直连已跳过：Codex 正在运行。可在管理器中手动切换并确认退出。");
            if (!await confirm()) return new(false, "已取消退出 Codex 和切换，当前连接保持不变。");
            token.ThrowIfCancellationRequested();
            found = await Task.Run(processes.Read, token);
            if (found.Any(p => !p.Verified)) return new(false, "无法核实部分 Codex 进程的身份，请手动退出后重试。尚未切换网络。");
            progress("已确认退出 Codex，正在请求正常退出（最多等待 15 秒）…");
            await Task.Run(() => { foreach (var process in found) processes.RequestClose(process); }, token);
            if (await processes.WaitForExitAsync(TimeSpan.FromSeconds(15), token)) return new(true, "Codex 已完全退出，继续切换。");

            progress("Codex 仍有后台进程，正在结束已核实的残留进程…");
            // A helper can briefly respawn while its parent is exiting. Limit retries;
            // never kill arbitrary descendants or bypass the final running check.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                token.ThrowIfCancellationRequested();
                found = await Task.Run(processes.Read, token);
                if (found.Any(p => !p.Verified)) return new(false, "Codex 残留进程身份无法核实，未继续切换。请手动退出后重试。");
                await Task.Run(() => { foreach (var process in found) processes.Terminate(process); }, token);
                if (await processes.WaitForExitAsync(TimeSpan.FromSeconds(3), token)) return new(true, "Codex 已完全退出，继续切换。");
            }
            return new(false, "Codex 未能完全退出，已停止切换，当前连接保持不变。请手动退出后重试。");
        }
        catch (OperationCanceledException) { return new(false, "退出等待已取消，尚未切换网络。"); }
        catch (Exception ex) { return new(false, $"退出 Codex 未完成，尚未切换网络：{ex.Message}"); }
        finally { _gate.Release(); }
    }
}
