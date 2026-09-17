# CS2 DemoTracer v1.3.1

This desktop hotfix repairs the upgrade path from older versions. Playback remains at v1.3.0.

- Keep update buttons visible while long release notes scroll inside the dialog.
- Make the Playback settings action follow the same GUI-first update sequence as the main update dialog.
- Explain incompatible playback bundles instead of incorrectly asking users to check the CS2 path.
- Shorten updater summaries so older GUI versions can reach their update buttons. Full release notes remain on GitHub.
- Fix converter Rust formatting required by CI.

Update the GUI first, then install the matching Playback v1.3.0 bundle. The playback binaries and replay format are unchanged by this hotfix.

---

这是修复旧版升级通道的 GUI 热修复，播放组件继续使用 v1.3.0。

- 更新说明在弹窗内部滚动，长说明不再遮挡底部更新按钮。
- 设置页的播放组件更新也遵循“先 GUI、后播放组件”的顺序。
- 组件不兼容时给出正确提示，不再错误地要求检查 CS2 路径。
- 缩短自动更新摘要，让旧 GUI 也能点到更新按钮；完整说明继续保留在 GitHub。
- 修复 CI 要求的转换器 Rust 格式。

请先更新 GUI，再安装配套的 Playback v1.3.0。本次热修复不改变播放组件二进制和回放格式。
