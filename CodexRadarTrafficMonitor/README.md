# TrafficMonitor GPT 雷达评分

独立的 x64 TrafficMonitor 插件和本地后台程序。后台读取众测雷达公开 API，每分钟缓存 DeepSWE 与庞贝壁画两个频道；插件只读本地快照。它不需要 API 密钥，也不访问 VPN 配置。后台程序需要本机 .NET 10 Desktop Runtime。

任务栏用 `A91m S92h L85h` 这样的紧凑格式显示 Astra、Sol、Luna 的综合评分和选中档位。评分在任务栏取整，`l/m/h/x` 分别表示 low/medium/high/xhigh；完整小数、样本和更新时间在悬停信息中。单击无动作，双击打开缓存详情。详情包含雷达公开的全部 Codex 原生 GPT 模型、推理强度、分数、样本、运行量、参考价格、耗时、步骤、Token、缓存命中率和更新时间。

## 构建和验证

在 Windows x64、.NET 10 SDK、Visual C++ 2019 x64 工具已安装的机器上运行完整构建入口：

```powershell
.\build.ps1
```

构建会发布单文件后台程序并编译插件与回归程序。默认运行离线解析/缓存检查和原生插件检查。单独验证实时公开接口：

```powershell
dotnet run --project .\Tests\CodexRadarHost.Regression.csproj -c Release -- --live
```

## 安装

先运行 `build.ps1`，然后：

```powershell
.\Install.ps1 -RestartTrafficMonitor
```

脚本会备份 `%APPDATA%\TrafficMonitor\config.ini`、可能已存在的评分插件和后台程序，部署到 `E:\TrafficMonitor_V1.85_x64\TrafficMonitor\plugins` 与 `%USERPROFILE%\AppData\Local\CodexRadarTrafficMonitor`，并在 `plugin_display_item` 中追加自己的项目 ID。它保留现有的任务栏排列、项目间距和 VPN 项目设置。安装程序通过独立的 Windows 计划任务完成部署及重启，避免从 Codex 内启动的 TrafficMonitor 读到隔离目录中的旧 VPN 状态。已有 TrafficMonitor 进程时，重启前发送正常关闭请求；关闭失败时中止，不强制结束进程。传入 `-RestartTrafficMonitor` 后，安装完成会启动 TrafficMonitor。安装结果和备份保存在本机 GPT 雷达目录。

若 TrafficMonitor 使用其他目录，传入 `-TrafficRoot`。仅预览操作可使用 `-WhatIf`。

## 数据处理

- 接口频道和 `equal_latest_3` 模式经校验后才进入缓存。
- 模型总分使用其推理强度的 `passed / total` 合并计算，满分 150；零样本保留为“暂无数据”。
- 网络失败时保留上次成绩，状态标为过期并每分钟重试。连续五分钟没有成功数据时，即使后台进程退出，任务栏也会根据缓存时间自行标记过期。
- 评分和详情均来自公开数据。价格为 API 等价参考值，不代表订阅实际扣费。

## 撤销

关闭 TrafficMonitor 后运行：

```powershell
.\Uninstall.ps1
```

卸载脚本备份配置及已安装文件，只移除 GPT 雷达项目 ID 和这两个文件。默认保留评分缓存和日志；传入 `-RemoveCache` 会同时删除它们。
