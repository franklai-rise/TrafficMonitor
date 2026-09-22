using VpnManager.Core;

internal static class CodexExitTests
{
    public static async Task<int> RunAsync(string root)
    {
        var passed = 0;
        void Check(bool value) { if (!value) throw new Exception("Codex exit assertion failed"); }
        void Pass(string name) { passed++; Console.WriteLine("PASS " + name); }
        Task<bool> Accept() => Task.FromResult(true);
        Task<bool> NeverAsk() => throw new Exception("Unexpected confirmation");
        void Progress(string _) { }

        var f = new FakeCodexProcesses { Alive = false };
        Check((await new CodexExitService(f).PrepareAsync(true, NeverAsk, Progress, default)).Ready && f.Closes == 0);
        Pass("No Codex needs no confirmation");

        f = new();
        var result = await new CodexExitService(f).PrepareAsync(true, () => Task.FromResult(false), Progress, default);
        Check(!result.Ready && f.Closes == 0 && f.Kills == 0 && f.Alive);
        Pass("Cancel leaves Codex untouched");

        f = new();
        Check(!(await new CodexExitService(f).PrepareAsync(false, NeverAsk, Progress, default)).Ready && f.Closes == 0 && f.Kills == 0);
        Pass("Login never closes Codex or prompts");

        f = new() { Graceful = true };
        Check((await new CodexExitService(f).PrepareAsync(true, Accept, Progress, default)).Ready && f.Closes == 1 && f.Kills == 0 && f.WaitCalls == 1);
        Pass("Confirmed graceful Codex exit");

        f = new() { ForceWorks = true };
        Check((await new CodexExitService(f).PrepareAsync(true, Accept, Progress, default)).Ready && f.Closes == 1 && f.Kills == 1);
        Pass("Confirmed residual Codex cleanup");

        f = new() { Verified = false };
        Check(!(await new CodexExitService(f).PrepareAsync(true, Accept, Progress, default)).Ready && f.Closes == 0 && f.Kills == 0);
        Pass("Unverified Codex never terminated");

        f = new() { DenyClose = true };
        Check(!(await new CodexExitService(f).PrepareAsync(true, Accept, Progress, default)).Ready && f.Kills == 0);
        Pass("Exit access denial blocks switching");

        f = new();
        Check(!(await new CodexExitService(f).PrepareAsync(true, Accept, Progress, default)).Ready && f.Kills == 3);
        Pass("Respawning Codex has bounded retries");

        f = new();
        using var cts = new CancellationTokenSource();
        f.OnWait = cts.Cancel;
        Check(!(await new CodexExitService(f).PrepareAsync(true, Accept, Progress, cts.Token)).Ready && f.Kills == 0);
        Pass("Canceled exit wait never force terminates");

        f = new() { ForceWorks = true };
        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new CodexExitService(f);
        var first = service.PrepareAsync(true, () => { entered.SetResult(); return confirmation.Task; }, Progress, default);
        await entered.Task;
        Check(!(await service.PrepareAsync(true, NeverAsk, Progress, default)).Ready && f.Closes == 0);
        confirmation.SetResult(true);
        Check((await first).Ready && f.Closes == 1 && f.Kills == 1);
        Pass("Concurrent clicks share one confirmation");

        // The switcher must still block if Codex reopens after a successful exit gate.
        f = new() { ForceWorks = true };
        Check((await new CodexExitService(f).PrepareAsync(true, Accept, Progress, default)).Ready);
        var system = new FakeSystem { Codex = true };
        var dir = Path.Combine(root, "reopened-codex"); Directory.CreateDirectory(dir);
        var paths = new VpnPaths("Clash.exe", "TiziGo.exe", "", "", dir);
        var collector = new StatusCollector(system, paths);
        var switcher = new SwitchService(system, paths, collector, new SnapshotStore(dir));
        Check(!(await switcher.SwitchAsync(VpnMode.Direct, default)).Success && system.Mutations == 0);
        Pass("Reopened Codex still blocks network mutation");

        var windowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
        Check(WindowsCodexProcesses.IsVerifiedPath(Path.Combine(windowsApps, "OpenAI.Codex_1.0_x64__2p2nqsd0c76g0", "app", "ChatGPT.exe")));
        Check(!WindowsCodexProcesses.IsVerifiedPath(Path.Combine(windowsApps, "OpenAI.ChatGPT_1.0_x64__2p2nqsd0c76g0", "app", "ChatGPT.exe")));
        Check(!WindowsCodexProcesses.IsVerifiedPath(Path.Combine(root, "codex.exe")));
        Check(!WindowsCodexProcesses.IsVerifiedPath(Path.Combine(root, "extension-host.exe")));
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local", "OpenAI", "Codex");
        Check(WindowsCodexProcesses.IsVerifiedPath(Path.Combine(local, "bin", "version", "codex.exe")));
        Check(!WindowsCodexProcesses.IsVerifiedPath(Path.Combine(local + "-other", "codex.exe")));
        Check(!WindowsCodexProcesses.IsVerifiedPath(Path.Combine(local, "..", "other", "codex.exe")));
        Pass("Codex paths exclude unrelated ChatGPT and same-name apps");
        return passed;
    }

    private sealed class FakeCodexProcesses : ICodexProcessController
    {
        public bool Alive = true, Verified = true, Graceful, ForceWorks, DenyClose;
        public int Closes, Kills, WaitCalls;
        public Action? OnWait;
        public IReadOnlyList<CodexProcessInfo> Read() => Alive ? [new(123, 1, "simulated-codex.exe", Verified)] : [];
        public void RequestClose(CodexProcessInfo p) { Closes++; if (DenyClose) throw new UnauthorizedAccessException("simulated"); if (Graceful) Alive = false; }
        public void Terminate(CodexProcessInfo p) { if (!p.Verified) throw new Exception("unsafe kill"); Kills++; if (ForceWorks) Alive = false; }
        public Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken token) { WaitCalls++; OnWait?.Invoke(); token.ThrowIfCancellationRequested(); return Task.FromResult(!Alive); }
    }
}
