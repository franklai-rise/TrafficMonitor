$ErrorActionPreference = 'Stop'
$state = Join-Path $env:USERPROFILE 'AppData\Local\CodexRadarTrafficMonitor'
$report = Join-Path $state 'weight-controls-result.json'
$result = [ordered]@{ success = $false }
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class RadarWeightWindow {
  public delegate bool EnumProc(IntPtr h, IntPtr p);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
}
'@
$original = $null
$sliders = @{}
try {
    $original = if (Test-Path (Join-Path $state 'ranking-weights.json')) {
        Get-Content (Join-Path $state 'ranking-weights.json') -Raw | ConvertFrom-Json
    } else { [pscustomobject]@{ Price = 3; Iq = 3; Speed = 4 } }
    $hostProcess = Get-Process CodexRadarHost | Select-Object -First 1
    $windows = [System.Collections.Generic.List[IntPtr]]::new()
    [RadarWeightWindow]::EnumWindows({ param($h,$l)
        $p = 0; [void][RadarWeightWindow]::GetWindowThreadProcessId($h,[ref]$p)
        if ($p -eq $hostProcess.Id -and [RadarWeightWindow]::IsWindowVisible($h)) { $windows.Add($h) }
        return $true
    },[IntPtr]::Zero) | Out-Null
    if ($windows.Count -lt 1) { throw 'Radar detail window is not visible.' }
    $root = [System.Windows.Automation.AutomationElement]::FromHandle($windows[0])
    foreach ($id in @('PriceWeight','IqWeight','SpeedWeight')) {
        $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
        $control = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
        if ($null -eq $control) { throw "Missing slider: $id" }
        $sliders[$id] = $control.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
    }
    $result.before = if (Test-Path (Join-Path $state 'ranking-weights.json')) {
        Get-Content (Join-Path $state 'ranking-weights.json') -Raw
    } else { 'default 3:3:4' }
    $sliders['PriceWeight'].SetValue(10)
    $sliders['IqWeight'].SetValue(0)
    $sliders['SpeedWeight'].SetValue(0)
    Start-Sleep -Milliseconds 800
    $result.changed = Get-Content (Join-Path $state 'ranking-weights.json') -Raw
    $result.statusChanged = (Get-Content (Join-Path $state 'status.ini') -Raw -Encoding Unicode) -match '价格:IQ:速度权重 10:0:0'
    $result.success = $result.changed -match '"Price":10' -and $result.changed -match '"Iq":0' -and $result.changed -match '"Speed":0' -and $result.statusChanged
}
catch { $result.error = $_.Exception.Message }
finally {
    try {
        if ($null -ne $original -and $sliders.Count -eq 3) {
            $sliders['PriceWeight'].SetValue([double]$original.Price)
            $sliders['IqWeight'].SetValue([double]$original.Iq)
            $sliders['SpeedWeight'].SetValue([double]$original.Speed)
            Start-Sleep -Milliseconds 800
            $result.restored = (Get-Content (Join-Path $state 'status.ini') -Raw -Encoding Unicode) -match ("价格:IQ:速度权重 $($original.Price):$($original.Iq):$($original.Speed)")
        }
    } catch { $result.restoreError = $_.Exception.Message }
    $result | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $report -Encoding UTF8
}
if (-not $result.success -or -not $result.restored) { exit 1 }
