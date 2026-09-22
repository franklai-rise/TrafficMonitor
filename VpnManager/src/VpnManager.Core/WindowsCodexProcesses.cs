using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace VpnManager.Core;

public sealed class WindowsCodexProcesses : ICodexProcessController
{
    private static readonly string[] Names = ["codex", "ChatGPT", "codex-code-mode-host", "codex-command-runner", "codex-computer-use", "codex-computer-use-swift", "extension-host"];
    private static readonly string UserProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string LocalCodex = Path.Combine(UserProfile, "AppData", "Local", "OpenAI", "Codex");
    private static readonly string PluginServer = Path.Combine(UserProfile, ".codex", "plugins", ".plugin-appserver");
    private static readonly string BundledPlugins = Path.Combine(UserProfile, ".codex", "plugins", "cache", "openai-bundled");
    private static readonly string WindowsApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
    private static readonly int Session = Process.GetCurrentProcess().SessionId;

    public static bool IsVerifiedPath(string path)
    {
        if (!Path.IsPathFullyQualified(path)) return false;
        path = Path.GetFullPath(path);
        if (!Names.Contains(Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase)) return false;
        if (ProcessPathRules.IsWithinDirectory(path, WindowsApps))
        {
            var package = Path.GetRelativePath(WindowsApps, path).Split(Path.DirectorySeparatorChar)[0];
            return package.StartsWith("OpenAI.Codex_", StringComparison.OrdinalIgnoreCase)
                && package.EndsWith("__2p2nqsd0c76g0", StringComparison.OrdinalIgnoreCase);
        }
        if (ProcessPathRules.IsWithinDirectory(path, LocalCodex) || ProcessPathRules.IsWithinDirectory(path, PluginServer)) return true;
        if (Path.GetFileName(path).Equals("extension-host.exe", StringComparison.OrdinalIgnoreCase))
            return ProcessPathRules.IsWithinDirectory(path, BundledPlugins);
        // npm CLI installations can live inside project directories. Verify package
        // metadata as well as the platform-specific vendor path, never name alone.
        if (!Path.GetFileName(path).Equals("codex.exe", StringComparison.OrdinalIgnoreCase)) return false;
        var directory = Path.GetDirectoryName(path);
        for (var depth = 0; depth < 6 && directory is not null; depth++, directory = Path.GetDirectoryName(directory))
        {
            if (!string.Equals(Path.GetFileName(Path.GetDirectoryName(directory)), "@openai", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "package.json")));
                var name = json.RootElement.GetProperty("name").GetString();
                if (name is "@openai/codex" or "@openai/codex-win32-x64" or "@openai/codex-win32-arm64")
                    return ProcessPathRules.IsWithinDirectory(path, Path.Combine(directory, "vendor"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or KeyNotFoundException) { }
        }
        return false;
    }

    public IReadOnlyList<CodexProcessInfo> Read()
    {
        var found = new List<CodexProcessInfo>();
        foreach (var name in Names)
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        if (process.SessionId != Session || process.HasExited) continue;
                        var path = ImagePath(process.Id);
                        var verified = IsVerifiedPath(path);
                        // Generic names outside Codex's directories are unrelated apps.
                        if (!verified && name is "ChatGPT" or "extension-host") continue;
                        found.Add(new(process.Id, process.StartTime.ToUniversalTime().Ticks, path, verified));
                    }
                    catch (InvalidOperationException) { } // Exited during inspection.
                    catch (System.ComponentModel.Win32Exception)
                    {
                        // Unknown identity must block switching, never authorize a kill.
                        found.Add(new(process.Id, 0, "", false));
                    }
                }
            }
        }
        return found;
    }

    public void RequestClose(CodexProcessInfo target)
    {
        using var process = OpenVerified(target);
        if (process is null) return;
        // Nonblocking close request; do not hang the manager on an unresponsive UI.
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var id);
            if (id == target.Id) PostMessage(window, 0x0010, IntPtr.Zero, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
    }

    public void Terminate(CodexProcessInfo target)
    {
        using var process = OpenVerified(target);
        if (process is null) return;
        try { process.Kill(entireProcessTree: false); }
        catch (InvalidOperationException) { }
    }

    private static Process? OpenVerified(CodexProcessInfo target)
    {
        if (!target.Verified || target.StartedAt == 0) throw new InvalidOperationException("Codex 进程身份未核实。");
        Process process;
        try { process = Process.GetProcessById(target.Id); }
        catch (ArgumentException) { return null; }
        try
        {
            if (process.HasExited) { process.Dispose(); return null; }
            // Opening a handle pins this process identity even if its PID is later reused.
            _ = process.Handle;
            if (process.SessionId != Session || process.StartTime.ToUniversalTime().Ticks != target.StartedAt ||
                !string.Equals(ImagePath(target.Id), target.ImagePath, StringComparison.OrdinalIgnoreCase) || !IsVerifiedPath(target.ImagePath))
                throw new InvalidOperationException("Codex 进程身份发生变化，已中止退出请求。");
            return process;
        }
        catch { process.Dispose(); throw; }
    }

    public async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        TimeSpan? emptySince = null;
        while (watch.Elapsed < timeout)
        {
            token.ThrowIfCancellationRequested();
            if ((await Task.Run(Read, token)).Count == 0)
            {
                emptySince ??= watch.Elapsed;
                if (watch.Elapsed - emptySince.Value >= TimeSpan.FromSeconds(1)) return true;
            }
            else emptySince = null;
            await Task.Delay(250, token);
        }
        return false;
    }

    private static string ImagePath(int id)
    {
        var handle = OpenProcess(0x1000, false, id);
        if (handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var text = new StringBuilder(32768); var size = text.Capacity;
            if (!QueryFullProcessImageName(handle, 0, text, ref size)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return text.ToString();
        }
        finally { CloseHandle(handle); }
    }
    private delegate bool EnumWindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder text, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
