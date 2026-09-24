[CmdletBinding()]
param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSCommandPath
$hostProject = Join-Path $root 'Host\CodexRadarHost.csproj'
$regressionProject = Join-Path $root 'Tests\CodexRadarHost.Regression.csproj'
$pluginProject = Join-Path $root 'Plugin\RadarScorePlugin.vcxproj'
$pluginTestsProject = Join-Path $root 'Tests\Plugin\RadarScorePlugin.Tests.vcxproj'
$hostOutput = Join-Path $root 'artifacts\host'

dotnet publish $hostProject -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o $hostOutput
if ($LASTEXITCODE -ne 0) { throw 'GPT 雷达后台程序发布失败。' }

$vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
$vsInstall = if (Test-Path -LiteralPath $vswhere) { & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath } else { $null }
if (-not $vsInstall) { $vsInstall = 'D:\abaqus\VS' }
$vcvars = Join-Path $vsInstall 'VC\Auxiliary\Build\vcvarsall.bat'
$msbuild = Join-Path $vsInstall 'MSBuild\Current\Bin\MSBuild.exe'
if (-not (Test-Path -LiteralPath $vcvars) -or -not (Test-Path -LiteralPath $msbuild)) { throw '找不到 Visual C++ x64 构建工具。' }

foreach ($project in @($pluginProject, $pluginTestsProject)) {
    $command = 'call "' + $vcvars + '" x64 && "' + $msbuild + '" "' + $project + '" /m /p:Configuration=Release /p:Platform=x64'
    & cmd.exe /d /c $command
    if ($LASTEXITCODE -ne 0) { throw "原生项目构建失败：$project" }
}

if (-not $SkipTests) {
    dotnet build $regressionProject -c Release
    if ($LASTEXITCODE -ne 0) { throw '评分缓存回归程序构建失败。' }
    dotnet run --project $regressionProject -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw '评分缓存回归验证失败。' }
    $pluginTest = Join-Path $root 'artifacts\plugin-tests\RadarScorePlugin.Tests.exe'
    & $pluginTest
    if ($LASTEXITCODE -ne 0) { throw 'TrafficMonitor 插件回归验证失败。' }
}

Write-Host "Build complete: $root\artifacts"
