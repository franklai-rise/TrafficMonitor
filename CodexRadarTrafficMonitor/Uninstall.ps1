[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$TrafficRoot = 'E:\TrafficMonitor_V1.85_x64\TrafficMonitor',
    [switch]$RemoveCache
)

$ErrorActionPreference = 'Stop'
$itemId = 'codex-radar-score-v1'
$plugin = Join-Path $TrafficRoot 'plugins\RadarScorePlugin.dll'
$config = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'TrafficMonitor\config.ini'
$stateDirectory = Join-Path ([Environment]::GetFolderPath('UserProfile')) 'AppData\Local\CodexRadarTrafficMonitor'
$host = Join-Path $stateDirectory 'CodexRadarHost.exe'
if (-not $PSCmdlet.ShouldProcess($TrafficRoot, '移除 GPT 雷达插件和后台程序')) { return }

$backupDirectory = Join-Path $stateDirectory ('backups\remove-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $backupDirectory -Force | Out-Null
if (Test-Path -LiteralPath $config) { Copy-Item -LiteralPath $config -Destination (Join-Path $backupDirectory 'TrafficMonitor-config.ini') }
if (Test-Path -LiteralPath $plugin) { Copy-Item -LiteralPath $plugin -Destination (Join-Path $backupDirectory 'RadarScorePlugin.dll') }
if (Test-Path -LiteralPath $host) { Copy-Item -LiteralPath $host -Destination (Join-Path $backupDirectory 'CodexRadarHost.exe') }

if (Test-Path -LiteralPath $config) {
    $bytes = [IO.File]::ReadAllBytes($config)
    if ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) {
        $encoding = [Text.Encoding]::Unicode; $preambleLength = 2
    } elseif ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        $encoding = New-Object Text.UTF8Encoding($true); $preambleLength = 3
    } else {
        $encoding = New-Object Text.UTF8Encoding($false); $preambleLength = 0
    }
    $text = $encoding.GetString($bytes, $preambleLength, $bytes.Length - $preambleLength)
    $match = [regex]::Match($text, '(?m)^(plugin_display_item\s*=\s*)([^\r\n]*)')
    if ($match.Success) {
        $items = @($match.Groups[2].Value -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ -and $_ -ne $itemId })
        $updated = $text.Substring(0, $match.Groups[2].Index) + ($items -join ',') + $text.Substring($match.Groups[2].Index + $match.Groups[2].Length)
        $temp = $config + '.tmp-' + [Guid]::NewGuid().ToString('N')
        try {
            [IO.File]::WriteAllText($temp, $updated, $encoding)
            [IO.File]::Replace($temp, $config, $null, $true)
        } finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force } }
    }
}
if (Test-Path -LiteralPath $plugin) { Remove-Item -LiteralPath $plugin -Force }
if (Test-Path -LiteralPath $host) { Remove-Item -LiteralPath $host -Force }
if ($RemoveCache) {
    foreach ($name in @('radar-cache.json', 'status.ini', 'logs')) {
        $path = Join-Path $stateDirectory $name
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    }
}
Write-Output "GPT 雷达插件已移除。备份：$backupDirectory"
