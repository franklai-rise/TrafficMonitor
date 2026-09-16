# VPN 管理器

新版位于此目录，未改动旧版 `F:\DeepSeek_Work\Stuff\vpn-switcher` 或桌面快捷方式。

## 行为

- 只在点击“切换到 Clash”或“切换到 TiziGo”时改变 VPN 与用户级代理变量。
- 检测到 Codex 正在运行时，默认拒绝切换；不会强制退出或重启 Codex。
- 只对指定的 Clash/TiziGo 主程序发送正常关闭请求，不按通用进程名或端口强杀。
- 目标 VPN 就绪后才停止旧 VPN；最终路由或端口不完整则恢复本次环境变量快照，并尝试恢复原模式。
- 状态刷新每 2 秒写入原子 JSON 快照，TrafficMonitor 插件只读该快照。当前版本不主动查询公网 IP，因此页面明确显示“未刷新”。

## 构建与部署

```powershell
dotnet build .\VpnManager.sln -c Release
& 'D:\abaqus\VS\MSBuild\Current\Bin\MSBuild.exe' .\TrafficMonitorPlugin\VpnStatusPlugin.vcxproj /p:Configuration=Release /p:Platform=x64
dotnet publish .\src\VpnManager\VpnManager.csproj -c Release -r win-x64 --self-contained false -o .\artifacts\manager
.\Install-Local.ps1
```

管理器部署后，若需要安装 TrafficMonitor 插件，先退出 TrafficMonitor，再执行：

```powershell
.\Install-Local.ps1 -InstallPlugin
```

脚本会先备份其配置。插件可通过删除 `E:\TrafficMonitor_V1.85_x64\TrafficMonitor\plugins\VpnStatusPlugin.dll` 撤销；管理器本身不管理 Windows 系统代理。

真实双向切换尚未在本次依赖 VPN 的会话中运行。请在空闲时段手动验收两种切换、失败恢复和重新打开 Codex 后的一次请求。
