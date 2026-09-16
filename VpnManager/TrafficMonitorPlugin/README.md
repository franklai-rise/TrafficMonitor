# TrafficMonitor VPN 状态插件

该插件只读取 `%LOCALAPPDATA%\VpnManager\vpn-status.json`。它不读取 Clash 密钥、不调用 VPN 控制接口，也不切换或启动任何 VPN。

管理器持续运行时，插件显示 `国家 · 接入方式 · 软件`；快照超过 10 秒未更新时显示“VPN 状态过期”。单击状态项会打开已部署的管理器。

部署由项目根目录的 `Install-Local.ps1` 处理：先备份 TrafficMonitor 配置，再复制 DLL；不重启 TrafficMonitor，也不修改现有显示项目。请在 TrafficMonitor 退出后执行部署，再手动在“更多功能 → 插件管理”启用“VPN 状态”。
