# Armor Control

通过网页远程监控和控制载具，为触控和桌面操作设计的 KSP 飞行控制台。

KSP 1.12.5 · 版本 0.1 · 早期测试 · CC BY-NC-SA

[English](README.md) | 简体中文

[下载](https://github.com/Armo00/ArmorControl/releases) · [KSP 论坛](https://forum.kerbalspaceprogram.com/topic/231740-1125-armor-control-v01/) · [反馈问题](https://github.com/Armo00/ArmorControl/issues)

![Armor Control 总览](Screenshot/Mainpage.jpg)

## 什么是 Armor Control？

Armor Control 把手机、平板或另一台电脑变成 KSP 的远程飞行控制台。通过网页监控载具状态、操作系统、规划机动，并使用已支持的自动驾驶功能。

HTTP/WebSocket 服务直接运行在 KSP 内部，无需额外服务器、kRPC 或专用的 Armor Control 零件。界面支持简体中文和英文，默认简体中文。

## 听起来有些熟悉？

没错！Armor Control 的灵感来自 [Telemachus](https://github.com/TeaGuild/Telemachus-1)，以及将 KSP 任务控制中心搬进浏览器的想法。

我希望在这一思路上，开发一个以更高性能和更广泛适用性为目标的远程监控与控制界面，将实时遥测、载具交互和其他 Mod 的功能整合到一起。性能是开发目标，并不是已经通过基准测试证明优于 Telemachus 的结论。

我也想把一些最常用的操作转移到另一台设备，让 KSP 主画面少一些窗口遮挡，留下更清爽的飞行视野。感谢 Telemachus 项目带来的灵感。

## 使用前请注意

**早期测试：**0.1 是实验性版本，可能有大量 Bug、未完善功能和兼容性问题。请提前备份存档。

### 依赖

| 组件 | 0.1 版本中的要求 | 兼容性说明 |
| --- | --- | --- |
| KSP | 必需 | 针对 1.12.5 开发和测试。 |
| MechJeb | 必需 | 使用 **2.14.3**，飞行数据、记录、节点和自动驾驶等功能依赖 MJ。 |
| VesselView / Vessel Viewer | 必需 | 载具渲染适配 **0.8.9.0**。 |
| Trajectories | 可选 | 轨迹预测页面需要；当前适配本地 **2.4.5.4** 程序集。 |

MechJeb 和 VesselView 是当前版本的直接程序集依赖，即使只使用基础控制，也不要假设缺少它们时插件可以正常启动。MJ 功能还需要当前载具有可用的 MechJeb Core。

我在自己的游戏环境中遇到了较多 MechJeb 2.15.x 的问题，因此目前限定支持 2.14.3，不支持其他 MJ 版本。其他版本的 VesselView 和 Trajectories 也不保证兼容。安装包不包含这些依赖。

**网络安全：**默认关闭鉴权，使用明文 HTTP/WebSocket。仅在可信局域网中使用，不要暴露到公网或设置路由器端口转发。可选的访问令牌并不会加密连接。

**多设备：**多个设备共享同一活动载具的控制，指令按接收顺序执行，没有用户角色或独占控制权。请协调操作；分级、EVA、加载 quicksave 都会真实影响游戏。

**AI 使用说明：**本 Mod 使用了 AI 辅助开发。如果你无法接受使用 AI 辅助开发的软件，请不要使用。

## 安装与连接

目前通过 [GitHub](https://github.com/Armo00/ArmorControl) 分发，SpaceDock 和 CKAN 支持在计划中。

1. 安装上表中的必需依赖。
2. 从 [Releases](https://github.com/Armo00/ArmorControl/releases) 下载打包好的安装 ZIP，不要使用 **Code → Download ZIP** 或 GitHub 自动生成的源码压缩包。
3. 关闭 KSP，将安装包解压到 KSP 安装目录，合并 `GameData` 文件夹。
4. 保留整个 `ArmorControl` 文件夹，包括 `prototype` 下的网页和 `Localization`。

安装后的主要结构：

```text
KSP 安装目录/
└── GameData/
    └── ArmorControl/
        ├── Plugins/ArmorControl.dll
        ├── prototype/
        │   ├── index.html
        │   ├── app.js
        │   └── Localization/
        └── settings.cfg
```

Armor Control 已独立于 ArmorOverhaul。从旧的集成版本升级时，请只移除旧位置的 ArmorControl 文件和 DLL，避免重复加载。不要删除无关的 ArmorOverhaul 内容。

### 打开控制台

启动 KSP，服务端默认自动启动。通过游戏工具栏的 **ArmorControl 按钮**查看状态、修改端口，或启动与停止服务。

| 访问设备 | 浏览器地址 |
| --- | --- |
| 运行 KSP 的电脑 | `http://127.0.0.1:8765` |
| 同一局域网中的其他设备 | `http://<KSP电脑的局域网IP>:8765` |

例如，游戏电脑的局域网 IP 是 `192.168.1.100`，则在手机上打开 `http://192.168.1.100:8765`。如果改过端口，请使用新端口。必要时在防火墙中仅对可信专用网络放行。

进入飞行场景即可查看载具数据，网页会跟随 KSP 当前活动载具。

### 语言

在网页右上角选择 **简体中文** 或 **English**。选择按浏览器保存，不影响其他设备；游戏内服务面板单独保存语言设置。

欢迎修改 [`prototype/Localization`](prototype/Localization) 中的 JSON 词典，详见[本地化维护说明](prototype/Localization/README.md)。

## 功能介绍：按页面查看

<details>
<summary>飞行：实时仪表</summary>

提供适用于航天飞行的姿态球，以及实时姿态、高度、速度、垂直速度、推力、推重比、动压和关键轨道信息。

![飞行：实时仪表](Screenshot/Flight%20Page.png)

</details>

<details>
<summary>飞行面板：高密度遥测</summary>

面向大屏幕，按轨道、地表、性能、载具和飞行分类展示数据，包含航向、水平速度、攻角、侧滑角、大气压力、所在生物群系，以及数据可用时的极限着陆点火倒计时。

![飞行面板：高密度遥测](Screenshot/Flight%20Pannel.png)

</details>

<details>
<summary>飞行包线：限制与发动机保护</summary>

调整已支持的 MechJeb 最大动压、最大加速度、最小油门、过热保护和自动分级等设置。

![飞行包线：限制与发动机保护](Screenshot/Flight%20Envelop.png)

</details>

<details>
<summary>飞行记录仪：图表与历史数据</summary>

分组查看高度、速度、气动、姿态、损失和载具性能。可切换时间或下行距离横轴、查看历史采样、显示分级标记并导出 CSV。清屏后以当前时刻作为新的 T+0。

![飞行记录仪：图表与历史数据](Screenshot/Recorder.png)

</details>

<details>
<summary>载具：可视化与零件操作</summary>

查看载具图像，选择并高亮零件，通过快速分组查找引擎、RCS、灯光、起落架、货舱、对接口和其他系统。

支持单独启停引擎、调整推力限制、开关万向节，以及部分零件右键菜单操作。根据零件和 Mod 的支持情况，可执行开伞、散热器启停、分离、解除对接和资源转换器启停等操作。适用分组提供一键全开/全关。温度视图突出显示接近温度极限的零件，气动中心可视化在数据可用时考虑 FAR。

并非所有零件动作都受支持，操作是否可用也取决于当前飞行状态。

![载具：可视化与零件操作](Screenshot/Vessel.png)

</details>

<details>
<summary>成员：当前载具名册</summary>

查看当前载具上的宇航员、所在舱室及职业。条件允许时，可通过确认操作让指定宇航员进行 EVA。

![成员：当前载具名册](Screenshot/Crew.png)

</details>

<details>
<summary>目标：选择与相对运动</summary>

通过分层菜单浏览天体和载具、选择或清除目标，并查看可用的目标与相对运动信息。

![目标：选择与相对运动](Screenshot/Target.png)

</details>

<details>
<summary>轨迹预测：落点数据与地面地图</summary>

查看 Trajectories 预测并调整下降姿态假设、显示选项和计算参数。数据可用时显示撞击点坐标、距离撞击的时间、撞击速度和撞击点到目标的距离。

地图上方为北，显示预计地面轨迹、撞击点和目标，支持自适应显示范围和比例尺。此页使用 Trajectories 自身的预测目标，不要假设在 KSP 其他界面选择的目标都会自动同步。

![轨迹预测：落点数据与地面地图](Screenshot/Trajectory.png)

</details>

<details>
<summary>节点规划：机动与 Porkchop 选点</summary>

通过已支持的 MechJeb 操作创建和管理节点，各机动类型显示对应参数。高级转移提供 Porkchop 图，用于比较出发时间、转移时长和 Δv 成本。

还提供轨道预览、节点队列、执行与中止控制，以及 MechJeb 自动时间加速设置。

![节点规划：机动与 Porkchop 选点](Screenshot/Node.png)

</details>

<details>
<summary>自动驾驶：五个 MechJeb 子页面</summary>

- **自动发射：**上升剖面、目标轨道、发射时机、制导约束，以及实时发射与轨道读数。
- **自动着陆：**着陆目标、制导设置、预测信息和自动着陆控制。
- **Smart A.S.S.：**姿态模式及偏移输入，支持增减按钮和可选角度步长。
- **自动交汇：**已支持的交汇设置及执行控制。
- **自动对接：**对接控制、对接轴选择、强制滚转，以及安全距离和起始距离覆盖设置。

![自动驾驶：五个 MechJeb 子页面](Screenshot/Autopilot.png)

</details>

<details>
<summary>基础控制：载具系统与触控输入</summary>

操作载具系统、01–10 动作组、油门以及触控俯仰、偏航和滚转。分级与加载 quicksave 使用滑动确认。

MechJeb 自动发射、节点执行或自动着陆期间，网页手动油门锁定；姿态输入仍然可用，不会自动取消 MJ 当前任务。

![基础控制：载具系统与触控输入](Screenshot/Control.png)

</details>

## 排查问题与反馈

- **网页打不开：**检查工具栏中的服务状态、端口、游戏主机 IP、防火墙，以及设备是否能够互相访问。手机上的 `127.0.0.1` 指向手机自身，不是游戏电脑。
- **网页打开但没有数据：**检查飞行场景、活动载具、连接状态和依赖安装情况。
- **MJ 操作不可用：**检查版本是否为 2.14.3、载具是否有 MJ Core，以及是否设置了所需目标或节点。
- **缺少零件操作：**可能是该零件、Mod 或当前飞行状态不支持。

请在 [GitHub Issues](https://github.com/Armo00/ArmorControl/issues) 提供 KSP 与 Mod 版本、复现步骤、预期与实际行为、浏览器和设备信息，以及相关日志或截图。分享日志前请移除令牌和其他隐私信息。

## 配置与开发

服务设置位于 `GameData/ArmorControl/settings.cfg`，默认值见 [`settings.example.cfg`](settings.example.cfg)。手动修改前请关闭 KSP。默认端口为 `8765`，监听 `0.0.0.0`，自动启动且不设置访问令牌。配置中的遥测频率是采样目标，不是保证能达到的帧率。

源码位于 [`Source/ArmorControl`](Source/ArmorControl)。源码仓库不是可直接安装的发行包，Git 忽略生成的 DLL 和本机配置。当前项目需要放在 KSP 安装目录的 `GameData/ArmorControl` 下，并具备所需的本地依赖程序集。准备好合适的 .NET SDK 与 .NET Framework 4.6.1 目标支持后，可在本目录执行：

```powershell
dotnet build Source/ArmorControl/ArmorControl.csproj -c Release
```

DLL 输出到 `Plugins`，中间文件保存在 `GameData` 之外，以免被 KSP 重复加载。维护者的验证与打包脚本目前位于游戏根目录的外部 `BuildTools` 中，尚未纳入本仓库。

[协议说明](PROTOCOL.md) · [本地化维护说明](prototype/Localization/README.md) · [发布约定](RELEASE.md)

## 许可与致谢

Armor Control 以 **CC BY-NC-SA** 发布。第三方 Mod 保留各自许可证，不随安装包附带。

感谢 Telemachus 提供灵感，以及 MechJeb、VesselView、Trajectories 和其他集成 Mod 的开发者。
