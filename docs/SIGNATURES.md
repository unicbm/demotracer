# DemoTracer 签名维护

本页记录引擎更新后的维护基线、已知兼容性要求和验收规则。
**运行定义以 gamedata 和源码为唯一真源**；本页不复制完整签名字节、RVA 或偏移清单。
历史基线通过 Git 保留。新引擎版本必须重新验证，不能继承旧版本的通过状态。

## 定义归属

| 范围 | 维护真源 | 需要检查的内容 |
| --- | --- | --- |
| BotController | [gamedata.json](../server/runtime/BotController/configs/addons/BotController/gamedata.json) | server 签名、私有偏移、移动服务虚表槽，以及可选 engine2 / panorama HUD 桥接 |
| BotHider | [gamedata.json](../server/runtime/BotHider/configs/addons/BotHider/gamedata.json) | server / engine2 签名、客户端列表和身份字段布局 |
| 独立 BotRandomizer | [BotRandomizer.cs](../server/runtime/BotRandomizer/BotRandomizer.cs) | attribute writer 与 item-view constructor 的内联签名 |
| 投掷物物理钩子 | [ProjectilePhysicsHook.cs](../server/plugins/DemoTracer/src/DemoTracer/Projectiles/ProjectilePhysicsHook.cs) | 入口、模块与函数体哈希、虚表和调用约定 |
| 动态 Schema | [version_targets.cpp](../server/runtime/BotController/src/common/version_targets.cpp) | 必需字段解析；不把运行时结果另抄成固定偏移 |

版本与 API 要求以 [playback contract](../shared/contracts/playback-contract.v1.json) 为准。
CSS、Metamod 和可选依赖的维护边界见 [server requirements](../server/README.md)。

## 当前维护基线

| 项目 | 基线 |
| --- | --- |
| 游戏 | Windows x64 CS2 **1.41.8.2** / revision **11026673** |
| 核验日期 | **2026-09-23** |
| 运行源码提交 | `deb00f340f5e443ac70b9ae80bd495f6817e8cc3` |
| 扫描结果 | BotController 23/23、BotHider 4/4、BotRandomizer 2/2、投掷物钩子 1/1 唯一命中 |
| 适用边界 | 仅对应本次核验的 Windows 构建；Linux 未验证 |

扫描通过不等于行为或全部私有布局验证通过。版本号相同但模块指纹变化时也应重新核验。
投掷物钩子的受支持模块指纹由上述源码直接维护。

本基线修复及以后必须保留的检查：

| 范围 | 修复内容 | 验收要求 |
| --- | --- | --- |
| BotController | 更新武器、购买、视角、梯子和可选 HUD 签名 | 最佳武器依次查询槽 0/1/2；手枪只查询槽 1。唯一命中的切刀函数仍是错误目标 |
| BotController 托管提供器 | 兼容原生模块加载时序 | 冷启动和热加载能完成能力注册，不能重复注册 |
| BotHider | 更新配额 / 实体打包签名及客户端列表偏移 | 检查真实 Bot 接管与 HUD；API ready 不足以验收 |
| 独立 BotRandomizer | 更新 attribute writer 签名 | 检查属性写入及所选外观；不能只确认插件加载 |
| 投掷物物理钩子 | 更新入口、指纹和虚表位置 | 五参数 ABI、完整函数体校验和全部 6 个虚表指针一致；真实投掷物首帧路径正常 |

CSS 必须配套适合该引擎版本的实体监听布局修复，native 与 gamedata 需要一致。
这是外部依赖要求，不由本仓的签名更新代替；不能仅凭 CSS 版本号或签名命中认定满足。

## 如何记录状态

每次更新分别记录以下三类结果，不能压成一个含义不明的“有效”：

| 维度 | 状态 | 判断依据 |
| --- | --- | --- |
| 扫描 | 唯一 / 零命中 / 多命中 / 未扫描 / 不适用 | 对指定模块记录匹配数量 |
| 语义与布局 | 已核对 / 错误目标 / 待核对 / 不适用 | 目标函数、参数、字段和虚表与消费者一致 |
| 实机 | 指定场景通过 / 失败 / 未覆盖 | 写明所覆盖的功能场景，不由扫描或编译结果推导 |

零命中、多命中、错误目标以及 ABI / 布局 / 哈希不符都属于兼容性失败。
“待核对”表示未知；可选功能停用与签名失效应分别处理。
本页只保留影响维护决策的异常和结论，完整扫描库存应从运行真源提取。

## 更新流程

1. 记录游戏版本、平台、模块指纹和源码提交。相关模块变化后，对新构建标记为待验证。
2. 从真源核对消费者及模式的增删，覆盖内联签名与非 server 模块；检查扫描结果。
3. 单独核对关键语义、私有字段、虚表、Schema 必需字段和指纹守卫。
   不通过扩大通配范围或增加强制切枪策略掩盖错误目标。
4. 核对 CSS / Metamod 等依赖是否需要配套更新；保持源码归属与契约一致。
5. 按 [开发文档](DEVELOPMENT.md#build-and-test) 和 [原生钩子验证](../server/README.md#shared-hook-runtime)
   运行对应 Release 构建及测试，再验证实际受影响场景。
   重点包含武器恢复 / handoff、Bot 接管与 HUD、投掷物生成，以及停止或结束后的状态释放。
6. 在同次修复中更新本页基线和异常结论，明确未覆盖范围；安装时保留用户配置与插件禁用状态。

本页目前人工维护，没有仓内自动扫描或自动刷新状态的命令。
若增加自动化，扫描列可以从源码生成；语义和实机列仍须有独立依据。
文档及提交不得包含本地路径、服务器配置、用户数据、原始二进制、研究记录或临时验证产物。
