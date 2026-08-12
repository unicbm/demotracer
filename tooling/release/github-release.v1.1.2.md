## Highlights

- Added anonymous aggregate statistics for demo-source distribution and analysis/conversion reliability. No linkable identifier is included, and the backend stores only aggregate counters.
- Added optional active-user estimates, disabled by default and using only a daily rotating identifier when enabled.
- Replaced the blocking telemetry dialog with a compact, non-blocking prompt.
- Added separate settings switches for aggregate statistics and active-user estimates.
- Demo content, file paths, raw server information, SteamIDs, logs, voice data, and stable device IDs are never uploaded.

## 主要更新

- 新增匿名聚合统计，用于了解 Demo 来源分布及分析、转换可靠性；不包含可关联标识，后端仅保存聚合计数。
- 新增可选的活跃人数统计，默认关闭；开启后仅使用每日轮换标识估算在线人数与日活。
- 取消阻塞式遥测弹窗，改为简洁的非阻塞提示。
- 设置页新增“匿名聚合统计”和“活跃人数统计”两个独立开关。
- 不会上传 Demo 内容、文件路径、原始服务器信息、SteamID、日志、语音或稳定设备 ID。
