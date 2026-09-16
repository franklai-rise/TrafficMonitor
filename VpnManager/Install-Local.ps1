[CmdletBinding(SupportsShouldProcess)]
param([switch]$InstallPlugin)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSCommandPath
$publish = Join-Path $root 'artifacts\manager'
$icon = Join-Path $root 'src\VpnManager\Assets\VpnManager.ico'
$plugin = Join-Path $root 'artifacts\plugin\VpnStatusPlugin.dll'
$state = Join-Path $env:LOCALAPPDATA 'VpnManager'
$shortcutIcon = Join-Path $state 'VpnManager-blue-v2.ico'
$trafficRoot = 'E:\TrafficMonitor_V1.85_x64\TrafficMonitor'
$trafficConfig = Join-Path $env:APPDATA 'TrafficMonitor\config.ini'

if (-not (Test-Path $publish)) { throw '未找到已发布的管理器。请先执行 build。' }
New-Item -ItemType Directory -Path $state -Force | Out-Null
Copy-Item -Path (Join-Path $publish '*') -Destination $state -Recurse -Force
Copy-Item -LiteralPath $icon -Destination $state -Force
Copy-Item -LiteralPath $icon -Destination $shortcutIcon -Force
Write-Host "管理器已部署到 $state"
$shell = New-Object -ComObject WScript.Shell
$shortcutPath = Join-Path ([Environment]::GetFolderPath('Desktop')) 'VPN 管理器.lnk'
if (Test-Path -LiteralPath $shortcutPath) { Remove-Item -LiteralPath $shortcutPath -Force }
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = Join-Path $state 'VpnManager.exe'
$shortcut.WorkingDirectory = $state
$shortcut.IconLocation = "$shortcutIcon,0"
$shortcut.Description = 'VPN 管理器（切换前请退出 Codex）'
$shortcut.Save()

if ($InstallPlugin) {
    if (Get-Process TrafficMonitor -ErrorAction SilentlyContinue) { throw 'TrafficMonitor 正在运行。请先完全退出后再使用 -InstallPlugin。' }
    if (-not (Test-Path $plugin)) { throw '未找到插件 DLL。请先执行 build。' }
    $backupDir = Join-Path $state ('backups\TrafficMonitor-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
    Copy-Item -LiteralPath $trafficConfig -Destination $backupDir -Force
    $pluginDir = Join-Path $trafficRoot 'plugins'; New-Item -ItemType Directory -Path $pluginDir -Force | Out-Null
    Copy-Item -LiteralPath $plugin -Destination $pluginDir -Force
    Write-Host "已备份 TrafficMonitor 配置到 $backupDir，并复制插件 DLL。请启动 TrafficMonitor 后在插件管理中启用“VPN 状态”。"
}
