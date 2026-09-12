# CS2 DemoTracer v1.2.2

This release improves replay stability, input reconstruction, and plugin compatibility. The GUI and the complete Playback/CSS bundle are both updated to v1.2.2.

## GUI

- Fixed button-change and subtick parsing in some demos to improve input reconstruction.
- Improved playback component installation, repair, and rollback while preserving user configuration and recordings.
- Improved environment diagnostics for mismatched components, duplicate plugins, and conflict sources.

## CSSharp plugins and native runtimes

- Fixed a crash that could occur during plugin initialization.
- Fixed repeated cleanup during stop, unload, and handoff so it does not interfere with human takeover or control acquired by other bot plugins.
- Fixed event, projectile, and automatic voice/chat synchronization in subsequent replay loops.
- Fixed identity and cosmetic state changes on disconnect, team changes, or takeover affecting other bots.
- Improved Randomizer settings and round-intro agent presentation, prioritizing appearance explicitly recorded in the demo.
- Removed redundant team assignment, respawn, and global state cleanup to reduce interference between plugins.

## Updating

- Update the GUI and the complete playback bundle together, including both CSSharp plugins and native runtimes.
- Existing replay archives remain playable. Newly converted archives use `.dtr v10` and require the updated playback bundle.
- Reconvert the original demo to benefit from the input parsing fixes. Recreating existing archives is otherwise optional.

---

本次更新改进回放稳定性、输入还原和插件兼容性。GUI 与完整 Playback/CSS 组件包均升级至 v1.2.2。

## GUI

- 修复部分 Demo 的按键变化和子 Tick 数据解析，改善动作输入的还原。
- 改进播放组件的安装、修复与回滚，保留用户配置和录制数据。
- 改进环境诊断，更准确地识别组件不配套、重复插件及冲突来源。

## CSSharp 插件及配套运行库

- 修复插件初始化期间可能发生的崩溃。
- 修复停止、卸载和交还控制时的重复清理，避免干扰真人接管或其他 BOT 插件的控制。
- 修复循环回放第二轮起事件、投掷物及自动语音／聊天的同步问题。
- 修复断线、换队和接管时，单个 BOT 的身份或饰品状态影响其他 BOT 的问题。
- 改进 Randomizer 随机化开关和回合开场探员显示，优先使用 Demo 中明确记录的外观。
- 移除多余的自动分队、复活和全局状态清理，减少插件之间的相互干扰。

## 更新须知

- GUI 与完整播放组件包需要配套升级，包含 CSSharp 插件和原生运行库。
- 旧回放档案仍可播放；新转换的档案使用 `.dtr v10`，需要新版播放组件。
- 要获得本次输入解析修复，需要从原始 Demo 重新转换；旧档案无需强制重转。
