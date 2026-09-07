# ArmorControl Web Protocol v1

状态：协议 v1 实施基线。传输为 HTTP/1.1 与 RFC 6455 WebSocket；浏览器不直接使用 kRPC。

## 连接

- 网页与健康检查：`http://<ksp-host>:8765/`、`GET /health`。
- 实时通道：`ws://<ksp-host>:8765/ws/realtime`。
- 载具图像通道：`ws://<ksp-host>:8765/ws/vessel`，只发送二进制 JPEG 帧；仅在载具页可见时连接。
- 当前默认关闭共享令牌，设备可直接打开 `http://<ksp-host>:8765/`。如以后在 `settings.cfg` 配置 `accessToken`，首次访问可使用 `http://<ksp-host>:8765/#token=<accessToken>`；网页会将令牌保存在该浏览器本地，并附加到受保护的 API 与 WebSocket 请求。
- 首版消息为 UTF-8 JSON。协议稳定后，快速遥测可增加二进制编码，但 JSON 字段语义保持不变。
- 服务端允许多个客户端。每个客户端有独立发送循环和最新遥测槽，慢客户端不会阻塞其他客户端。

所有服务端消息包含 `type` 与 `protocol`。未知的可选字段必须被忽略；不支持的主版本必须拒绝并显示升级提示。

## 服务端消息

### `hello`

连接建立后的第一条可靠消息：

```json
{
  "type": "hello",
  "protocol": 1,
  "clientId": "8e5f...",
  "capabilities": ["telemetry.fast", "telemetry.regular", "event.context", "event.vessel", "command.fifo", "multiClient"]
}
```

`clientId` 只用于命令追踪和诊断，不表示角色或控制权。

### `telemetry.fast`

服务端以 KSP 物理快照为来源。该消息采用 latest-only 语义：未发送的旧帧可被新帧覆盖，不补传过时帧。

| 字段 | 单位/类型 | 语义 |
|---|---|---|
| `sequence` | int64 | 服务启动期内单调递增的快照序号 |
| `ut` | s | KSP Universal Time |
| `met` | s | 当前载具 Mission Elapsed Time；无载具时为 0 |
| `scene` | string | KSP 场景 |
| `vesselId` | string/null | 当前载具 GUID |
| `vesselName` | string/null | 当前载具名称 |
| `altitude` | m | 海平面高度 |
| `radarAltitude` | m | 雷达/地形高度 |
| `surfaceSpeed` | m/s | 地表参考系速度 |
| `verticalSpeed` | m/s | 垂直速度，向上为正 |
| `heading` | deg | 航向 `[0, 360)` |
| `pitch` | deg | 俯仰 `[-180, 180]` |
| `roll` | deg | 滚转 `[-180, 180]` |
| `throttle` | ratio | 油门 `[0, 1]` |
| `geeForce` | g | 当前 G 力 |
| `dynamicPressureKpa` | kPa | 飞行动压 |

非有限浮点值序列化为 `null`，不得输出非标准 JSON 的 `NaN` 或 `Infinity`。

### `telemetry.flightPanel`

5 Hz、latest-only 的 MechJeb 同源 Flight Pannel 快照。包含当前本机配置中的全部只读项：轨道、地表、性能、载具、飞行数据和目标距离；数值字段保留底层单位，倒计时、Biome、坐标与 MechJeb 组合摘要保留公开 API 返回文本。三项操作（自动分级、太阳能板、天线）归入控制/状态协议，不伪装成只读遥测。

### `telemetry.automation`

5 Hz、latest-only 的节点和自动驾驶权威状态。`maneuverNodes` 是当前 KSP 机动节点的完整有序数组，每项包含 `ut` 与 `deltaV`；`maneuverNodeCount`、`nextNodeUt`、`nextNodeDeltaV` 保留为快速摘要。消息还包含：

- Node Executor、Smart A.S.S.、自动发射、自动交汇、自动着陆和自动对接的启用状态；Smart A.S.S. 当前 target 和自动停用配置。自动着陆包含 MJ 当前状态、步骤、下降策略、着陆架/降落伞级数限制，以及预测结果、坐标、ASL 高度、目标偏差、最大阻力、所需 Δv、着陆时间和气动刹车后轨道。自动对接包含速度限制、安全距离覆盖、起始距离覆盖、Force Roll 角度与边界框设置；对接轴数据包含有效性、目标对接口局部坐标系中的有符号 X/Y 偏差、轴向距离与横向合量，它直接由当前目标对接口计算，不依赖自动对接是否已经启动。
- Node Executor 的 `autoWarp` 权威状态；节点规划页与自动控制页显示并修改同一个值。
- 当前 KSP 目标名称、类型、距离、相对速度和目标轨道摘要，以及 MechJeb 地面位置目标的天体、纬度和经度。
- `targetCatalog`：天体层级、其他载具及已加载载具对接口的目录；浏览器按 `parentId` 渲染目标树。
- MechJeb 2.14.3 三种上升制导器的通用及路径专属参数，以及自动交汇的距离、相位轨道数、接近速度和状态。
- 自动着陆预测是否可用、对接步骤、轴向距离和横向合量。
- SAS/RCS/起落架/刹车/灯光、动作组位图、太阳能板、天线和自动分级的权威状态。
- Porkchop revision、计算状态和进度；矩阵本体仍通过 bulk 通道/API 传输。
- `envelope*`：直接来自 MechJeb 2.14.3 Thrust Controller 与全局 Staging Controller 的飞行包线状态，包括终端速度、动压、加速度、最大/最小油门、过热、熄火、点火稳定、RCS 沉降、进气口、平滑油门、差动油门和自动分级。
- `trajectory`：通过运行时桥接读取本机 Trajectories 2.4.5.4 的显示/计算设置、气动模型、撞击时间/位置/速度、最大减速度、目标点与四段下降剖面。没有安装或未就绪时 `available=false`，不影响 ArmorControl 其他功能。

### `recorder.sample`

直接来自 `MechJebModuleFlightRecorder.history` 的新增记录点，字段与 MechJeb 的 18 个 `recordType` 一一对应。每客户端只保留尚未发送的最新增量；初次打开先读取一次 `/api/v1/recorder` 历史，之后只消费 WebSocket 增量。

### `command.ack`

```json
{
  "type": "command.ack",
  "protocol": 1,
  "commandId": "client-generated-id",
  "serverSequence": 18,
  "success": true,
  "code": "ok",
  "message": "..."
}
```

回执在命令经过全局 FIFO 并由 KSP 主线程处理后产生。按钮只能在成功回执或后续权威状态帧到达后确认状态。

### `event.context`

场景或活动载具发生变化时可靠发送，包含 `sequence`、`scene`、`vesselId` 与 `vesselName`。客户端收到后应清空属于旧载具的 Recorder、零件选择、节点预览和未确认危险操作。

### `event.vessel`

同一活动载具的结构摘要发生变化时可靠发送。`changes` 数组可包含 `stage`、`target`、`parts` 与 `crew`；对接、解体等复合事件会表现为其中一个或多个变化。客户端必须把该事件视为“重新拉取对应模块数据”的失效通知，而不是依赖它推断完整物理过程。

### `pong` 与 `error`

`pong.sequence` 原样返回客户端心跳序号。`error` 包含稳定的机器码 `code` 和可显示文本 `message`。

## 客户端消息

### `ping`

```json
{"type":"ping","sequence":77}
```

浏览器默认每秒发送一次。它用于测量应用层链路和发现半开连接，不代表用户角色或控制租约。

### `command`

```json
{
  "type": "command",
  "commandId": "8e5f-1720000000-4",
  "name": "noop",
  "sequence": 4
}
```

- 所有客户端的离散命令进入同一个全局 FIFO，`serverSequence` 给出实际入队顺序。
- `commandId` 必须唯一。服务端缓存最近 2048 个完成结果；相同 ID 在排队期间只执行一次并向所有等待者返回同一结果，完成后的重试直接重放原回执。
- 原生 KSP 控制不依赖 MechJeb；`mechjeb.*` 命令在当前载具没有 MechJeb 核心时返回 `mechjeb_unavailable`。

### 控制命令

- `vessel.system.toggle`：参数 `system` 为 `sas`、`rcs`、`gear`、`brakes` 或 `lights`。
- `vessel.actionGroup.toggle`：参数 `group` 为 1–10。
- `vessel.stage`、`vessel.solar.toggle`、`vessel.antenna.toggle`。
- `game.quicksave.load`：通过 KSP 1.12.5 原生 QuickSaveLoad 管线直接加载 `quicksave.sfs`。活动存档含 quicksave 时优先使用；主菜单、启动加载阶段或当前目录缺少 quicksave 时选择最近写入的有效 quicksave。网页要求单击后将确认滑块拖到最右端，文件不存在或 KSP 加载器未就绪时返回失败。
- `vessel.part.engine.setActive`、`vessel.part.engine.setThrustLimit`：用 `partId` + `moduleIndex` 唯一定位一个 `ModuleEngines`；分别显式设置点火状态和 0–100% 推力限制。RCS 不进入引擎数组。
- `vessel.part.gimbal.set`、`vessel.part.rcs.set`、`vessel.part.light.set`、`vessel.part.gear.set`、`vessel.part.cargo.set`：显式设置单个模块状态。起落架兼容原版 WheelDeployment 和当前安装的 KSPWheel，货舱只识别真正的货舱部署动画。
- `vessel.partGroup.set`：参数 `kind` 为 `engine`、`rcs`、`light`、`gear` 或 `cargo`，参数 `enabled` 为目标状态；整个载具的批量变更作为一条 FIFO 主线程命令执行。
- `vessel.crew.eva`：用 `crewName` + `partId` 定位当前载具成员，并通过 KSP `FlightEVA` 从所在舱段执行 EVA。网页仅此操作及分级/quicksave 使用单击后滑动到底的确认轨；自动发射页底部的手动分级与控制页调用同一个 `vessel.stage` 命令。
- `vessel.view.mode`：参数 `mode` 为 `state`、`stage`、`heat` 或 `resource`，触发一次低频网格重绘。
- `vessel.view.rotate`、`vessel.view.fit`：切换正交观察平面或重新适配画布，并触发一次网格重绘。
- `vessel.control.set`：`pitch`、`yaw`、`roll` 限幅到 `[-1,1]`，并像 KSP 键盘轴一样叠加到当前 FlyByWire 状态，不会主动停止 MechJeb；`throttle` 限幅到 `[0,1]`，`holdMilliseconds` 限制到 100–1000 ms。网页每 100 ms 续期，默认有效期 350 ms。当 MechJeb 自动发射、节点执行器或自动着陆处于启用状态时，服务端忽略网页油门并保留 MJ 的推力输出；网页也根据 `telemetry.automation` 锁定油门槽。
- `vessel.control.release`：立即解除 ArmorControl 的 FlyByWire 回调并清空其连续输入；若上述 MJ 燃烧自动驾驶正在运行，不覆盖 MJ 的油门或姿态状态。
- `mechjeb.recorder.clear`（兼容别名 `mechjeb.recorder.mark`）：调用 MechJeb Flight Recorder 的 `Mark()`，清除 DLL 与网页的旧历史，并把当前时刻作为新的 T+0。
- `mechjeb.smartass.set`：`target` 使用 MechJeb Smart A.S.S. 枚举名；可带 `pitchOffset`、`yawOffset`、`rollOffset` 和 `autoDisable`。只有 Surface/Surface Velocity 模式接受 Pitch/Yaw 偏移，其余模式只写入 Roll 偏移。`mechjeb.smartass.off` 解除接管。
- `mechjeb.ascent.start`：直接配置并启用本地 MechJeb 2.14.3 的 Ascent Autopilot；除目标高度、倾角、LAN、油门/转向/分级/部署、迎角/动压、滚转与圆化选项外，还按 `ascentPath`（`GRAVITYTURN`、`PVG` 或 `CLASSIC`）写入该制导器的专属路径参数。`launchMode` 支持 `IMMEDIATE`、`COUNTDOWN`、`TARGET_PLANE`、`TARGET_LAN` 与 `RENDEZVOUS`；后三项均要求目标与载具绕同一天体运行。`TARGET_PLANE` 调用 MJ 的最早目标轨道面窗口并解析顺/逆行倾角，`TARGET_LAN` 精确调用目标 LAN 窗口且仅用于 PVG，`RENDEZVOUS` 调用交会相位窗口且仅用于非 PVG；旧客户端的 `TARGET_ORBIT` 作为 `TARGET_PLANE` 兼容别名继续接受。PVG 下 `orbitAltitudeKm` 明确表示 PE，`pvgDesiredApoapsisKm` 表示 AP，且服务端拒绝 AP 低于 PE；`pvgDesiredAttachAltitudeKm` 对应燃尽/轨道插入高度。`mechjeb.ascent.stop` 取消倒计时、清除全部 MJ 发射模式标志并解除上升制导。
- `vessel.view.selectPart`：按 `partId` 设置 VesselView 高亮零件；选择变化只触发一帧重绘。
- `vessel.structure` 中的 `aerodynamicCenter`：来自本地 FAR 0.16.1.2 的当前总气动力与以 CoM 为参考的气动力矩。包含是否可用、是否在当前载具投影内、归一化投影坐标以及相对 CoM 的前/右/上偏移。气动力过低时明确报告不可用。
- `mechjeb.autowarp.set`：参数 `enabled`，直接修改 MechJeb Node Executor 的 `autowarp`。
- `target.set`：参数来自 `targetCatalog` 的 `kind`、`bodyName`、`vesselId`、`partId`，通过 KSP Target Controller 设置天体、载具或对接口目标；`target.clear` 清除目标。
- `mechjeb.rendezvous.start`：参数 `desiredDistance`、`maxPhasingOrbits`、`maxClosingSpeed`，要求有效轨道目标；`mechjeb.rendezvous.stop` 解除自动交汇。
- `mechjeb.landing.configure`：即时写入 MJ 2.14.3 的接地速度、着陆架/降落伞部署及最低级数、RCS 微调，以及着陆预测、气动刹车节点、世界/相机轨迹显示设置。`mechjeb.landing.setTarget` 的参数为 `latitude`、`longitude`，通过 MechJeb Target Controller 的位置目标 API 设置。`mechjeb.landing.target`、`mechjeb.landing.untargeted` 接受同一组设置并启动对应自动驾驶；`mechjeb.landing.stop` 解除接管。
- `mechjeb.docking.configure`：即时写入 MJ 2.14.3 自动对接设置但不启动接管。参数为 `speedLimit`、`overrideSafeDistance`、`safeDistance`、`overrideStartDistance`、`startDistance`、`forceRoll`、`roll`、`drawBoundingBox`；其中 MJ 内部历史字段名 `overrideTargetSize/overridenTargetSize` 实际对应原生面板的 Override Start Distance。`mechjeb.docking.start` 接受相同参数并启动自动对接；旧客户端的 `overrideTargetSize/targetSize` 参数继续作为起始距离兼容别名；`mechjeb.docking.stop` 解除接管。
- `mechjeb.maneuver.create`：参数 `operation` 与该 Operation 的字段；`timeReference` 直接使用 MechJeb `TimeReference` 名称，`X_FROM_NOW` 可带 `leadTime`。`replaceLast` 在成功求解后替换最后节点，`execute` 将“创建并执行”作为一个 FIFO 原子命令。新建节点以现有最后节点的 `nextPatch` 为规划轨道。
- `mechjeb.porkchop.solve` 异步生成真实转移代价矩阵；默认时间范围与 MechJeb 2.14.3 `OperationAdvancedTransfer.ComputeTimes` 一致：出发窗口为 1.5 个会合周期，最短飞行时间 3600 秒，最长飞行时间为名义转移时间的 2 倍。服务端从 `MechJebModuleTargetController.Target` 获取目标天体，并把因 FIFO 延迟而落后于当前 UT 的最早出发时刻向前钳制。`mechjeb.porkchop.create` 接受矩阵索引以及同样的 `replaceLast` / `execute` 原子语义。未完成真实解算时网页不生成估算值。
- `mechjeb.node.executeOne`、`mechjeb.node.executeAll`、`mechjeb.node.abort`、`mechjeb.node.removeAll` 与 `mechjeb.autostage.toggle` 由对应 MechJeb 模块执行。
- `mechjeb.envelope.set`：一次原子写入飞行包线页的 MechJeb Thrust Controller 设置；数值参数包括 `maxDynamicPressure`、`maxAcceleration`、`maxThrottle`、`minThrottle`、`flameoutSafetyPct` 与 `throttleSmoothingTime`，其余布尔参数与 `telemetry.automation` 的 `envelope*` 字段同名；`autoStage` 写入 MechJeb 全局 Staging Controller。
- `trajectories.settings.set`：写入 Trajectories 的 `DisplayTrajectories`、`DisplayTrajectoriesInFlight`、`AlwaysUpdate`、`DisplayCompleteTrajectory`、`BodyFixedMode`、`AutoUpdateAeroDynamicModel`、`UseCache`、`IntegrationStepSize`、`MaxPatchCount` 与 `MaxFramesPerPatch`，并调用插件自身保存逻辑。
- `trajectories.profile.set`：写入 Entry、High、Low、Final 四段下降剖面。每段分别传 `mode`（`VELOCITY` 或 `HORIZON`）、`retrograde` 和角度制数值 `angle`；桥接层按 Trajectories API 约定转换为弧度。
- `trajectories.target.set`：用 `latitude`、`longitude`、`altitude` 设置手动撞击目标；`trajectories.target.clear` 清除目标；`trajectories.update` 请求插件立即重算。

连续控制仍通过全局 FIFO 接收，但每条输入只维持短时有效状态；页面隐藏、连接断开、活动载具变化或续期超时都会清空 ArmorControl 输入并释放回调，避免网络中断后保留油门或姿态输入。

## 背压与可靠性

- `hello`、命令回执、错误、结构事件必须可靠排队。
- 快速遥测每客户端只保留一份尚未发送的最新帧。
- Recorder 与 Porkchop 使用独立 bulk 通道，避免大消息阻塞实时通道。
- 页面分别记录 WebSocket 消息、`telemetry.fast`、`telemetry.regular` 与 `pong` 的到达时间。快速遥测超过 1 秒未更新时，即使心跳仍正常也必须标记“遥测延迟”；连接关闭后进入演示/重连状态，不继续显示为实时。
- 客户端收到不支持的 `hello.protocol` 后停止自动重连并显示版本不兼容；用户手动重连可重新执行协商。

## 服务配置与采样

`ArmorControl/settings.cfg` 控制服务启动参数：`enabled`、`autoStartServer`、`bindAddress`、`port`、`fastTelemetryHz`、`regularTelemetryHz` 与 `accessToken`。默认随 DLL 加载自动启动；用户仍可通过 KSP 原生工具栏的 ArmorControl 面板设置端口并启动/停止服务。默认监听 `0.0.0.0:8765`，快速/常规遥测分别为 30 Hz 与 5 Hz。快速频率有效范围为 1–60 Hz，常规频率为 1–20 Hz；采样仍只在 KSP 主线程执行。将 `accessToken` 留空会关闭鉴权，只建议用于受控的本机调试。

`GET /health` 的 `metrics` 返回累计的快速/常规采样数量、采样平均与峰值毫秒，以及快速 JSON 序列化平均与峰值毫秒。它用于 30/60 Hz 帧预算验收，不参与飞行控制。

## 按需结构与历史 API

- `GET /api/v1/crew`：只返回当前活动载具成员、职业、等级、所在零件/舱段、载具容量，以及逐人的 EVA 可用状态/原因。
- `GET /api/v1/vessel`：返回当前载具完整零件树、温度、资源和模块名称，并为每个零件返回带稳定模块索引的 `engines` 与 `toggles`。引擎包含点火、重启/关机能力、推力限制和对应 Gimbal；`toggles.kind` 独立区分 `rcs`、`light`、`gear`、`cargo`。协议不设零件数量上限。
- `GET /api/v1/vessel-image.jpg`：返回最近一次载具投影，主要用于诊断；正式网页使用 `/ws/vessel`，不轮询此端点。
- `GET /api/v1/recorder`：返回当前 3000 点记录窗口，只在首次打开或上下文重置后请求。
- `GET /api/v1/recorder.csv`：以 MechJeb 的 18 列顺序导出同一记录窗口。

这些端点不是高频轮询通道。完整 `vessel.structure` 快照随实时 WebSocket 的 regular 频率更新；网格只在首次观看、载具/模式/观察方向变化时重绘，并以 5 秒一次作兜底。Recorder 通过 `recorder.sample` 增量续接。
