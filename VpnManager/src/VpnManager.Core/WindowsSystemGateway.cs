using Microsoft.Win32;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace VpnManager.Core;

public sealed class WindowsSystemGateway : ISystemGateway
{
    public bool IsProcessRunningAtPath(string executablePath) => Process.GetProcesses().Any(p => { try { return string.Equals(p.MainModule?.FileName, executablePath, StringComparison.OrdinalIgnoreCase); } catch { return false; } finally { p.Dispose(); } });
    public bool IsPortListening(int port) => IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(x => x.Port == port);
    public bool IsAdapterUp(string adapterName) => NetworkInterface.GetAllNetworkInterfaces().Any(x => string.Equals(x.Name, adapterName, StringComparison.OrdinalIgnoreCase) && x.OperationalStatus == OperationalStatus.Up);
    public IReadOnlyCollection<string> GetRoutesForAdapter(string adapterName)
    {
        var start = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -Command \"Get-NetRoute -ErrorAction SilentlyContinue | Where-Object {{$_.InterfaceAlias -eq '{adapterName}'}} | Select-Object -ExpandProperty DestinationPrefix\"") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var process = Process.Start(start); if (process is null) return [];
        var text = process.StandardOutput.ReadToEnd(); process.WaitForExit(3000);
        return text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
    public bool IsCodexRunning() => Process.GetProcessesByName("codex").Length > 0 || Process.GetProcessesByName("codex-code-mode-host").Length > 0;
    public void Start(string executablePath)
    {
        if (Process.Start(new ProcessStartInfo(executablePath) { UseShellExecute = true }) is null) throw new IOException("无法启动 VPN 程序。");
    }
    public bool RequestCloseAtPath(string executablePath)
    {
        var found = false;
        foreach (var process in Process.GetProcesses()) { try { if (!string.Equals(process.MainModule?.FileName, executablePath, StringComparison.OrdinalIgnoreCase)) continue; found = true; if (!process.CloseMainWindow()) return false; } catch { return false; } finally { process.Dispose(); } }
        return found;
    }
    public async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var until = DateTimeOffset.UtcNow + timeout;
        do { cancellationToken.ThrowIfCancellationRequested(); if (condition()) return true; await Task.Delay(800, cancellationToken); } while (DateTimeOffset.UtcNow < until);
        return false;
    }
    public string? GetUserEnvironment(string name) => Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
    public string? GetMachineEnvironment(string name) => Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
    public void SetUserEnvironment(string name, string? value) => Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);
    public void BroadcastEnvironmentChanged() => SendMessageTimeout((nint)0xffff, 0x1A, 0, "Environment", 2, 5000, out _);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern nint SendMessageTimeout(nint hWnd, uint msg, nint wParam, string lParam, uint flags, uint timeout, out nint result);
}
