[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$TrafficRoot = 'E:\TrafficMonitor_V1.85_x64\TrafficMonitor',
    [switch]$RestartTrafficMonitor,
    [switch]$Worker,
    [string]$ResultPath
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSCommandPath
$sourceHost = Join-Path $root 'artifacts\host\CodexRadarHost.exe'
$sourcePlugin = Join-Path $root 'artifacts\plugin\RadarScorePlugin.dll'
$trafficExe = Join-Path $TrafficRoot 'TrafficMonitor.exe'
$pluginDirectory = Join-Path $TrafficRoot 'plugins'
$targetPlugin = Join-Path $pluginDirectory 'RadarScorePlugin.dll'
$config = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'TrafficMonitor\config.ini'
$stateDirectory = Join-Path ([Environment]::GetFolderPath('UserProfile')) 'AppData\Local\CodexRadarTrafficMonitor'
$targetHost = Join-Path $stateDirectory 'CodexRadarHost.exe'
$itemId = 'codex-radar-score-v1'
if (-not $ResultPath) { $ResultPath = Join-Path $root 'artifacts\install-result.json' }

foreach ($file in @($sourceHost, $sourcePlugin, $trafficExe, $config)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "所需文件不存在：$file" }
}
$configBytes = [IO.File]::ReadAllBytes($config)
if ($configBytes.Length -ge 2 -and $configBytes[0] -eq 0xFF -and $configBytes[1] -eq 0xFE) {
    $encoding = [Text.Encoding]::Unicode
    $preambleLength = 2
} elseif ($configBytes.Length -ge 3 -and $configBytes[0] -eq 0xEF -and $configBytes[1] -eq 0xBB -and $configBytes[2] -eq 0xBF) {
    $encoding = New-Object Text.UTF8Encoding($true)
    $preambleLength = 3
} else {
    $encoding = New-Object Text.UTF8Encoding($false)
    $preambleLength = 0
}
$configText = $encoding.GetString($configBytes, $preambleLength, $configBytes.Length - $preambleLength)
$itemLine = [regex]::Match($configText, '(?m)^(plugin_display_item\s*=\s*)([^\r\n]*)')
if (-not $itemLine.Success) { throw 'TrafficMonitor 配置中未找到 plugin_display_item，未做更改。' }
$existingItems = @($itemLine.Groups[2].Value -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$newItems = @($existingItems)
if ($newItems -notcontains $itemId) { $newItems += $itemId }
$updatedConfig = $configText.Substring(0, $itemLine.Groups[2].Index) + ($newItems -join ',') + $configText.Substring($itemLine.Groups[2].Index + $itemLine.Groups[2].Length)

if (-not $PSCmdlet.ShouldProcess($TrafficRoot, '备份配置并安装 GPT 雷达 TrafficMonitor 插件')) { return }

if (-not $Worker) {
    # An MSIX-hosted shell can redirect AppData for its children. Task Scheduler starts
    # the worker outside that package, so both TrafficMonitor and Radar see real files.
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $admin = (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    $restartFlag = if ($RestartTrafficMonitor) { ' -RestartTrafficMonitor' } else { '' }
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -TrafficRoot "{1}" -ResultPath "{2}"{3}' -f $PSCommandPath,$TrafficRoot,$ResultPath,$restartFlag
    if (-not $admin) {
        $elevated = Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -ArgumentList $arguments -Wait -PassThru
        if ($elevated.ExitCode -ne 0) { throw "雷达安装启动失败，退出码 $($elevated.ExitCode)。" }
        if (-not (Test-Path -LiteralPath $ResultPath)) { throw "没有安装结果：$ResultPath" }
        $report = Get-Content -LiteralPath $ResultPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if (-not $report.success) { throw "雷达安装失败：$($report.error)" }
        return $report
    }
    $name = 'CodexRadar-Install-' + [Guid]::NewGuid().ToString('N')
    $action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -Argument ($arguments + ' -Worker') -WorkingDirectory $root
    $principal = New-ScheduledTaskPrincipal -UserId $identity.Name -LogonType Interactive -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::FromMinutes(5))
    if (Test-Path -LiteralPath $ResultPath) { Move-Item -LiteralPath $ResultPath -Destination ($ResultPath + '.previous-' + (Get-Date -Format 'yyyyMMddHHmmss')) }
    try {
        Register-ScheduledTask -TaskName $name -Action $action -Principal $principal -Settings $settings | Out-Null
        Start-ScheduledTask -TaskName $name
        for ($i=0;$i -lt 240;$i++) {
            Start-Sleep -Milliseconds 500
            if (Test-Path -LiteralPath $ResultPath) {
                $report = Get-Content -LiteralPath $ResultPath -Raw -Encoding UTF8 | ConvertFrom-Json
                if (-not $report.success) { throw "雷达安装失败：$($report.error)" }
                return $report
            }
        }
        throw "雷达安装超时；检查结果文件：$ResultPath"
    } finally { Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue }
}

$backupDirectory = Join-Path $stateDirectory ('backups\install-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
$backupConfig = Join-Path $backupDirectory 'TrafficMonitor-config.ini'
$backupPlugin = Join-Path $backupDirectory 'RadarScorePlugin.dll'
$backupHost = Join-Path $backupDirectory 'CodexRadarHost.exe'
$configExisted = Test-Path -LiteralPath $config
$pluginExisted = Test-Path -LiteralPath $targetPlugin
$hostExisted = Test-Path -LiteralPath $targetHost

$stoppedPids = @()
$startedTraffic = $false
$configTemp = $config + '.tmp-' + [Guid]::NewGuid().ToString('N')
try {
    $running = @(Get-Process -Name TrafficMonitor -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0 -and -not $RestartTrafficMonitor) {
        throw 'TrafficMonitor 正在运行；为安全替换已加载的插件，请先关闭它或使用 -RestartTrafficMonitor。'
    }
    if ($running.Count -gt 0) {
        Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class CodexRadarTrafficMonitorWindow {
    public delegate bool EnumWindowProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowProc callback, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    public static bool CloseMonitor(int pid) {
        bool sent = false;
        EnumWindows((hWnd, _) => {
            uint owner; GetWindowThreadProcessId(hWnd, out owner);
            if(owner == (uint)pid) {
                var title = new StringBuilder(256); GetWindowText(hWnd, title, title.Capacity);
                if(title.ToString().Equals("TrafficMonitor", StringComparison.OrdinalIgnoreCase)) {
                    IntPtr result;
                    if (SendMessageTimeout(hWnd, 0x0010, IntPtr.Zero, IntPtr.Zero, 2, 4000, out result) != IntPtr.Zero) sent = true;
                }
            }
            return true;
        }, IntPtr.Zero);
        return sent;
    }
}
'@
        foreach ($process in $running) {
            if ([CodexRadarTrafficMonitorWindow]::CloseMonitor($process.Id)) {
                $stoppedPids += $process.Id
                if (-not $process.WaitForExit(10000)) { throw 'TrafficMonitor 未能正常关闭；已停止安装且没有强制结束进程。' }
            }
        }
        if ($stoppedPids.Count -eq 0) { throw '找不到可安全关闭的 TrafficMonitor 主窗口；安装文件已备份但未写入。' }
    }

    foreach ($hostProcess in @(Get-Process -Name CodexRadarHost -ErrorAction SilentlyContinue)) {
        if (-not $hostProcess.WaitForExit(10000)) { throw 'GPT 雷达后台未能随 TrafficMonitor 退出；安装已中止。' }
    }

    Copy-Item -LiteralPath $config -Destination $backupConfig
    if ($pluginExisted) { Copy-Item -LiteralPath $targetPlugin -Destination $backupPlugin }
    if ($hostExisted) { Copy-Item -LiteralPath $targetHost -Destination $backupHost }

    New-Item -ItemType Directory -Path $stateDirectory -Force | Out-Null
    New-Item -ItemType Directory -Path $pluginDirectory -Force | Out-Null
    Copy-Item -LiteralPath $sourceHost -Destination $targetHost -Force
    Copy-Item -LiteralPath $sourcePlugin -Destination $targetPlugin -Force
    if ((Get-FileHash -LiteralPath $sourceHost -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $targetHost -Algorithm SHA256).Hash) { throw '后台程序复制校验失败。' }
    if ((Get-FileHash -LiteralPath $sourcePlugin -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $targetPlugin -Algorithm SHA256).Hash) { throw '插件复制校验失败。' }

    [IO.File]::WriteAllText($configTemp, $updatedConfig, $encoding)
    if ($preambleLength -gt 0) {
        $body = [IO.File]::ReadAllBytes($configTemp)
        $preamble = $encoding.GetPreamble()
        $matchesPreamble = $body.Length -ge $preambleLength
        for ($index = 0; $matchesPreamble -and $index -lt $preambleLength; $index++) {
            if ($body[$index] -ne $preamble[$index]) { $matchesPreamble = $false }
        }
        if (-not $matchesPreamble) {
            throw 'TrafficMonitor 配置编码校验失败。'
        }
    }
    $atomicReplaceBackup = Join-Path $backupDirectory ('TrafficMonitor-config-atomic-' + [Guid]::NewGuid().ToString('N') + '.ini')
    [IO.File]::Replace($configTemp, $config, $atomicReplaceBackup)

    if ($RestartTrafficMonitor) {
        Start-Process -FilePath $trafficExe -WorkingDirectory $TrafficRoot -WindowStyle Hidden
        $startedTraffic = $true
        Start-Sleep -Seconds 4
        if (-not (Get-Process -Name TrafficMonitor -ErrorAction SilentlyContinue)) { throw 'TrafficMonitor 重启后未运行。' }
    }

    $verification = [pscustomobject]@{
        success = $true
        backupDirectory = $backupDirectory
        config = $config
        plugin = $targetPlugin
        host = $targetHost
        pluginSha256 = (Get-FileHash -LiteralPath $targetPlugin -Algorithm SHA256).Hash
        hostSha256 = (Get-FileHash -LiteralPath $targetHost -Algorithm SHA256).Hash
        itemEnabled = $updatedConfig.Contains($itemId)
        horizontalArrangePreserved = ([regex]::Match($configText, '(?m)^horizontal_arrange\s*=.*$').Value -eq [regex]::Match($updatedConfig, '(?m)^horizontal_arrange\s*=.*$').Value)
        itemSpacePreserved = ([regex]::Match($configText, '(?m)^item_space\s*=.*$').Value -eq [regex]::Match($updatedConfig, '(?m)^item_space\s*=.*$').Value)
        trafficMonitorRestarted = $startedTraffic
        completedAt = (Get-Date).ToString('o')
    }
    $verification | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $backupDirectory 'install-result.json') -Encoding UTF8
    $verification | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $ResultPath -Encoding UTF8
    Write-Output ($verification | Format-List | Out-String)
} catch {
    $failure = $_.Exception.Message
    if (Test-Path -LiteralPath $backupConfig) { Copy-Item -LiteralPath $backupConfig -Destination $config -Force }
    if ($pluginExisted) {
        if (Test-Path -LiteralPath $backupPlugin) { Copy-Item -LiteralPath $backupPlugin -Destination $targetPlugin -Force }
    } elseif (Test-Path -LiteralPath $targetPlugin) { Remove-Item -LiteralPath $targetPlugin -Force }
    if ($hostExisted) {
        if (Test-Path -LiteralPath $backupHost) { Copy-Item -LiteralPath $backupHost -Destination $targetHost -Force }
    } elseif (Test-Path -LiteralPath $targetHost) { Remove-Item -LiteralPath $targetHost -Force }
    if ($stoppedPids.Count -gt 0 -and -not (Get-Process -Name TrafficMonitor -ErrorAction SilentlyContinue)) {
        Start-Process -FilePath $trafficExe -WorkingDirectory $TrafficRoot -WindowStyle Hidden
    }
    [pscustomobject]@{ success=$false; error=$failure; backupDirectory=$backupDirectory } |
        ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $ResultPath -Encoding UTF8
    throw "安装已回退；备份位于 $backupDirectory。原因：$failure"
} finally {
    if (Test-Path -LiteralPath $configTemp) { Remove-Item -LiteralPath $configTemp -Force }
}
