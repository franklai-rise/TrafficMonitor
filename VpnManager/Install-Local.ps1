[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$InstallPlugin, [switch]$InstallStartup, [switch]$Restart,
    [switch]$Force, [switch]$Worker,
    [string]$ResultPath,
    [string]$TrafficRoot = 'E:\TrafficMonitor_V1.85_x64\TrafficMonitor'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSCommandPath
if (-not $ResultPath) { $ResultPath = Join-Path $root 'artifacts\install-result.json' }
$publish = Join-Path $root 'artifacts\manager'
$plugin = Join-Path $root 'artifacts\plugin\VpnStatusPlugin.dll'
$state = Join-Path $env:USERPROFILE 'AppData\Local\VpnManager'
$exe = Join-Path $state 'VpnManager.exe'
$trafficExe = Join-Path $TrafficRoot 'TrafficMonitor.exe'
if (-not (Test-Path -LiteralPath (Join-Path $publish 'VpnManager.exe'))) { throw '请先构建并发布管理器。' }
if ($InstallPlugin -and (-not (Test-Path -LiteralPath $plugin) -or -not (Test-Path -LiteralPath $trafficExe))) { throw '插件或 TrafficMonitor 主程序不存在。' }
if ($InstallStartup -and -not (Test-Path -LiteralPath $trafficExe)) { throw 'TrafficMonitor 主程序不存在。' }
if (-not $PSCmdlet.ShouldProcess($state, '备份并安装 VPN 管理器；不执行网络切换')) { return }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$admin = (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$flags = @()
foreach($name in @('InstallPlugin','InstallStartup','Restart','Force')) { if (Get-Variable -Name $name -ValueOnly) { $flags += "-$name" } }
$arguments = '-NoProfile -ExecutionPolicy Bypass -File "{0}" {1} -ResultPath "{2}" -TrafficRoot "{3}"' -f $PSCommandPath,($flags -join ' '),$ResultPath,$TrafficRoot
if (-not $admin) {
    Start-Process powershell.exe -Verb RunAs -WindowStyle Hidden -ArgumentList $arguments
    return
}
if (-not $Worker) {
    # A child of an MSIX app may inherit file virtualization even with no package identity.
    # Task Scheduler creates an independent process, also avoiding the Codex process lifetime.
    $taskName = 'VpnManager-Install-' + [Guid]::NewGuid().ToString('N')
    $action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -Argument ($arguments + ' -Worker') -WorkingDirectory $root
    $principal = New-ScheduledTaskPrincipal -UserId $identity.Name -LogonType Interactive -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)
    if (Test-Path -LiteralPath $ResultPath) { Move-Item -LiteralPath $ResultPath -Destination ($ResultPath+'.previous-'+(Get-Date -Format 'yyyyMMddHHmmss')) }
    try {
        Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Settings $settings | Out-Null
        Start-ScheduledTask -TaskName $taskName
        for($i=0;$i -lt 180;$i++) {
            Start-Sleep -Milliseconds 500
            if (Test-Path -LiteralPath $ResultPath) { Get-Content -LiteralPath $ResultPath -Raw; return }
        }
        throw "安装仍在后台运行。请查看结果文件：$ResultPath"
    } finally {
        # Removing the registration does not request termination of its running processes.
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    }
}
$tracePath=Join-Path $root 'artifacts\install-trace.log'
Start-Transcript -Path $tracePath -Append -Force | Out-Null
function Trace-Step([string]$message){Write-Host ((Get-Date -Format 'HH:mm:ss')+' '+$message)}
Trace-Step 'Worker started'
$backup = Join-Path $state ('backups\install-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$taskChanges = New-Object 'System.Collections.Generic.List[object]'
$runChanges = New-Object 'System.Collections.Generic.List[object]'
$replaced = New-Object 'System.Collections.Generic.List[object]'
$stoppedManager = $false
$stoppedTraffic = $false
$success = $false
$installMutex=$null; $ownsInstallMutex=$false
$result = [ordered]@{success=$false; version='1.1.3'; backup=$backup; networkChanged=$false}
try {
    $installMutex=New-Object Threading.Mutex($false,'Local\VpnManager.Installation')
    try {$ownsInstallMutex=$installMutex.WaitOne(0)} catch [Threading.AbandonedMutexException] {$ownsInstallMutex=$true}
    if(-not $ownsInstallMutex){throw '另一次安装正在进行。'}
    Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices; using Microsoft.Win32.SafeHandles;
public static class InstallNative {
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] public static extern uint GetFinalPathNameByHandle(SafeFileHandle h,StringBuilder b,uint size,uint flags);
 public delegate bool EnumProc(IntPtr h,IntPtr p);
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc callback,IntPtr p);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint id);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h,StringBuilder b,int size);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern IntPtr SendMessageTimeout(IntPtr h,uint msg,IntPtr w,IntPtr l,uint flags,uint timeout,out IntPtr result);
 public static void CloseTraffic(int pid) {
  EnumWindows((h,p)=>{uint id;GetWindowThreadProcessId(h,out id);if(id==pid){var b=new StringBuilder(256);GetWindowText(h,b,256);if(b.ToString()=="TrafficMonitor"){IntPtr r;SendMessageTimeout(h,0x10,IntPtr.Zero,IntPtr.Zero,2,3000,out r);}}return true;},IntPtr.Zero);
 }
}
"@
    function Real-Path([string]$path) {
        $file=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        try {$b=New-Object Text.StringBuilder 4096;[void][InstallNative]::GetFinalPathNameByHandle($file.SafeFileHandle,$b,4096,0);return $b.ToString().Replace('\\?\','')}
        finally {$file.Dispose()}
    }
    New-Item -ItemType Directory -Path $state -Force | Out-Null
    $probe=Join-Path $state ('.install-probe-'+[Guid]::NewGuid().ToString('N'))
    try {
        [IO.File]::WriteAllText($probe,'install-path-check')
        $real=Real-Path $probe
        if ($real -ne $probe) { throw "安装目录被重定向，已停止：$real" }
    } finally { if(Test-Path -LiteralPath $probe){Remove-Item -LiteralPath $probe -Force} }
    $result.actualInstallDirectory=$state
    New-Item -ItemType Directory -Path $backup -Force | Out-Null
    function Deploy-File([string]$source,[string]$destination,[string]$tag) {
        $parent=Split-Path -Parent $destination
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
        $old=Join-Path $backup $tag
        $existed=Test-Path -LiteralPath $destination
        if($existed){Copy-Item -LiteralPath $destination -Destination $old -Force}
        $replaced.Add([pscustomobject]@{destination=$destination;old=$old;existed=$existed})
        Copy-Item -LiteralPath $source -Destination $destination -Force
        if((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $destination).Hash){throw "文件校验失败：$destination"}
    }
    foreach($name in @('VpnManager.exe','VpnManager.dll','VpnManager.deps.json','VpnManager.runtimeconfig.json','VpnManager.Core.dll')){if(-not(Test-Path -LiteralPath (Join-Path $publish $name))){throw "发布文件不完整：$name"}}
    function Network-Fingerprint {
        $processes=@(Get-Process -ErrorAction SilentlyContinue | Where-Object {$_.ProcessName -in @('Clash for Windows','clash-win64','clash-core-service','TiziGo','sing-box')} | Sort-Object Id | ForEach-Object {"$($_.Id):$($_.ProcessName)"})
        $values=@(foreach($scope in @('User','Machine')){foreach($name in @('HTTP_PROXY','HTTPS_PROXY','ALL_PROXY','NO_PROXY')){[Environment]::GetEnvironmentVariable($name,$scope)}})
        $sha=[Security.Cryptography.SHA256]::Create()
        try {$hash=[BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($values | ConvertTo-Json -Compress))))}
        finally {$sha.Dispose()}
        return [pscustomobject]@{vpnProcesses=$processes;proxyFingerprint=$hash}
    }
    $result.beforeNetwork=Network-Fingerprint
    Trace-Step 'Checking manager'
    $manager = @(Get-Process VpnManager -ErrorAction SilentlyContinue)
    foreach($process in $manager) {
        if($process.Path -ne $exe){throw "另一路径的管理器正在运行，请先手动退出：$($process.Path)"}
        if(-not $Restart){throw '管理器正在运行，请使用 -Restart 或先从托盘退出。'}
        # New versions support cooperative shutdown and refuse shutdown while switching.
        Trace-Step 'Requesting cooperative shutdown'
        try {$event=[Threading.EventWaitHandle]::OpenExisting('Local\VpnManager.Shutdown');[void]$event.Set();$event.Dispose();if(-not $process.WaitForExit(5000)){throw '管理器未同意退出，可能正在切换。'}}
        catch [Threading.WaitHandleCannotBeOpenedException] {
            # Older versions lack the shutdown event. Only replace an observed idle UI.
            Add-Type -AssemblyName UIAutomationClient
            Add-Type -AssemblyName UIAutomationTypes
            if($process.MainWindowHandle -eq 0){throw '旧管理器隐藏在托盘中，请显示窗口后重试更新。'}
            $window=[System.Windows.Automation.AutomationElement]::FromHandle($process.MainWindowHandle)
            foreach($id in @('ClashButton','TiziGoButton','DirectButton')){
                $condition=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
                $button=$window.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$condition)
                if(-not $button -or -not $button.Current.IsEnabled){throw '旧管理器可能正在切换，已中止更新。'}
            }
            Stop-Process -Id $process.Id
            if(-not $process.WaitForExit(5000)){throw '旧管理器尚未退出。'}
        }
        $stoppedManager=$true
        Trace-Step 'Manager shutdown completed'
    }
    Trace-Step 'Manager stopped, checking TrafficMonitor'
    if($InstallPlugin) {
        foreach($process in @(Get-Process TrafficMonitor -ErrorAction SilentlyContinue)) {
            if($process.Path -ne $trafficExe){throw '另一路径的 TrafficMonitor 正在运行。'}
            if(-not $Restart){throw 'TrafficMonitor 正在运行，请使用 -Restart 或先退出。'}
            Trace-Step 'Requesting TrafficMonitor exit'
            [InstallNative]::CloseTraffic($process.Id)
            if(-not $process.WaitForExit(8000)){throw 'TrafficMonitor 未正常退出，未强制终止。'}
            $stoppedTraffic=$true
            Trace-Step 'TrafficMonitor exit completed'
        }
        $config=Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'TrafficMonitor\config.ini'
        if(Test-Path -LiteralPath $config){Copy-Item -LiteralPath $config -Destination (Join-Path $backup 'TrafficMonitor-config.ini')}
        Deploy-File $plugin (Join-Path $TrafficRoot 'plugins\VpnStatusPlugin.dll') 'VpnStatusPlugin.dll'
    }
    Trace-Step 'Plugin copied, installing manager files'
    $allowed=@('VpnManager.exe','VpnManager.dll','VpnManager.deps.json','VpnManager.runtimeconfig.json','VpnManager.Core.dll')
    foreach($name in $allowed) {
        $source=Join-Path $publish $name
        if(-not(Test-Path -LiteralPath $source)){throw "发布文件不完整：$name"}
        Deploy-File $source (Join-Path $state $name) $name
    }
    $icon=Join-Path $root 'src\VpnManager\Assets\VpnManager.ico'
    Deploy-File $icon (Join-Path $state 'VpnManager.ico') 'VpnManager.ico'
    Deploy-File $icon (Join-Path $state 'VpnManager-loop-v1.ico') 'VpnManager-loop-v1.ico'
    New-Item -ItemType Directory -Path (Join-Path $state 'Assets') -Force | Out-Null
    Deploy-File $icon (Join-Path $state 'Assets\VpnManager.ico') 'Assets-VpnManager.ico'
    # Preserve all display values, migrate old UTF-8 INI so Win32 reads Chinese fonts correctly.
    $style=Join-Path $state 'vpn-display-settings.ini'
    if(Test-Path -LiteralPath $style) {
        Copy-Item -LiteralPath $style -Destination (Join-Path $backup 'vpn-display-settings.ini')
        $replaced.Add([pscustomobject]@{destination=$style;old=(Join-Path $backup 'vpn-display-settings.ini');existed=$true})
        $text=[IO.File]::ReadAllText($style)
        [IO.File]::WriteAllText($style,$text,[Text.Encoding]::Unicode)
    }
    $desktop=[Environment]::GetFolderPath('Desktop')
    $shortcutPath=Join-Path $desktop 'VPN 管理器.lnk'
    if(Test-Path -LiteralPath $shortcutPath){Copy-Item -LiteralPath $shortcutPath -Destination (Join-Path $backup 'VPN 管理器.lnk')}
    $replaced.Add([pscustomobject]@{destination=$shortcutPath;old=(Join-Path $backup 'VPN 管理器.lnk');existed=(Test-Path -LiteralPath $shortcutPath)})
    $shell=New-Object -ComObject WScript.Shell
    $shortcut=$shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath=$exe;$shortcut.Arguments='';$shortcut.WorkingDirectory=$state
    $shortcut.IconLocation=(Join-Path $state 'VpnManager-loop-v1.ico')+',0'
    $shortcut.Description='VPN 管理器 1.1.3（普通打开仅刷新状态）';$shortcut.Save()
    $result.shortcut=$shortcutPath
    Trace-Step 'Files installed, configuring startup'
    if($InstallStartup) {
        # TrafficMonitor can create its own per-user scheduled task. Keeping it beside
        # TrafficMonitor-Logon starts two instances at sign-in and triggers the
        # "already running" dialog, so preserve it for rollback and remove it.
        $nativeTrafficTaskName='Autorun for '+$env:USERNAME
        $nativeTrafficTaskPath='\TrafficMonitor\'
        $nativeTrafficTask=Get-ScheduledTask -TaskPath $nativeTrafficTaskPath -TaskName $nativeTrafficTaskName -ErrorAction SilentlyContinue
        if($nativeTrafficTask){
            $nativeXml=Export-ScheduledTask -TaskPath $nativeTrafficTaskPath -TaskName $nativeTrafficTaskName
            $nativeXml | Set-Content -LiteralPath (Join-Path $backup 'TrafficMonitor-native-autorun.xml') -Encoding Unicode
            $taskChanges.Add([pscustomobject]@{name=$nativeTrafficTaskName;path=$nativeTrafficTaskPath;xml=$nativeXml})
            Unregister-ScheduledTask -TaskPath $nativeTrafficTaskPath -TaskName $nativeTrafficTaskName -Confirm:$false
        }
        $principal=New-ScheduledTaskPrincipal -UserId $identity.Name -LogonType Interactive -RunLevel Highest
        $settings=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
        $trigger=New-ScheduledTaskTrigger -AtLogOn -User $identity.Name
        foreach($entry in @(@('VpnManager-Logon',$exe,'--startup-direct'),@('TrafficMonitor-Logon',$trafficExe,''))) {
            $existing=Get-ScheduledTask -TaskName $entry[0] -ErrorAction SilentlyContinue
            if($existing){Export-ScheduledTask -TaskName $entry[0] | Set-Content -LiteralPath (Join-Path $backup ($entry[0]+'.xml')) -Encoding Unicode}
            $action=New-ScheduledTaskAction -Execute $entry[1] -WorkingDirectory (Split-Path -Parent $entry[1])
            if($entry[2]){$action.Arguments=$entry[2]}
            $oldXml=if($existing){Export-ScheduledTask -TaskName $entry[0]}else{$null}
            $taskChanges.Add([pscustomobject]@{name=$entry[0];path='\';xml=$oldXml})
            Register-ScheduledTask -TaskName $entry[0] -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
        }
        $run='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
        foreach($name in @('VpnManager','TrafficMonitor')) {
            $properties=Get-ItemProperty -LiteralPath $run -ErrorAction SilentlyContinue
            $property=$properties.PSObject.Properties[$name]
            $value=if($property){$property.Value}else{$null}
            if($value -and $value -like ('*'+$name+'.exe*')) {
                [pscustomobject]@{name=$name;value=$value} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $backup ($name+'-old-run.json')) -Encoding UTF8
                $runChanges.Add([pscustomobject]@{name=$name;value=$value})
                Remove-ItemProperty -LiteralPath $run -Name $name
            }
        }
        $result.startup='Highest interactive logon tasks; Direct applies only at next login, subject to Codex guard'
    }
    $result.executableActualPath=Real-Path $exe
    if($result.executableActualPath -ne $exe){throw '最终安装路径校验失败。'}
    $result.managerHash=(Get-FileHash -LiteralPath (Join-Path $state 'VpnManager.dll')).Hash
    $success=$true;$result.success=$true
} catch {
    $result.error=$_.Exception.Message
    Trace-Step ("Failure: "+$result.error)
    $rollbackErrors=@()
    for($i=$replaced.Count-1;$i -ge 0;$i--) {
        $item=$replaced[$i]
        try {if($item.existed){Copy-Item -LiteralPath $item.old -Destination $item.destination -Force}else{Remove-Item -LiteralPath $item.destination -Force -ErrorAction SilentlyContinue}}
        catch {$rollbackErrors += $_.Exception.Message}
    }
    foreach($change in $taskChanges) {
        try {if($change.xml){Register-ScheduledTask -TaskPath $change.path -TaskName $change.name -Xml $change.xml -Force | Out-Null}else{Unregister-ScheduledTask -TaskPath $change.path -TaskName $change.name -Confirm:$false}}
        catch {$rollbackErrors += $_.Exception.Message}
    }
    foreach($change in $runChanges) {
        try {New-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name $change.name -Value $change.value -PropertyType String -Force | Out-Null}
        catch {$rollbackErrors += $_.Exception.Message}
    }
    $result.rollbackErrors=$rollbackErrors
} finally {
    try {
        if(($success -and $Restart) -or $stoppedManager){Start-Process -FilePath $exe -WorkingDirectory $state}
        if(($success -and $Restart -and $InstallPlugin) -or $stoppedTraffic){Start-Process -FilePath $trafficExe -WorkingDirectory $TrafficRoot}
    } catch {$result.restartError=$_.Exception.Message;$result.success=$false}
    Trace-Step 'Saving result'
    if($ownsInstallMutex){$installMutex.ReleaseMutex()};if($installMutex){$installMutex.Dispose()}
    if(Get-Command Network-Fingerprint -ErrorAction SilentlyContinue){
        $result.afterNetwork=Network-Fingerprint
        $result.networkChanged=($result.beforeNetwork.proxyFingerprint -ne $result.afterNetwork.proxyFingerprint) -or (($result.beforeNetwork.vpnProcesses -join ',') -ne ($result.afterNetwork.vpnProcesses -join ','))
    }
    $result.completedAt=(Get-Date).ToString('o')
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $ResultPath -Encoding UTF8
}
if(-not $result.success){throw "安装未完成，详情：$ResultPath"}
