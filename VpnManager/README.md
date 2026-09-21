# VPN 管理器 1.1.1

独立 WPF 管理器和 TrafficMonitor x64 状态插件。保留 DeepSeek 的 Clash 路径发现、管理员清单和操作日志，修正切换恢复、并发、刷新和部署检查。

## 使用

普通打开只读取状态，不启动或停止 VPN。关闭窗口会隐藏到托盘；从托盘“退出”才结束管理器。插件只读快照，单击只打开管理器。

切换前退出 Codex。选择 Clash / TiziGo 时可以启动相应客户端；客户端仍须安装并配置好。“关闭 VPN（直连）”退出已核实的 VPN 并清除三个用户级代理变量。不修改 Windows 系统代理或 NO_PROXY，不退出或重启 Codex。

每 2 秒采集本机状态；联网地区查询在独立异步任务中每 60 秒进行，不阻塞本机快照。可关闭“刷新出口 IP 和地区”。查询失败保留旧结果及采集时间，超过 90 秒标为过期。手动刷新有按钮反馈和高亮结果。

任务栏按两行显示地区、接入方式/端口和软件。“显示样式…”只影响 VPN 项目，保存字体、字号、颜色、对齐。两行字号受实际任务栏高度限制，防止覆盖。规则模式标为“规则分流”；地区来自此次明确路径的出口查询，不能代表每个应用或所有分流请求。

## 安装和更新

需要 Windows x64、.NET 10 Desktop Runtime。发布目录为 artifacts/manager 和 artifacts/plugin。双击 Install.cmd，或：

~~~powershell
.\Install-Local.ps1 -InstallPlugin -InstallStartup -Restart
~~~

默认 TrafficMonitor 路径可用 -TrafficRoot 指定；-WhatIf 只预览。安装脚本为 UTF-8 BOM，兼容 Windows PowerShell 5.1。

安装请求管理员权限，通过临时计划任务建立独立进程。MSIX 子进程即使没有包身份，仍可能继承文件重定向；显式 USERPROFILE 路径或 Resolve-Path 不足以证明真实安装位置。本版核查文件句柄的实际路径及文件哈希，结果写入 artifacts/install-result.json。

安装目录为 %USERPROFILE%\AppData\Local\VpnManager，快捷方式放在 Windows 配置的桌面。更新保留显示样式和联网刷新开关。普通重启不传 --startup-direct，保持当前网络。

普通直连状态也会按联网刷新设置访问 ping0.cc，显示当前出口地区、IP 和“普通直连”。它只观察当前路径，不会为了刷新而启动或切换 VPN。

-InstallStartup 注册当前用户登录、最高权限的 VpnManager-Logon 和 TrafficMonitor-Logon 任务。管理器的 --startup-direct 仅在下次登录的新进程执行一次，仍受 Codex 运行检查保护。重复打开、重新显示隐藏窗口和安装后重启都不执行直连。不要在当前 VPN 会话中手动运行登录任务。安装成功后移除对应旧 HKCU Run 项，避免管理员清单造成自启动失败或重复启动。

旧管理器只有在确认切换按钮可用时才能更新；隐藏时需先显示窗口，正在切换时应等待。新版支持协作退出，切换期间拒绝退出。

## 切换保护和局限

1. 检查 Codex、端口归属、网卡/路由、机器级代理冲突。
2. 记录本次状态及环境变量，启动目标，检查本机路径和基础 HTTPS。
3. 退出旧 VPN，再检查目标独立路径和 HTTPS，通过后提交代理变量。
4. 失败时恢复原变量，清理新目标并恢复原 VPN；复核恢复结果，失败时明确要求手动恢复。

基础 HTTPS 只说明收到有效 TLS/HTTP 响应，不证明 Codex 登录、WebSocket 或模型请求成功。401/403 不会被称为 Codex 已验证。Clash 检查显式使用 127.0.0.1:7890；TUN/直连检查禁用 HttpClient 代理继承。

只操作已核实目录内、白名单名称的残留 VPN 内核；7890 属于其他程序时不会结束它。若关闭窗口只是隐藏到托盘，会中止并提示从客户端托盘退出，避免假报成功。

不保证切换零中断。真实双向切换、失败恢复、重启电脑及重新打开 Codex 请求，仍需在不依赖当前 VPN 的时段验收。

## 构建与验证

~~~powershell
dotnet build .\VpnManager.sln -c Release
dotnet run --project .\tests\VpnManager.Core.Tests -c Release
dotnet publish .\src\VpnManager\VpnManager.csproj -c Release -r win-x64 --self-contained false -o .\artifacts\manager
msbuild .\TrafficMonitorPlugin\VpnStatusPlugin.vcxproj /p:Configuration=Release /p:Platform=x64
msbuild .\tests\VpnStatusPlugin.Tests\VpnStatusPlugin.Tests.vcxproj /p:Configuration=Release /p:Platform=x64
.\artifacts\plugin-tests\VpnStatusPlugin.Tests.exe
~~~

原生项目默认 v142，VS 2022 可传 /p:PlatformToolset=v143。仓库自动使用顶层 include/PluginInterface.h；独立目录可传 /p:TrafficMonitorIncludePath=路径。

31 项核心离线检查使用模拟进程、路由、HTTP 和环境变量，不控制真实 VPN。原生测试使用临时目录，覆盖 3,000 字符中文详情、字符串转义、中文/emoji、缺失/过期/版本不符、两行裁剪和并发读取。

日志在安装目录 logs/operations.log，超过 1 MB 轮转。状态快照使用唯一临时文件、串行写入和原子替换；插件共享读取允许替换，防止偶发写入失败。控制接口令牌不写日志，也不发到非回环地址。

## 撤销

安装前文件和 TrafficMonitor 配置保存到安装目录 backups/install-时间。退出两程序后可恢复备份二进制；只撤销插件则移除 plugins/VpnStatusPlugin.dll。自启动可在任务计划程序禁用上述两任务。撤销文件更新本身不会切换 VPN 或修改代理变量。
