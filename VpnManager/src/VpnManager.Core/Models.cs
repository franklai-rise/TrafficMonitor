namespace VpnManager.Core;

using Microsoft.Win32;

public enum VpnMode { Unknown, Direct, Clash, TiziGo, BothActive }
public sealed record VpnPaths(string ClashExe, string TiziGoExe, string ClashConfig, string TiziGoRegionFile, string StateDirectory)
{
    public const int ClashPort = 7890;
    public const string TiziGoAdapter = "TiziGo";

    /// Clash GUI 的进程镜像名，用于解析真实安装位置。
    public static readonly string[] ClashGuiImageNames = ["Clash for Windows", "Clash Verge", "clash-verge", "clash-nyanpasu", "ClashN"];

    /// Clash 的内核与辅助服务进程名。
    /// 关键：Clash for Windows 的内核 clash-win64 是由服务 clash-core-service 托管启动的，
    /// 关闭 GUI 并不会让它退出，7890 端口会继续被占用，必须单独处理。
    public static readonly string[] ClashCoreImageNames = ["clash-win64", "clash-meta", "clash", "mihomo", "clash-core-service"];

    /// TiziGo 的 GUI 与内核进程名。内核 sing-box 位于 <安装目录>\Core\ 下。
    public static readonly string[] TiziGoImageNames = ["TiziGo", "sing-box"];

    public static readonly string[] TiziGoInstallCandidates =
    [
        @"E:\Programs\TiziGo\TiziGo.exe",
        @"E:\Program Files\VelikSoft\TiziGo\TiziGo.exe",
        @"C:\Program Files\VelikSoft\TiziGo\TiziGo.exe"
    ];

    /// Clash 候选安装位置（自动探测用，按优先级排列）。
    public static readonly string[] ClashInstallCandidates =
    [
        @"F:\New_Clash\Clash for Windows\Clash for Windows.exe",
        @"E:\Program Files\Clash for Windows\Clash for Windows.exe",
        @"D:\ClashX\Clash for Windows\Clash for Windows.exe",
        @"C:\Program Files\Clash for Windows\Clash for Windows.exe",
        @"C:\Program Files (x86)\Clash for Windows\Clash for Windows.exe",
        @"E:\Program Files\Clash Verge\Clash Verge.exe"
    ];

    private static string UserLocalAppData => Path.Combine(Environment.ExpandEnvironmentVariables("%USERPROFILE%"), "AppData", "Local");

    /// Clash GUI 所在的安装目录。内核与辅助服务都在它下面，用于"已核实路径"校验，
    /// 避免按通用进程名误杀无关进程。
    public string ClashDirectory => Path.GetDirectoryName(ClashExe) ?? string.Empty;

    /// TiziGo GUI 所在的安装目录，同样用于"已核实路径"校验。
    public string TiziGoDirectory => Path.GetDirectoryName(TiziGoExe) ?? string.Empty;

    public static VpnPaths Default { get; } = Create(ResolveClashExe(), ResolveTiziGoExe());

    public static VpnPaths Create(string clashExe, string? tiziGoExe = null) => new(
        clashExe,
        tiziGoExe ?? ResolveTiziGoExe(),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "clash", "config.yaml"),
        Path.Combine(UserLocalAppData, "VelikSoft", "TiziGo", "selected-region-v1.txt"),
        Path.Combine(UserLocalAppData, "VpnManager"));

    /// 自动探测 Clash 可执行文件。原先这里写死为 E:\Program Files\...，而实际使用的是
    /// F:\New_Clash\...，导致 IsProcessRunningAtPath 永远返回 false：切换时既不会关闭 Clash，
    /// 也无法释放 7890，最终在 EnsureFinalTransport 抛异常并回滚。
    /// 探测顺序：
    ///   1) 正在运行的 GUI 进程的真实镜像路径（最可靠）
    ///   2) 正在运行的内核进程，从它的目录向上回溯找到 GUI
    ///   3) 候选列表里第一个存在的文件
    public static string ResolveClashExe()
    {
        foreach (var name in ClashGuiImageNames)
        {
            var found = TryGetRunningImagePath(name);
            if (found is not null) return found;
        }
        foreach (var name in ClashCoreImageNames)
        {
            var core = TryGetRunningImagePath(name);
            if (core is null) continue;
            var directory = Path.GetDirectoryName(core);
            for (var depth = 0; depth < 6 && directory is not null; depth++)
            {
                var gui = Path.Combine(directory, "Clash for Windows.exe");
                if (File.Exists(gui)) return gui;
                directory = Path.GetDirectoryName(directory);
            }
        }
        foreach (var candidate in ClashInstallCandidates)
            if (File.Exists(candidate)) return candidate;
        return ClashInstallCandidates[0];
    }

    public static string ResolveTiziGoExe()
    {
        var running = TryGetRunningImagePath("TiziGo");
        if (running is not null) return running;
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    using var app = uninstall.OpenSubKey(name);
                    if (app is null || !(app.GetValue("DisplayName") as string ?? "").StartsWith("TiziGo", StringComparison.OrdinalIgnoreCase)) continue;
                    var directory = app.GetValue("InstallLocation") as string;
                    if (!string.IsNullOrWhiteSpace(directory))
                    {
                        var executable = Path.Combine(directory, "TiziGo.exe");
                        if (File.Exists(executable)) return executable;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        foreach (var candidate in TiziGoInstallCandidates)
            if (File.Exists(candidate)) return candidate;
        return TiziGoInstallCandidates[0];
    }

    private static string? TryGetRunningImagePath(string imageName)
    {
        try
        {
            var processes = System.Diagnostics.Process.GetProcessesByName(imageName);
            try { foreach (var process in processes)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return path;
                }
                catch { }
            }
            } finally { foreach (var process in processes) process.Dispose(); }
        }
        catch { }
        return null;
    }
}
public sealed record ObservedState(VpnMode Mode, bool ClashProcess, bool ClashPortListening, bool TiziGoProcess, bool TunAdapterUp, bool TunRoutesComplete, bool CodexRunning, bool ProxyMatchesClash, bool AnyUserProxy, string? TiziGoRegion, string? ClashNode, string? ClashCountry, string? ExitIp, string? ExitCountry, string? ExitLocation, DateTimeOffset ObservedAt, string? Warning);
public sealed record StatusSnapshot(int SchemaVersion, string DisplayText, string Tooltip, string Mode, string Access, string Software, string Country, string CountrySource, string? ExitIp, DateTimeOffset ObservedAt, bool IsFresh, string? LastError)
{
    public const int CurrentSchema = 1;
}
public sealed record EnvironmentSnapshot(Dictionary<string, string?> UserValues, string? MachineHttpProxy, string? MachineHttpsProxy, string? MachineAllProxy, string? NoProxy);
public sealed record SwitchResult(bool Success, string Summary, ObservedState FinalState, bool RestoreAttempted, bool RestoreSucceeded);
public interface ISystemGateway
{
    bool IsProcessRunningAtPath(string executablePath);
    bool IsPortListening(int port);
    bool IsPortOwnedBy(int port, string directory, IReadOnlyCollection<string> imageNames);
    bool IsAdapterUp(string adapterName);
    IReadOnlyCollection<string> GetRoutesForAdapter(string adapterName, bool forceRefresh = false);
    bool IsCodexRunning();
    void Start(string executablePath);
    bool RequestCloseAtPath(string executablePath);

    /// 强制结束「镜像路径位于 directory 之下、且镜像名在 imageNames 中」的进程，返回结束的数量。
    /// 用于停止隐藏到托盘的 GUI，或由服务/计划任务托管的残留内核进程。
    /// 仍然坚持"已核实路径"原则，不按通用进程名乱杀。
    int TerminateProcessesInDirectory(string directory, IReadOnlyCollection<string> imageNames);

    Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken cancellationToken);
    Task<bool> ProbePathAsync(VpnMode mode, CancellationToken cancellationToken);
    string? GetUserEnvironment(string name);
    string? GetMachineEnvironment(string name);
    void SetUserEnvironment(string name, string? value);
    void BroadcastEnvironmentChanged();
}
