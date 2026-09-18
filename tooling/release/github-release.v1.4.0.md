# CS2 DemoTracer v1.4.0

This update substantially improves Demo parsing performance, reducing the wait when importing FACEIT and tournament recordings.

- Faster complete Demo parsing, including large tournament recordings.
- Preserve the existing parsed data and sampling precision; the performance improvements run entirely on the CPU.
- No GPU or additional runtime is required.
- Existing replay archives remain compatible. Playback stays at v1.3.0, and users already on that bundle only need to update the GUI.

Actual parsing time depends on the recording and hardware. The replay format and playback components are unchanged.

---

本次更新大幅提升 Demo 解析性能，缩短 FACEIT 和赛事录像的导入等待。

- 加快完整 Demo 解析，大型赛事录像同样受益。
- 保留原有解析数据和采样精度，全部优化均在 CPU 上完成。
- 无需 GPU 或额外运行环境。
- 继续兼容现有回放档案。播放组件保持 v1.3.0，已安装该组件的用户只需更新 GUI。

实际解析耗时取决于录像和硬件。本次更新不改变回放格式和播放组件。
