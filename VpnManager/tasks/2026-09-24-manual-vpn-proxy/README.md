# 2026-09-24 手动 VPN 与独立状态组件

本次把管理器改为手动同步 Codex 用户级代理：用户自行退出 Codex、自行切换 Clash 或 TiziGo，再按对应按钮。Clash 设置 127.0.0.1:7890；TiziGo TUN 和普通直连清除三个代理变量。程序不启动、退出或切换 VPN，也不修改 Windows 系统代理。离线测试覆盖目标状态不匹配、Codex 未退出、端口归属错误、机器级代理冲突及写入失败恢复。

TrafficMonitor 的 VPN 插件启动独立 `VpnStatusHost.exe`，后者每两秒采集本机状态，并在启用时约每 60 秒通过当前路径查询 ping0.cc。管理器仅读取快照。管理器开机任务已删除，TrafficMonitor 开机任务保留。

部署备份位于管理器真实 LocalAppData 的 `backups/install-20260924-105531`。安装结果 `artifacts/install-result.json` 报告成功，且前后 VPN 进程和代理变量指纹一致。管理器退出后，独立计划任务从真实 LocalAppData 读取两次快照，时间间隔超过 12 秒，均为 Clash 状态；当时管理器 0 个、TrafficMonitor 1 个、状态组件 1 个，快照采集时间距检查不足 1 秒。完整结果保存在本目录 `installed-verification.json`，其中含当时出口 IP，不应提交公开仓库。

离线检查：.NET 解决方案构建成功，50 个 Core 检查通过，原生 VPN 插件和原生显示测试构建并通过。未执行真实 VPN 切换。
