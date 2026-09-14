# CS2 DemoTracer v1.2.3

The GUI and complete playback bundle are both updated to v1.2.3.

- **First-person view**: Fixed replay bot view synchronization by letting the engine update it normally, reducing extra processing overhead.
- **Cosmetic detection**: Capture cosmetics from warmup and buy-and-drop situations, improve ownership detection, and retain distinct cosmetic items of the same weapon type.
- **Projectile reconstruction**: Improved initial position and velocity alignment, corrected Molotov/incendiary grenade identification, and fixed some projectile matching issues.
- **Desktop performance**: Optimized replay library caching, conversion queues, and file access to reduce repeated reads and data copies.

**Updating**: Update the GUI and complete playback bundle together. Existing archives remain playable, and the view fix does not require reconversion. Reanalyze or reconvert the original demo to benefit from cosmetic detection and projectile parsing improvements.

---

GUI 与完整播放组件包同步更新。

- **第一人称视角**：修复回放 BOT 的视角同步，交由引擎正常更新，减少额外处理开销。
- **饰品识别**：补全热身、购买后立即丢弃等场景的饰品记录，改善归属识别，保留同类武器的不同饰品。
- **投掷物还原**：改善初始位置与速度对齐，修正燃烧瓶／燃烧弹识别及部分投掷物匹配问题。
- **桌面性能**：优化回放库缓存、转换任务队列和文件访问，减少重复读取与数据复制。

**更新须知**：GUI 与完整播放组件包请配套升级。旧档案仍可使用，视角修复无需重转；饰品识别与投掷物解析改进需要重新分析或转换原始 Demo。
