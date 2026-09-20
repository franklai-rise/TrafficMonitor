using Microsoft.Win32;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Net;
using System.Net.Http;

namespace VpnManager.Core;

public sealed class WindowsSystemGateway : ISystemGateway
{
    private readonly object _routeCacheLock = new();
    private readonly Dictionary<string, (DateTimeOffset CapturedAt, IReadOnlyCollection<string> Routes)> _routeCache = new(StringComparer.OrdinalIgnoreCase);
    public bool IsProcessRunningAtPath(string executablePath)
    {
        var name = Path.GetFileNameWithoutExtension(executablePath);
        var processes = Process.GetProcessesByName(name);
        try { return processes.Any(process => { try { return string.Equals(process.MainModule?.FileName, executablePath, StringComparison.OrdinalIgnoreCase); } catch { return false; } }); }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    public bool IsPortListening(int port) => IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(x => x.Port == port);
    public bool IsPortOwnedBy(int port, string directory, IReadOnlyCollection<string> imageNames)
    {
        try
        {
            var owners = GetListenerOwners(port).Distinct().ToArray();
            if (owners.Length == 0) return false;
            foreach (var id in owners)
            {
                using var process = Process.GetProcessById(id);
                if (!imageNames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase) ||
                    !ProcessPathRules.IsWithinDirectory(process.MainModule?.FileName ?? "", directory)) return false;
            }
            return true;
        }
        catch { return false; }
    }
    private static IEnumerable<int> GetListenerOwners(int port)
    {
        var owners = new List<int>();
        foreach (var family in new[] { 2, 23 })
        {
            var size = 0;
            var first = GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, 3, 0);
            if (first != 0 && first != 122) throw new IOException("无法读取端口进程信息。");
            if (size < 4) continue;
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetExtendedTcpTable(buffer, ref size, false, family, 3, 0) != 0) throw new IOException("端口进程信息在读取中变化，请重试。");
                var count = Marshal.ReadInt32(buffer);
                var rowSize = family == 2 ? 24 : 56;
                var portOffset = family == 2 ? 8 : 20;
                var pidOffset = family == 2 ? 20 : 52;
                for (var i = 0; i < count; i++)
                {
                    var row = IntPtr.Add(buffer, 4 + i * rowSize);
                    var raw = Marshal.ReadInt32(row, portOffset);
                    var actual = ((raw & 255) << 8) | ((raw >> 8) & 255);
                    if (actual == port) owners.Add(Marshal.ReadInt32(row, pidOffset));
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return owners;
    }
    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);
    public bool IsAdapterUp(string adapterName) => NetworkInterface.GetAllNetworkInterfaces().Any(x => string.Equals(x.Name, adapterName, StringComparison.OrdinalIgnoreCase) && x.OperationalStatus == OperationalStatus.Up);
    public IReadOnlyCollection<string> GetRoutesForAdapter(string adapterName, bool forceRefresh = false)
    {
        lock (_routeCacheLock) if (!forceRefresh && _routeCache.TryGetValue(adapterName, out var cached) && DateTimeOffset.UtcNow - cached.CapturedAt < TimeSpan.FromSeconds(10)) return cached.Routes;
        var script = $"Get-NetRoute -ErrorAction Stop | Where-Object {{$_.InterfaceAlias -eq '{adapterName.Replace("'", "''")}'}} | Select-Object -ExpandProperty DestinationPrefix";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var start = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -EncodedCommand {encoded}") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var process = Process.Start(start); if (process is null) return [];
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(5000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new IOException("本机路由查询超过 5 秒，已停止本次查询。");
        }
        if (process.ExitCode != 0) throw new IOException("本机路由查询失败，请查看网卡状态。");
        var text = output.WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        _ = errors.GetAwaiter().GetResult();
        var routes = text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        lock (_routeCacheLock) _routeCache[adapterName] = (DateTimeOffset.UtcNow, routes);
        return routes;
    }
    public bool IsCodexRunning()
    {
        foreach (var name in new[] { "codex", "codex-code-mode-host" })
        {
            var processes = Process.GetProcessesByName(name);
            var running = processes.Length > 0;
            foreach (var process in processes) process.Dispose();
            if (running) return true;
        }
        return false;
    }
    public void Start(string executablePath)
    {
        using var process = Process.Start(new ProcessStartInfo(executablePath) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(executablePath) });
        if (process is null) throw new IOException("无法启动 VPN 程序。");
    }
    public bool RequestCloseAtPath(string executablePath)
    {
        var matched = false; var requested = false;
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (!string.Equals(process.MainModule?.FileName, executablePath, StringComparison.OrdinalIgnoreCase)) continue;
                matched = true;
                // 多进程应用（Electron 等）的渲染/GPU/工具进程没有主窗口。
                // 原实现遇到第一个没有窗口的进程就 return false，导致整体判定为"无法发送关闭请求"。
                if (process.MainWindowHandle != IntPtr.Zero && process.CloseMainWindow()) requested = true;
            }
            catch { }
            finally { process.Dispose(); }
        }
        return matched && requested;
    }
    public int TerminateProcessesInDirectory(string directory, IReadOnlyCollection<string> imageNames)
    {
        if (string.IsNullOrWhiteSpace(directory) || imageNames.Count == 0) return 0;
        var terminated = 0;
        // Stop the supervisor first so it cannot immediately restart the kernel.
        var processes = Process.GetProcesses();
        try { foreach (var process in processes.OrderBy(p => { try { return p.ProcessName.EndsWith("service") ? 0 : 1; } catch { return 2; } }))
        {
            var verified = false;
            try
            {
                if (!imageNames.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase)) continue;
                var path = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(path)) continue;
                // 只结束"已核实路径"之下的进程；路径之外的任何进程都不碰。
                if (!ProcessPathRules.IsWithinDirectory(path, directory)) continue;
                verified = true;
                process.Kill();
                if (!process.WaitForExit(5000)) throw new IOException("已核实的 VPN 内核没有退出。");
                terminated++;
            }
            catch (System.ComponentModel.Win32Exception) { if (verified) throw new UnauthorizedAccessException("无法停止已核实的 VPN 内核，请检查管理员权限。"); }
            catch (InvalidOperationException) { }
        }
        } finally { foreach (var process in processes) process.Dispose(); }
        return terminated;
    }
    public async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        do { cancellationToken.ThrowIfCancellationRequested(); if (condition()) return true; await Task.Delay(800, cancellationToken); } while (watch.Elapsed < timeout);
        return false;
    }
    public async Task<bool> ProbePathAsync(VpnMode mode, CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler { UseProxy = mode == VpnMode.Clash,
            Proxy = mode == VpnMode.Clash ? new WebProxy($"http://127.0.0.1:{VpnPaths.ClashPort}") : null };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
        async Task<bool> Reachable(string url)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, url);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                // A TLS HTTP response is transport evidence only, never evidence of Codex authentication.
                var code = (int)response.StatusCode;
                return code >= 200 && code < 500 && code != 407;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (HttpRequestException) { return false; }
            catch (OperationCanceledException) { return false; }
        }
        return (await Task.WhenAll(Reachable("https://www.microsoft.com/"), Reachable("https://www.baidu.com/"))).Any(x => x);
    }
    public string? GetUserEnvironment(string name) => Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
    public string? GetMachineEnvironment(string name) => Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine);
    public void SetUserEnvironment(string name, string? value) => Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);
    public void BroadcastEnvironmentChanged() => SendMessageTimeout((nint)0xffff, 0x1A, 0, "Environment", 2, 5000, out _);
    [DllImport("user32.dll", CharSet = CharSet.Auto)] private static extern nint SendMessageTimeout(nint hWnd, uint msg, nint wParam, string lParam, uint flags, uint timeout, out nint result);
}
