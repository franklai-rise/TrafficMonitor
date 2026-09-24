# VPN 状态与 Codex 代理 1.2.0

TrafficMonitor 的 VPN 插件会启动自己的状态组件，持续采集本机 VPN、出口 IP 与地区；管理器只读取状态，并按按钮同步 Codex 代理变量。Clash、TiziGo 的启动、退出和切换由用户在各自软件中手动完成；管理器不会操作它们的进程、网卡、路由或 Windows 系统代理，也不会退出或重启 Codex。

## 使用顺序

1. 自行完全退出 Codex。
2. 自行切换 VPN 软件，等待管理器显示正确的当前连接。
3. 点对应的“同步 Codex”按钮，再重新打开 Codex。

Clash 按钮把当前用户的 `HTTP_PROXY`、`HTTPS_PROXY`、`ALL_PROXY` 设置为 `http://127.0.0.1:7890`。TiziGo 使用 TUN，不需要本地代理端口；TiziGo 按钮清除这三个变量。普通直连按钮也清除它们。这三个按钮只在观察到的 VPN 状态与所选目标一致、且 Codex 已退出时写入；TiziGo 与直连还会检查是否存在不能通过清除用户变量解决的机器级代理冲突。写入失败会尝试恢复原值。`NO_PROXY` 保持不变。这些是用户级变量，其他新启动的程序也可能继承；本程序不会修改已运行的 Codex 进程。

管理器取消开机自启动；TrafficMonitor 继续开机启动，其 VPN 插件在管理器关闭时照常更新。旧版 `--startup-direct` 启动参数在本版不再执行任何网络操作。关闭窗口会隐藏到托盘；托盘“退出管理器”真正结束进程。正在写入代理变量时，退出请求会在复核完成后生效。

插件的独立状态组件每 2 秒采集本机状态；可选的 ping0.cc 出口地区查询每 60 秒在当前路径上进行。Clash 的地区查询明确使用 7890，本机 TiziGo/直连路径不使用 HTTP 代理。查询不会启动或切换 VPN。原生插件只读取独立组件写入的本地原子快照；VPN 两行字体、颜色和对齐可用管理器中的“显示样式…”单独设置，设定保存在本机。

## 安装和验证

需要 Windows x64 和 .NET 10 Desktop Runtime。构建后可双击 `Install.cmd`，或运行：

```powershell
.\Install-Local.ps1 -InstallPlugin -InstallStartup -Restart
```

安装脚本先备份现有文件与配置，再通过独立计划任务部署管理器和插件状态组件，避免 Codex 的 AppData 隔离视图干扰。脚本删除旧 `VpnManager-Logon`，保留 `TrafficMonitor-Logon`。安装过程不切换当前 VPN，也不改变代理变量；真实 Codex 请求需在用户手动切换、同步并重新打开 Codex 后验证。

离线构建与检查：

```powershell
dotnet build .\VpnManager.sln -c Release
dotnet run --project .\tests\VpnManager.Core.Tests -c Release
dotnet publish .\src\VpnManager\VpnManager.csproj -c Release -r win-x64 --self-contained false -o .\artifacts\manager
dotnet publish .\StatusHost\VpnStatusHost.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o .\artifacts\status-host
```

插件可由 `TrafficMonitorPlugin\VpnStatusPlugin.vcxproj` 构建；原生回归程序位于 `tests\VpnStatusPlugin.Tests`。上述离线检查不会切换真实 VPN 或访问外网。安装结果在 `artifacts\install-result.json`，备份在本机管理器目录的 `backups` 中。
