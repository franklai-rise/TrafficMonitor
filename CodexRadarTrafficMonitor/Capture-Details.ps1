$ErrorActionPreference = 'Stop'
$output = Join-Path $env:USERPROFILE 'AppData\Local\CodexRadarTrafficMonitor\details-style.png'
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class RadarDetailsCapture {
  public delegate bool EnumProc(IntPtr h,IntPtr p);
  [StructLayout(LayoutKind.Sequential)] public struct Rect { public int L,T,R,B; }
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc f,IntPtr p);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out Rect r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h,IntPtr dc,uint flags);
}
'@
$hostProcess = Get-Process CodexRadarHost | Select-Object -First 1
$windows = [System.Collections.Generic.List[IntPtr]]::new()
[RadarDetailsCapture]::EnumWindows({param($h,$l)
    $p=0; [void][RadarDetailsCapture]::GetWindowThreadProcessId($h,[ref]$p)
    if($p -eq $hostProcess.Id -and [RadarDetailsCapture]::IsWindowVisible($h)){$windows.Add($h)}
    return $true
},[IntPtr]::Zero) | Out-Null
if($windows.Count -lt 1){throw 'Radar details are not visible.'}
$rect = New-Object RadarDetailsCapture+Rect
[void][RadarDetailsCapture]::GetWindowRect($windows[0],[ref]$rect)
$bitmap=[System.Drawing.Bitmap]::new($rect.R-$rect.L,$rect.B-$rect.T)
$graphics=[System.Drawing.Graphics]::FromImage($bitmap)
$dc=$graphics.GetHdc()
try { $ok=[RadarDetailsCapture]::PrintWindow($windows[0],$dc,2) }
finally { $graphics.ReleaseHdc($dc) }
$bitmap.Save($output,[System.Drawing.Imaging.ImageFormat]::Png)
$graphics.Dispose();$bitmap.Dispose()
if(-not $ok){throw 'PrintWindow failed.'}
