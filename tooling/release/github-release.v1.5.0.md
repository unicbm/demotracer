# CS2 DemoTracer v1.5.0

The GUI and complete playback bundle are updated together to v1.5.0. This release streamlines the desktop workflow, fixes replay behavior, and moves the playback runtimes to the shared KHook engine.

- **Desktop and loading**: Simplify conversion, batch tasks, the replay library, and settings. Show library scan results progressively and load player and cosmetic catalogs on demand, reducing eager loading and repeated work.
- **Parsing and presentation**: Reduce repeated processing and memory use during Demo parsing and cosmetic evidence handling. Support newer crosshair share-code previews and apply recorded clan tags when replay identity restoration is enabled.
- **Round playback**: Fix round-start spawn timing, overtime continuation round selection, and missing teammate colors on replacement replay bots.
- **Replay control and cosmetics**: Tighten replay bot ownership checks and input cleanup, strengthen cosmetic plan validation, and remove obsolete identity logic and duplicate runtime processing.
- **Playback runtime**: Move BotController and BotHider to Metamod's shared KHook engine. Update native compatibility data and projectile alignment profiles; unsupported projectile profiles disable alignment and report a diagnostic.
- **Maintenance and releases**: Move maintained components into independent repositories, with the GUI and product integration remaining in the main repository. Pin matching component revisions, automate upgrade PRs, validate complete playback packages in CI, and record exact component sources in build artifacts.

**Updating**: Update the GUI first, then install the complete v1.5.0 playback bundle. Do not mix DLLs from different builds. Playback now requires **Metamod 2.0 build 1469+ (plugin API 18)** and a **KHook-enabled CounterStrikeSharp build**. These are external prerequisites and are not included in the playback bundle. See the [playback server requirements](https://github.com/unicbm/demotracer/blob/main/server/README.md#shared-hook-runtime) for the matched host baseline.

Previously supported replay archives remain compatible. Reconvert the original Demo to capture newly exported evidence such as recorded clan tags.

---

GUI 与完整播放组件包同步升级至 1.5.0。本次更新重点是桌面流程精简、回放修复，以及播放运行时迁移到共享 KHook 引擎。

- **桌面与加载**：精简转换、批量任务、回放库和设置流程。回放库逐步显示扫描结果，玩家与饰品目录按需加载，减少提前加载与重复处理。
- **解析与展示**：减少 Demo 解析和饰品证据处理中的重复计算与内存占用；支持新版准星分享码预览，并在启用身份还原时应用 Demo 记录的战队标签。
- **回合回放**：修复回合开始的出生时序、加时续打的回合选择，以及替补回放 BOT 的队友颜色缺失问题。
- **回放控制与饰品**：收紧回放 BOT 的控制对象校验与输入清理，加强饰品方案校验，移除过时的身份逻辑和重复运行时处理。
- **播放运行时**：BotController 与 BotHider 改用 Metamod 提供的共享 KHook 引擎。更新原生兼容数据和投掷物对齐配置；配置不匹配时停止投掷物对齐并给出诊断。
- **维护与发布**：自维护组件拆分为独立仓库，GUI 与产品集成继续保留在主仓库。固定匹配的组件提交，通过机器人提交升级 PR，加入完整播放包 CI，并在构建制品中记录准确的组件来源。

**升级提示**：请先更新 GUI，再安装完整的 1.5.0 播放组件包，不要混用不同构建的 DLL。播放环境现需 **Metamod 2.0 build 1469+（插件 API 18）** 和 **启用 KHook 的 CounterStrikeSharp 构建**。这两项属于外部依赖，不包含在播放包内；匹配要求见[播放服务器安装说明](https://github.com/unicbm/demotracer/blob/main/server/README.md#shared-hook-runtime)。

此前支持的回放档案继续兼容。如需新采集的战队标签等信息，请从原始 Demo 重新转换。
