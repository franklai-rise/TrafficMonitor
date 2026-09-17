namespace VpnManager.Core;

public enum VpnMode { Unknown, Direct, Clash, TiziGo, BothActive }
public sealed record VpnPaths(string ClashExe, string TiziGoExe, string ClashConfig, string TiziGoRegionFile, string StateDirectory)
{
    public const int ClashPort = 7890;
    public const string TiziGoAdapter = "TiziGo";
    private static string UserLocalAppData => Path.Combine(Environment.ExpandEnvironmentVariables("%USERPROFILE%"), "AppData", "Local");
    public static VpnPaths Default { get; } = new(
        @"E:\Program Files\Clash for Windows\Clash for Windows.exe",
        @"E:\Program Files\VelikSoft\TiziGo\TiziGo.exe",
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "clash", "config.yaml"),
        Path.Combine(UserLocalAppData, "VelikSoft", "TiziGo", "selected-region-v1.txt"),
        Path.Combine(UserLocalAppData, "VpnManager"));
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
    bool IsAdapterUp(string adapterName);
    IReadOnlyCollection<string> GetRoutesForAdapter(string adapterName, bool forceRefresh = false);
    bool IsCodexRunning();
    void Start(string executablePath);
    bool RequestCloseAtPath(string executablePath);
    Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken cancellationToken);
    string? GetUserEnvironment(string name);
    string? GetMachineEnvironment(string name);
    void SetUserEnvironment(string name, string? value);
    void BroadcastEnvironmentChanged();
}
