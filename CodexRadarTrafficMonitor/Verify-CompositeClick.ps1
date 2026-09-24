$ErrorActionPreference = 'Stop'
$state = Join-Path $env:USERPROFILE 'AppData\Local\CodexRadarTrafficMonitor'
$report = Join-Path $state 'composite-click-result.json'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class CompositeRadarClick {
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string t);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out Rect r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr SendMessageTimeout(IntPtr h,uint m,IntPtr w,IntPtr l,uint f,uint t,out IntPtr r);
}
'@
$result = [ordered]@{ success = $false }
try {
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    $result.admin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    $tm = Get-Process TrafficMonitor | Select-Object -First 1
    $tray = [CompositeRadarClick]::FindWindow('Shell_TrayWnd', $null)
    $targets = [System.Collections.Generic.List[IntPtr]]::new()
    [CompositeRadarClick]::EnumChildWindows($tray, {
        param($h,$l)
        $p = 0; [void][CompositeRadarClick]::GetWindowThreadProcessId($h,[ref]$p)
        if ($p -eq $tm.Id) {
            $t = [Text.StringBuilder]::new(128); [void][CompositeRadarClick]::GetWindowText($h,$t,128)
            if ($t.ToString() -eq 'TrafficMonitorTaskbarWindow') { $targets.Add($h) }
        }
        return $true
    }, [IntPtr]::Zero) | Out-Null
    if ($targets.Count -ne 1) { throw "Expected one taskbar panel, found $($targets.Count)." }
    $window = $targets[0]
    $result.windowHandle = $window.ToInt64()
    $result.windowValid = [CompositeRadarClick]::IsWindow($window)
    $rect = New-Object CompositeRadarClick+Rect
    [void][CompositeRadarClick]::GetClientRect($window,[ref]$rect)
    $result.taskbarWidth = $rect.Right
    $result.taskbarHeight = $rect.Bottom
    # The current panel is 811 pixels wide: CPU/RAM, VPN, then the right-hand radar block.
    $x = $rect.Right - 170
    $y = [Math]::Max(3, [Math]::Min(13, $rect.Bottom - 3))
    if ($rect.Right -le $x) { throw 'Taskbar panel is too narrow for the expected radar region.' }
    $result.clickX = $x; $result.clickY = $y
    $position = ($y -shl 16) -bor $x
    [IntPtr]$returned = [IntPtr]::Zero
    $sent = [CompositeRadarClick]::SendMessageTimeout($window,0x0203,[IntPtr]1,[IntPtr]$position,2,5000,[ref]$returned)
    if ($sent -eq [IntPtr]::Zero) { throw "Taskbar double-click message was not accepted (Win32 $([Runtime.InteropServices.Marshal]::GetLastWin32Error()))." }
    Start-Sleep -Seconds 2
    $hostProcess = Get-Process CodexRadarHost | Select-Object -First 1
    $windows = [System.Collections.Generic.List[IntPtr]]::new()
    [CompositeRadarClick]::EnumWindows({
        param($h,$l)
        $p = 0; [void][CompositeRadarClick]::GetWindowThreadProcessId($h,[ref]$p)
        if ($p -eq $hostProcess.Id -and [CompositeRadarClick]::IsWindowVisible($h)) { $windows.Add($h) }
        return $true
    },[IntPtr]::Zero) | Out-Null
    $result.visibleRadarWindows = $windows.Count
    if ($windows.Count -lt 1) { throw 'Radar detail window did not open.' }
    $element = [System.Windows.Automation.AutomationElement]::FromHandle($windows[0])
    $result.title = $element.Current.Name
    foreach ($id in @('RankingGrid','ModelsGrid','EffortsGrid','PriceWeight','IqWeight','SpeedWeight')) {
        $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
        $control = $element.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
        $result[$id] = $null -ne $control
    }
    $result.success = $result.title -like '*众测雷达*'
    foreach ($id in @('RankingGrid','ModelsGrid','EffortsGrid','PriceWeight','IqWeight','SpeedWeight')) {
        if (-not $result[$id]) { $result.success = $false }
    }
}
catch { $result.error = $_.Exception.Message }
finally { $result | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $report -Encoding UTF8 }
if (-not $result.success) { exit 1 }
