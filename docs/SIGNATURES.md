# DemoTracer 签名维护

引擎签名、偏移和调用约定由下列文件维护。

## 定义归属

| 范围 | 文件 | 内容 |
| --- | --- | --- |
| dtr-controller | [gamedata.json](../server/runtime/dtr-controller/configs/addons/dtr-controller/gamedata.json) | server 签名、私有偏移、移动服务虚表槽 |
| dtr-hider | [gamedata.json](../server/runtime/dtr-hider/configs/addons/dtr-hider/gamedata.json) | server / engine2 签名、客户端列表和身份字段布局 |
| 独立 BotRandomizer | [BotRandomizerItems.cs](../server/runtime/BotRandomizer/BotRandomizerItems.cs) | attribute writer 与 item-view constructor 的内联签名 |
| 投掷物物理钩子 | [demotracer-native.json](../server/plugins/DemoTracer/config/demotracer-native.json) | 入口、模块与函数体哈希、虚表和调用约定 |
| 动态 Schema | [version_targets.cpp](../server/runtime/dtr-controller/src/common/version_targets.cpp) | 必需字段解析；不把运行时结果另抄成固定偏移 |

版本与 API 要求以 [playback contract](../shared/contracts/playback-contract.v1.json) 为准。
CSS、Metamod 和可选依赖的维护边界见 [server requirements](../server/README.md)。

## 游戏更新后的维护

目标版本和模块指纹以 [native profile](../server/plugins/DemoTracer/config/demotracer-native.json) 为准。

1. 检查 gamedata 和内联签名在目标模块中的匹配位置，以及对应的参数、字段和虚表布局。
2. 更新投掷物 profile 时，核对模块指纹、完整函数体和全部 6 个虚表引用。
3. 运行 [Release 构建与测试](DEVELOPMENT.md#build-and-test) 和
   [原生钩子测试](../server/README.md#shared-hook-runtime)。
4. 在配套服务器上检查回放、Bot 接管、投掷物生成和停止后的状态释放。
