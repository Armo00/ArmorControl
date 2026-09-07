# ArmorControl 前期可获取性调查

调查日期：2026-09-03  
调查方式：仅静态读取配置与程序集元数据；未启动、关闭或控制 KSP，也未发送任何控制命令。

## 1. 本机组件

| 组件 | 本机版本 | 证据 |
|---|---:|---|
| KSP | 1.12.5 | 当前游戏目录 |
| kRPC | 0.6.0.0 | `GameData/kRPC/KRPC.SpaceCenter.dll`、`kRPC.version` |
| MechJeb 2 | 文件版本 2.14.3.0（程序集版本 2.5.1.0） | `GameData/MechJeb2/Plugins/MechJeb2.dll` |
| VesselViewer Continued | 0.8.9.0 | `GameData/VesselView/VesselView.version`、相关程序集 |
| RemoteTech | 1.9.12.0 | `GameData/RemoteTech/RemoteTech.version` |

当前 kRPC 安装中没有 `KRPC.MechJeb` 服务程序集，kRPC 各程序集也没有 MechJeb 程序集引用。因此不能假定存在现成的 kRPC MechJeb service。

## 2. 推荐的数据通路分层

| 等级 | 通路 | 用途 | 稳定性 | 主要风险 |
|---|---|---|---|---|
| A | ArmorControl DLL 直接调用 KSP 公开 API | 通用飞行、轨道、零件、资源和状态 | 高 | 必须在 Unity/KSP 主线程采样部分对象；场景切换时对象会失效 |
| A | kRPC 0.6.0 SpaceCenter service | 已有的标准化飞行与零件字段，可作为兼容/验证通路 | 高 | 浏览器不能直接方便地消费 kRPC 协议，仍需网页网关；连接和流数量需限流 |
| B | ArmorControl DLL 直接引用 MechJeb 2.14.3 公开类型 | 精确复现 Flight Pannel 的 MechJeb 特有值 | 中高 | 固定兼容本机 MechJeb 2.14.3；构建验证会拒绝其他版本 |
| B | 根据 KSP/kRPC 原始值重算 | TWR、目标距离、部分格式化值 | 中高 | 可能与 MechJeb 的滤波、仿真或格式化有小差异 |
| C | 调用 VesselView 的公开渲染入口 | 原样生成 Vessel View 图像 | 中 | API 公开但未形成稳定的数据契约；GPU 回读和图片编码有性能成本 |
| D | 只读反射 | 获取无公开入口的私有缓存/实例 | 低 | 字段名和实现易随版本变化；只能作为有版本守卫的最后手段 |

初步架构结论：不应把 ArmorControl 设计成“网页直接连接 kRPC”。更合适的是由自有 DLL 在游戏内聚合 KSP、MechJeb、VesselView（可选）数据，再通过 HTTP/WebSocket 向浏览器提供稳定 JSON/图像协议。kRPC 是可用的数据来源之一，而非硬依赖。

## 3. MechJeb Flight Pannel

### 3.1 实际窗口定义

本机窗口定义位于：

`GameData/MechJeb2/Plugins/PluginData/MechJeb2/mechjeb_settings_global.cfg`

窗口标题确实是 `Flight Pannel`，其中共有 51 个配置项：45 个遥测项、3 个操作项和 3 个水平分隔符。`refreshRate = 10`，程序集实现把刷新间隔设为 `1 / refreshRate` 秒，因此 UI 缓存目标刷新率为 10 Hz。网页只需要完整包含这些信息，不需要复刻该窗口的原始排版、文本拼接方式或视觉样式。

窗口中的值来自两类公开类型：

- `MuMech.VesselState`：MechJeb 的持续更新飞行状态缓存。
- `MuMech.MechJebModuleInfoItems`：组合、格式化和特有计算。

自有 DLL 可通过公开扩展方法 `MuMech.VesselExtensions.GetMasterMechJeb(Vessel)` 取得当前载具的 `MechJebCore`，再通过公开 `GetComputerModule<T>()` 获取 `MechJebModuleInfoItems`、`MechJebModuleStageStats` 等。调查涉及的特有方法和级段数组均为 public，不需要反射。

### 3.2 字段清单

约定：

- 更新频率“快”建议网页采样 5–10 Hz；“慢”建议 1–2 Hz；结构字段在事件发生后刷新。
- 除明确标为“操作”的三项外，以下路径均为只读。
- “飞行”表示需要 Flight 场景和已加载的 vessel；部分轨道字段在非飞行场景可从存档对象取得，但不作为首版保证。

| Flight Pannel 字段/ID | 首选来源和获取方法 | 频率 | 场景 | 稳定性/风险 |
|---|---|---:|---|---|
| 当前轨道摘要 `InfoItems.CurrentOrbitSummary` | MechJeb public 方法精确复现；或用 KSP/kRPC `Orbit` 的 Ap/Pe/patch 组合 | 2 Hz | 飞行 | 高；重算时只会有文本格式差异 |
| 轨道倾角 `VesselState.orbitInclination` | kRPC `Orbit.Inclination` 或 KSP `Orbit.inclination` | 2 Hz | 飞行 | 高 |
| 轨道周期 `VesselState.orbitPeriod` | kRPC `Orbit.Period` | 2 Hz | 飞行 | 高；逃逸轨道需处理无穷/无效值 |
| 轨道速度 `VesselState.speedOrbital` | kRPC `Flight.Speed`（轨道参考系）或 KSP vessel velocity | 快 | 飞行 | 高；参考系必须固定 |
| 到远拱点时间 `orbitTimeToAp` | kRPC `Orbit.TimeToApoapsis` | 2 Hz | 飞行 | 高；无远拱点时返回不可用 |
| 到近拱点时间 `orbitTimeToPe` | kRPC `Orbit.TimeToPeriapsis` | 2 Hz | 飞行 | 高 |
| 到 SOI 转换时间 `InfoItems.TimeToSOITransition` | kRPC `Orbit.TimeToSOIChange`；或 MechJeb public 方法 | 2 Hz | 飞行 | 高；无下一 patch 时显示 N/A |
| 离心率 `orbitEccentricity` | kRPC `Orbit.Eccentricity` | 2 Hz | 飞行 | 高 |
| 载具底部高度 `VesselState.altitudeBottom` | MechJeb public 属性；无 MechJeb 时由 KSP 零件包围盒/地形高度估算 | 快 | 飞行 | 中高；kRPC `surface_altitude` 不保证与“载具最低点”完全同义 |
| 坐标 `InfoItems.GetCoordinateString` | kRPC `Flight.Latitude/Longitude`；网页自行格式化 | 2 Hz | 飞行 | 高 |
| 航向 `VesselState.vesselHeading` | kRPC `Flight.Heading` | 快 | 飞行 | 高 |
| 表面重力 `InfoItems.SurfaceGravity` | KSP body `GeeASL` 或 kRPC 天体表面重力 | 慢 | 飞行 | 高 |
| 地表速度 `VesselState.speedSurface` | kRPC `Flight.Speed`（surface reference frame） | 快 | 飞行 | 高 |
| 垂直速度 `VesselState.speedVertical` | kRPC `Flight.VerticalSpeed` | 快 | 飞行 | 高 |
| 水平地表速度 `VesselState.speedSurfaceHorizontal` | kRPC `Flight.HorizontalSpeed` | 快 | 飞行 | 高 |
| 最大加速度 `InfoItems.MaxAcceleration` | kRPC `Vessel.MaxAcceleration`；或最大推力/质量 | 快 | 飞行 | 高 |
| 当前推力加速度 `InfoItems.CurrentAcceleration` | kRPC `Vessel.Thrust / Vessel.Mass` | 快 | 飞行 | 高 |
| 最大推力 `InfoItems.MaxThrust` | kRPC `Vessel.MaxThrust` | 快 | 飞行 | 高 |
| 当前推力 `InfoItems.CurrentThrust` | kRPC `Vessel.Thrust` | 快 | 飞行 | 高 |
| 海平面 TWR `InfoItems.SurfaceTWR` | MechJeb public 方法；或 `availableThrust / mass / bodySurfaceG` | 快 | 飞行 | 高 |
| 当地 TWR `InfoItems.LocalTWR` | MechJeb public 方法；或 `availableThrust / mass / localG` | 快 | 飞行 | 高；需与 MechJeb 使用相同当地重力定义 |
| 当前油门 TWR `InfoItems.ThrottleTWR` | MechJeb public 方法；或 `currentThrust / mass / localG` | 快 | 飞行 | 高 |
| G 力 `InfoItems.Acceleration` | kRPC `Flight.GForce` 或 KSP `Vessel.geeForce` | 快 | 飞行 | 高 |
| 大气阻力加速度 `InfoItems.AtmosphericDrag` | kRPC `Flight.DragAcceleration` 向量模；或 MechJeb `VesselState.drag` | 快 | 飞行 | 中高；FAR/物理模型下两路径可能略有差异 |
| 乘员容量 `InfoItems.CrewCapacity` | kRPC `Vessel.CrewCapacity` | 慢 | 飞行 | 高 |
| 当前乘员 `InfoItems.CrewCount` | kRPC `Vessel.CrewCount` | 慢/事件 | 飞行 | 高 |
| 干质量 `InfoItems.DryMass` | kRPC `Vessel.DryMass` | 慢 | 飞行 | 高；不同 mod 的模块质量修正需实机对照一次 |
| 当前质量 `InfoItems.VesselMass` | kRPC `Vessel.Mass` | 2 Hz | 飞行 | 高 |
| 零件数/上限 `InfoItems.PartCountAndMaxPartCount` | kRPC `Vessel.Parts.All.Count` + KSP 设施/难度上限；MechJeb public 方法可精确复现 | 事件 | 飞行 | 中高；“上限”不是 kRPC 的直接字段 |
| 当前级大气/真空 Δv `InfoItems.StageDeltaVAtmosphereAndVac` | 精确：MechJeb public 方法或 `MechJebModuleStageStats.atmoStats/vacStats`；备选 kRPC `Stage` Δv | 1–2 Hz | 飞行 | 中高；kRPC/KSP stock 与 MechJeb 燃流仿真可能不同 |
| 总大气/真空 Δv `InfoItems.TotalDeltaVAtmosphereAndVac` | 同上，对所有 MechJeb stage stats 求和 | 1–2 Hz | 飞行 | 中高；仿真有计算成本，必须限频 |
| 当前油门级段剩余时间 `StageTimeLeftCurrentThrottle` | MechJeb public 方法 | 2 Hz | 飞行 | 中高；零油门和极低油门需显示不可用/无穷 |
| 满油门级段剩余时间 `StageTimeLeftFullThrottle` | MechJeb public 方法 | 1–2 Hz | 飞行 | 中高；会请求级段仿真更新 |
| 悬停级段剩余时间 `StageTimeLeftHover` | MechJeb public 方法 | 1–2 Hz | 飞行 | 中高；推重比不足时需明确状态 |
| 终端速度 `VesselState.TerminalVelocity` | kRPC `Flight.TerminalVelocity` 或 MechJeb public 方法 | 2 Hz | 飞行 | 中高；不同气动模型下定义可能不同 |
| 载具成本 `InfoItems.VesselCost` | 汇总 kRPC `Part.Cost` 或 KSP part/module cost | 事件 | 飞行 | 中高；可变模块成本需要实机对照 |
| 迎角 `VesselState.AoA` | kRPC `Flight.AngleOfAttack` | 快 | 飞行 | 高 |
| 侧滑角 `VesselState.AoS` | kRPC `Flight.SideslipAngle` | 快 | 飞行 | 高 |
| 大气静压 `InfoItems.AtmosphericPressurekPA` | kRPC `Flight.StaticPressure`；统一向网页输出 Pa | 快 | 飞行 | 高；注意 KSP 内部常用 kPa，API/标签单位必须归一 |
| 当前生物群系/情境 `InfoItems.CurrentBiome` | kRPC `Vessel.Biome`；若要完全复制 MechJeb 文本则调用 public 方法 | 慢 | 飞行 | 高；MechJeb 文本还组合 landedAt/飞行高度情境 |
| 动压 `VesselState.dynamicPressure` | kRPC `Flight.DynamicPressure` | 快 | 飞行 | 高 |
| 自杀点火倒计时 `InfoItems.SuicideBurnCountdown` | MechJeb public 方法；备选自有落地预测算法 | 快 | 飞行 | 中；无 kRPC 直接等价，地形/推力变化会导致跳变 |
| 自动分级 `ThrustWindow.Autostage` | **操作，不属于遥测**；MechJeb public UI action/模块接口 | 事件 | 飞行 | 中；调查阶段不调用。未来必须显示状态并防误触 |
| 撞击倒计时 `InfoItems.TimeToImpact` | MechJeb public 方法；备选根据轨道与地形求交 | 2 Hz | 飞行 | 中；有大气/升力时只是预测，不是保证值 |
| 太阳能板展开/收回 `SolarPanelDeployButton` | **操作**；MechJeb public action 或 KSP/kRPC solar panel control | 事件 | 飞行 | 中高；未来应根据混合状态显示“部分展开” |
| 天线展开/收回 `AntennaDeployButton` | **操作**；MechJeb public action或 KSP 部件模块；RemoteTech 需专门适配 | 事件 | 飞行 | 中；RemoteTech 天线和 stock 天线语义不同 |
| 最近交会距离 `TargetClosestApproachDistance` | kRPC `Orbit.DistanceAtClosestApproach`；或 MechJeb public 方法 | 2 Hz | 飞行 | 中高；目标必须有同 SOI 有效轨道 |
| 当前目标距离 `InfoItems.TargetDistance` | kRPC/KSP 目标位置差；或 MechJeb public 方法 | 快 | 飞行 | 高；目标类型可能是 vessel、port、body 等 |

水平分隔符仅是 UI 布局元素，没有数据含义。

### 3.3 仅 UI 文本与底层数值的界线

- 纯 UI/格式化：水平分隔符、轨道摘要文本、坐标 DMS 字符串、N/A 文本、单位和 SI 缩写。
- 底层已有数值/API：绝大多数轨道、速度、姿态、压力、推力、质量、乘员、目标与气动量。
- MechJeb 特有但有公开 API：级段大气/真空 Δv、级段剩余时间、自杀点火倒计时、撞击倒计时、精确的 MechJeb 文本组合。
- 操作项：自动分级、太阳能板、天线。这三项必须与只读遥测分离。

## 4. VesselViewer Continued 0.8.9.0

### 4.1 它实际展示什么

核心程序集为：

- `VesselView.dll`：渲染器和设置。
- `VesselViewPlugin.dll`：外部窗口/工具栏宿主。
- `VesselViewRPM.dll`：RasterPropMonitor 菜单适配。
- `VVPartSelector.dll`：零件树、全局动作和单零件选择菜单。
- `VVDiscoDisplay.dll`：娱乐性自定义着色模式。

Vessel View 主画面本质上是一个 `RenderTexture` 中的载具示意渲染，而不是字段列表。它从 `FlightGlobals.ActiveVessel.rootPart` 开始遍历零件树并绘制零件网格/包围框，可叠加：

- 发动机推力方向/喷流；
- 质心；
- 坐标轴；
- 地面参考（火箭/飞机模式）；
- XY、XZ、YZ、等轴、相对、真实视角；
- 自动居中、缩放、旋转。

填充、线框和包围框可独立选择颜色模式：

| Vessel View 模式 | 实际底层数据 | 结构化复用 |
|---|---|---|
| White | 无状态数据，仅固定颜色 | 无需复用 |
| State | `Part.State`，根部另用特殊颜色 | 可直接输出枚举，稳定 |
| Stage | `Part.inverseStage` 和当前 stage count | 可直接输出，稳定 |
| Heat | `temperature/maxTemp` 与 `skinTemperature/skinMaxTemp` | 可直接输出数值和比例，稳定 |
| Resources | 每个 `PartResource.amount/maxAmount` 汇总为填充比例 | 可输出逐资源明细，稳定；比原渲染信息更丰富 |
| Drag | stock `Part.angularDrag`；若存在 FAR 则反射 FAR 的 `currentDrag` 字段 | stock 路径可取；FAR 路径版本脆弱 |
| Lift | 读取 FAR `currentLift` | 依赖 FAR 私有/动态字段，低稳定性；stock 下信息有限 |
| Stall | 读取 FAR `stall` | 依赖 FAR 字段，低稳定性 |
| Hide | 透明，不绘制 | 无数据 |

`VVPartSelector` 还维护：父子零件树、所选零件、对称选择、零件上的可激活 `BaseEvent` 列表和按动作分组的零件集合。后两项属于潜在控制面，不应在只读遥测接口中自动暴露为可执行操作。

### 4.2 三种网页呈现路线

| 路线 | 获取方法 | 建议频率 | 是否只读 | 稳定性与风险 |
|---|---|---:|---|---|
| 结构化载具示意图（推荐首版） | 自有 DLL 读取 KSP `Part.parent/children`、position/rotation/bounds、stage、温度、资源和模块状态；向网页发 JSON，由 Canvas/SVG 绘制 | 结构事件刷新；动态着色 2–5 Hz | 是 | 高；不等同于原始网格外观，但交互、缩放和点击零件更适合触控 |
| 复用 VesselView 渲染器 | 自有 DLL 直接引用 public `VesselView.VesselViewer`，调用 public `drawCall(RenderTexture)` | 原实现按设置每 1/3/10/30/75 帧重绘；网页建议只取 1–5 FPS | 是 | 中；只支持 active vessel，Unity 主线程/GPU 回读/PNG 或 JPEG 编码成本明显 |
| 抓取现有 VesselView 私有纹理 | 反射 private static `activeInstances` 和 private `screenBuffer` | 1–5 FPS | 是 | 低；强耦合实现细节，不推荐作为主方案 |

kRPC 能提供父子零件关系、stage、质量、温度、资源、位置、方向和 bounding box，因此也能支撑简化结构视图；但它不提供 Vessel View 使用的 Unity 零件网格三角形，也不会直接输出该 RenderTexture。

### 4.3 场景与生命周期

- 原版 Vessel View 渲染固定读取 `FlightGlobals.ActiveVessel`，因此需要 Flight 场景和已加载的 active vessel。
- 结构化只读快照也应首版限定在 Flight 场景；场景切换、换船、解体、对接后必须废弃旧 Part 引用并重建树。
- 温度、资源、发动机等可 2–5 Hz 更新；位置/姿态如果用于动态视图可提高到 10 Hz；零件树只在 vessel/part/stage 事件或校验哈希变化时更新。

## 5. 控制通路当前约束

- 当前只完成读取能力调查；没有执行自动分级、零件事件、太阳能板、天线、油门或姿态控制。
- 离散控制未来可优先走自有 DLL 的 KSP API，并对 RemoteTech 控制许可/信号状态做检查。
- 连续轴不能默认已完全可靠。`fixKRPC.dll` 已验证油门 0→1→0，但 FlyByWire 回调去重 v1.0.1 尚待下次重启验证；首版控制设计不应把 additive 姿态模式作为既定方案。
- 自有 DLL 若直接控制，可绕开浏览器→kRPC 的一层，但仍要处理 KSP FlyByWire、RemoteTech 延迟/许可和多人设备抢占。控制协议应采用单一控制租约、心跳超时自动释放、服务端限幅，并把遥测与命令分成不同端点。

## 6. 进入设计讨论前的结论

1. Flight Pannel 的数据可获取性很好：绝大多数不需要读 UI 文本；MechJeb 特有值也有 public API。
2. 自有 ArmorControl DLL 是最合适的聚合层，能够直接调用 KSP API，并按安装情况加载 MechJeb/VesselView 适配器；kRPC 可以作为备选和交叉验证。
3. Vessel View 的“数据”与“画面”应分开理解。结构化零件视图最适合网页触控；原样视频式渲染可作为低帧率可选视图，而不应成为唯一方案。
4. 下一步应先和用户确定网页的信息架构、手机/平板布局、Vessel View 呈现方式、允许的控制范围、危险操作确认、刷新率和断线行为，再开始编码。

## 7. MechJeb Flight Recorder 静态分析

### 7.1 记录器与图表是两个模块

- `MuMech.MechJebModuleFlightRecorder` 负责在 `OnFixedUpdate()` 中累计和保存数据。
- `MuMech.MechJebModuleFlightRecorderGraph` 只负责读取记录器的 public 历史数组并绘图、缩放和提供导出入口。
- 两个类型均位于 `MechJeb2.dll` 且为 public。记录器的 `history`、`historyIdx`、`historySize`、`precision`、最大/最小值数组和多数累计值也是 public，因此自有 DLL 可只读接入，不需要抓取图表像素。

本机配置：

| 设置 | 当前值 | 含义 |
|---|---:|---|
| `historySize` | 3000 | 固定长度数组，不是循环缓冲；写满后停止追加 |
| `precision` | 0.2 秒 | 最短落盘采样间隔，即约 5 Hz |
| `downrange` | true | 默认允许使用下行距离轴 |
| 图表 `realAtmo` | true | 使用真实大气背景 |
| 图表 `autoScale` | true | 根据记录 extrema 自动计算纵轴范围和刻度 |
| 图表尺寸 | 10 × 4 | 内部按 128 像素倍数生成，当前约 1280 × 512 |

按 0.2 秒和 3000 个样本计算，当前单次记录窗口约为 600 秒，即 10 分钟。

### 7.2 每个历史样本的字段

| 序号 | recordType | 含义/来源 |
|---:|---|---|
| 0 | `TimeSinceMark` | 自 Mark 起的游戏时间 |
| 1 | `CurrentStage` | 当前级 |
| 2 | `AltitudeASL` | 海平面高度 |
| 3 | `DownRange` | 从 Mark 点计算的地面距离 |
| 4 | `SpeedSurface` | 地表速度 |
| 5 | `SpeedOrbital` | 轨道速度 |
| 6 | `Mass` | 载具质量 |
| 7 | `Acceleration` | `Vessel.geeForce`，即 G 力而非单纯推力加速度 |
| 8 | `Q` | 动压 |
| 9 | `AoA` | 迎角 |
| 10 | `AoS` | 侧滑角 |
| 11 | `AoD` | displacement angle，中文本地化显示为“俯角” |
| 12 | `AltitudeTrue` | 相对地形的真实高度 |
| 13 | `Pitch` | 俯仰角 |
| 14 | `GravityLosses` | 自 Mark 起积分的重力损失 |
| 15 | `DragLosses` | 自 Mark 起积分的阻力损失 |
| 16 | `SteeringLosses` | 自 Mark 起积分的转向损失 |
| 17 | `DeltaVExpended` | 自 Mark 起积分的已消耗 Δv |

### 7.3 实际采样流程

1. 第一次更新时自动 `Mark()`；载具处于 `PRELAUNCH` 时每个物理 tick 都重新 Mark，因此真正历史从离开发射前状态开始。
2. 每个 `FixedUpdate` 都使用 MechJeb `VesselState.deltaT` 积分重力损失、阻力损失、转向损失和已消耗 Δv，并更新最大阻力 G。也就是说这些累计量的积分精度是物理 tick 级，不是 5 Hz。
3. 只有当 `vesselState.time >= lastRecordTime + precision` 时，才把 18 个字段的快照写进 `history`；本机 `precision=0.2`，所以历史点约 5 Hz。
4. `historyIdx` 到达数组最后一个位置后停止记录，不会自动丢弃旧样本。
5. `Mark()` 会清空历史索引、极值和累计损失，并保存起点时间、经纬度、高度、LAN 与天体。
6. CSV 会写入 `GameData/MechJeb2/Export/`，表头就是上述 18 个 `recordType` 名称。

静态代码还显示一个值得注意的行为：图表的“暂停/恢复”按钮只切换 `MechJebModuleFlightRecorderGraph.paused`，这个字段除按钮文字和切换外没有被其他逻辑读取；记录器自己的 private `paused` 字段也没有发现写入点。因此本机 2.5.1.0 程序集中该按钮看起来不会真正暂停记录或绘制。后续如果网页提供暂停，应由 ArmorControl 自己实现明确的采样暂停状态，不能照搬这个行为。

### 7.4 图表功能

- 横轴可在时间与下行距离之间切换。
- 可选择海平面高度、真实高度、G 力、地表/轨道速度、质量、动压、AoA、AoS、AoD、俯仰角及三类损失等序列。
- 可显示级段切换竖线。
- 支持自动缩放、手动横轴尺度、横纵尺寸、比例重置、Mark 和 CSV 导出。
- 自动缩放读取记录器保存的全局最小/最大值，再用 “nice number” 算法生成刻度。
- 原图表逐点遍历历史数组绘制线段；若直接把采样率从 5 Hz 提升到 60 Hz 且保持 10 分钟，点数会从 3000 增至 36000，原绘图方法会承担约 12 倍的逐帧绘制工作。

网页端不应复制这种即时模式绘图实现。建议保留高频原始环形缓冲，并按当前像素宽度做 min/max 或 LTTB 降采样后再发给浏览器。

## 8. 刷新率与 60 Hz 以上的可行性

### 8.1 结论

60 Hz 的网页动画和紧凑快速遥测是可行的；此前给出的 5–10 Hz 是保守的全量数据刷新建议，不是硬限制。真正不合理的是把所有数据——包括级段 Δv 仿真、完整零件树、资源明细和 Vessel View 图像——都以 60 Hz 重新计算、序列化和发送。

应把“采样率、网络推送率、浏览器绘制率”分开：

| 层 | 推荐策略 |
|---|---|
| 游戏物理采样 | 快速状态在每个 `FixedUpdate` 抓取一次；实际新鲜度由当前物理 tick 决定 |
| 快速网络通道 | 姿态、速度、油门、操纵输入等用紧凑包，目标 30–60 Hz，可配置 |
| 慢速网络通道 | 轨道摘要、资源、热状态、MechJeb 级段仿真等 1–10 Hz，按字段成本分组 |
| 结构事件 | 零件树、对接、解体、换船等事件触发，不轮询 60 Hz |
| 浏览器动画 | `requestAnimationFrame` 跟随设备 60/90/120 Hz，在最近两个物理样本之间插值 |
| Flight Recorder | 兼容 MechJeb 时读取其 5 Hz 历史；增强模式可由 ArmorControl 以物理 tick 频率另建环形缓冲 |

### 8.2 主要限制因素

1. **物理模拟频率**：很多关键值只在 `FixedUpdate` 后真正变化。网页以 60/120 Hz 读取并不会自动产生同样数量的新物理状态，中间会是重复值；平滑应由浏览器插值完成。实际物理 cadence 会受卡顿、物理时间加速和 CPU 负载影响，需要运行时测量，不能硬编码为固定 50 Hz。
2. **Unity/KSP 主线程约束**：Vessel、Part、MechJeb 等对象大多应在主线程读取。每帧遍历大量零件或调用重型模拟，会直接抢占游戏帧时间。
3. **MechJeb 计算成本**：`StageDeltaV...` 和燃烧时间会请求 `MechJebModuleStageStats` 更新；它们不应每帧调用。普通数值 getter 则便宜得多。
4. **序列化和多客户端放大**：小型快速包在局域网以 60 Hz 没有问题，但 45 个字段、资源明细或完整零件状态乘以多个设备后，会增加主线程分配、GC、带宽和手机端 JSON 解析成本。
5. **图像路径**：Vessel View 原图若走 RenderTexture→CPU 回读→PNG/JPEG→网络，60 FPS 成本很高；结构化 Canvas/SVG 可以只更新状态并由浏览器 60/120 FPS 绘制。
6. **历史图点数**：高频记录本身的内存成本可控，但不能每个浏览器帧把数万点全量重发和重画，必须使用增量传输、环形缓冲和显示降采样。

### 8.3 建议的首版性能目标

- 快速飞行数据：默认 30 Hz，设置中允许 60 Hz；若运行时实测物理 tick 和帧预算允许，可继续开放更高值。
- 浏览器仪表动画：跟随屏幕刷新率，可达 60/90/120 Hz，通过插值获得平滑效果。
- 控制输入：浏览器最多按显示帧发送，但服务端只保留最新值，并在每个物理 tick 应用一次；断线立即释放。
- Flight Recorder 增强采样：默认 20 Hz，提供“物理 tick”模式；MechJeb 原历史仍保持只读兼容。
- 轨道/资源/热状态：2–10 Hz，按字段成本细分。
- MechJeb 级段仿真：1–2 Hz 或脏状态触发。
- 原版 Vessel View 图像：从 5–15 FPS 起测；结构化视图本身可在浏览器以屏幕刷新率动画。

最终上限不应靠猜测决定。实现后要记录每类采样耗时、序列化耗时、包大小、客户端数、KSP 帧时间和丢帧，再让自适应调度器在预算内提高或降低频率。

## 9. MechJeb Maneuver Planner 与 Porkchop

MechJeb 的 Maneuver Planner 会长期保留各个 `Operation` 实例，并调用当前 Operation 自己的 `DoParametersGUI()`。因此每种机动拥有不同参数与前置条件，切页后参数也会保留；网页实现必须沿用“操作 schema + 操作状态”的模型，不能只替换标题。

`OperationAdvancedTransfer` 有两种模式：`LIMITED_TIME` 与 `PORKCHOP`。Porkchop 模式通过 `AllGraphTransferCalculator` 计算二维矩阵，当前源码的图高为 200 个样本；默认时间范围从当前 UT 开始，出发窗口覆盖约 1.5 个会合周期，最大转移时长约为霍曼转移时间的两倍，最小采样步长为 12 小时。

图中横坐标映射出发时间，纵坐标映射转移时长，矩阵单元为 Δv 成本。MechJeb 默认选中全局最低 Δv 点；“ASAP”则固定在最早出发列，并从该列挑选成本最低的转移时长。选点随后不仅决定显示值，还为 `OptimizeEjectionToTarget()` 提供 epoch、到达时长与局部优化区间。

高级转移的关键工程约束：

- 选择目标、模式、时间范围或“包括捕获燃烧”变化时会触发重算；旧 worker 必须可取消。
- 捕获燃烧启用时，目标近拱点参与最终成本/捕获轨道求解；目标变化时可按目标是否有大气设置合理默认高度。
- 创建节点前必须检查计算是否完成、是否失败、是否存在有效选点，以及当前轨道和目标是否满足星际转移前置条件。
- 网页端应保留解算结果纹理/网格，只在重算后更新；触控选点不触发整个矩阵重传，只请求所选样本的精确解与节点预览。
