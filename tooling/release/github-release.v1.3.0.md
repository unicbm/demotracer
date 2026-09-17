# CS2 DemoTracer v1.3.0

The GUI and complete playback bundle are updated together to v1.3.0. This release improves replay state restoration, control handoff, and presentation synchronization while reducing replay storage and runtime overhead.

- **Replay and handoff**: Restore more movement, crouch, ladder, and timing state from the original demo while reducing forced per-tick corrections. Fix incorrect fall damage in some low-step, jumping, and landing situations.
- **First-person presentation**: Improve crosshair, viewmodel, and handedness continuity across respawns, pawn changes, and handoff. Avoid unnecessary weapon switching in some handoff situations.
- **Avatars and identity**: Improve tournament and team avatar updates in the top HUD and replay identity publication, fixing some delayed overrides and inconsistent displays.
- **Equipment and round transitions**: Restore equipment at its recorded acquisition time without forcing demo magazine or reserve ammunition values. Reduce repeated preparation during round transitions.
- **Replay archives**: Compact state records and use Zstandard compression to reduce the storage cost of additional replay state. Older replay archives remain supported.
- **Parsing accuracy**: Fix missing 5E platform metadata in some professional match demos and improve server clock, tick, and movement field decoding.
- **Runtime efficiency**: Replace internal per-tick lease renewal, expiration sweeps, and repeated provider probes with lifecycle notifications. Reduce repeated object construction and queries during BOT checks and presentation updates.

**Updating**: Update the GUI and complete playback bundle together; do not mix components from different builds. Existing archives remain playable. Reanalyze or reconvert the original demo to benefit from newly captured evidence, restored platform metadata, and the new compression format.

---

GUI 与完整播放组件包同步升级至 1.3.0。本次更新重点改善回放状态还原、接管衔接和展示同步，并优化回放档案与运行时开销。

- **回放与接管**：补全原始 Demo 中的移动、蹲伏、梯子及相关时间状态，减少强制逐 tick 矫正；修复部分低台阶、起跳和落地场景中的错误坠落伤害。
- **第一人称展示**：改善准星、持枪视角和左右手状态在复活、Pawn 变化及接管时的衔接，避免部分接管场景中不必要的切枪。
- **头像与身份**：改善顶栏赛事头像、队伍头像及回放身份的更新，修复部分头像覆盖不及时或显示不同步的问题。
- **装备与回合切换**：按 Demo 记录的装备获取时机恢复装备，不再强行同步弹匣和备弹；减少回合切换中的重复准备工作。
- **回放档案**：精简状态记录，采用 Zstandard 压缩，减少新增状态带来的存储负担，并继续兼容旧版回放档案。
- **解析准确性**：修复部分职业比赛 Demo 的 5E 平台信息缺失，改进服务器时钟、tick 和移动字段的读取。
- **运行时效率**：移除内部逐 tick 续租、超时扫描和重复提供者探测，改用生命周期通知；减少 BOT 判断和展示更新中的重复对象构造与查询。

**更新须知**：请将 GUI 与完整播放组件包配套升级，避免混装旧组件。旧档案仍可播放；新增解析证据、平台信息补全和新压缩格式需要重新分析或转换原始 Demo 才能生效。
