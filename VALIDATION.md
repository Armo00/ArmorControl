# ArmorControl 验收记录

日期：2026-09-04（Asia/Shanghai）

## 自动化回归

- 生产 `net461` 构建：0 warning，0 error。
- 前端 `node --check`：通过。
- HTTP 静态文件、健康检查与路径约束：通过。
- 共享令牌：无令牌 API 返回 401，正确令牌 API 返回 200，带令牌 WebSocket 完成协议握手。
- 八客户端压力：4 个持续读取、4 个不读取；活动客户端均收到第 2000 个 latest-only 快照，慢客户端未阻塞其他连接。
- 双客户端全局 FIFO：服务端序号严格按到达顺序递增。
- 幂等：排队中的重复 `commandId` 只执行一次且共享回执；已完成 ID 重试重放原回执。
- 快速/常规遥测、Flight Pannel、Automation、Recorder、结构事件、Porkchop、心跳：全部通过协议回归。
- Automation 回归包含完整有序机动节点队列、地面位置目标与 Smart A.S.S. 自动停用状态。
- 节点规划时间选择直接映射 MechJeb `TimeSelector`；新增节点从最后节点后的轨道分支继续，替换模式从被替换节点的前驱轨道重新求解。
- 浏览器验收确认 2 个节点均显示真实 UT、Δv 和倒计时；未解算 Porkchop 不显示模拟成本；自动着陆目标、Smart A.S.S. 偏移限制和自动对接停止命令均正确呈现。
- 宽屏浏览器验收确认 Pitch/Yaw、Roll、Throttle 三个独立触控面均为 `touch-action: none`，动作组严格为 01–10。
- 独立静态服务器断线验收：页面显示“等待游戏”，关键遥测全部为 `—`，成员/零件显示明确连接指引；不存在旧原型模拟遥测动画。
- `ArmorControl.VerifyRelease.ps1` 一键发布闸门已通过，并明确检查 8 个页面、必要控制命令、10 个动作组和禁止模拟遥测约束。

## KSP 真实进程

- KSP 1.12.5 成功加载 `ArmorControl/Plugins/ArmorControl.dll`。
- 日志确认监听 `0.0.0.0:8765`，快速/常规采样配置为 30/5 Hz。
- 真实服务：`/health` 200；无令牌 `/api/v1/crew` 401；正确令牌 200。
- 带令牌浏览器完成真实 WebSocket 握手并显示“实时”。
- 812 次快速采样：捕获平均 0.010935 ms、峰值 0.2064 ms。
- 182 次常规采样：捕获平均 0.006129 ms、峰值 0.3104 ms。
- 快速 JSON 序列化：平均 0.021969 ms、峰值 1.5069 ms。
- 本轮日志未出现 ArmorControl 警告或异常。
- 最新发布 DLL（SHA-256 见下）再次在 KSP 主菜单进程中加载：协议 v1 健康检查通过，鉴权 API 分别返回 401/200；随后已退出 KSP 并恢复 DevHost。

## 发布体积

- `ArmorControl.dll`：116,736 bytes。
- HTML/CSS/JS：214,343 bytes。
- DLL SHA-256：`1A8BF3D4E1FE65AF1B845A8A3E46A1FB723E81818227F46B77CFF14FD2B49048`。
- ZIP 内容已逐项核对，只包含 `GameData/ArmorControl/` 与 `GameData/ArmorControl/Plugins/ArmorControl.dll`，保留可直接解压安装的目录结构。
- 安装包已解压到隔离临时目录做冒烟检查：DLL、HTML 均存在，解压 DLL SHA-256 与工作区发布 DLL 完全一致；临时目录随后已清理。

## 真实数据一致性审计（2026-09-04）

- 当前运行中的旧 DLL 接受了 `ArmorControl.LiveAudit.ps1` 的 15 项端到端检查：健康检查、五类 WebSocket 帧、载具身份、速度、航向、质量、零件数和成员数等 13 项通过。
- 审计准确检出旧 DLL 的两项失败：Flight Pannel 静压把 Pa 标成 kPa；Smart A.S.S. 在目标为 `OFF` 时仍报告为活动。两项均已在下一版 DLL 修正，等待 KSP 重启后复验。
- 浏览器对 1440×900、1024×768、390×844 三种视口逐页检查，8 个页面均无横向溢出；三个连续控制区域均保持 `touch-action: none`，手机视口触控宽度为 284 px。
- 已清除旧 DevHost 页面驻留造成的演示数据，修复主飞行页内嵌面板、操作状态、Recorder 空状态、Porkchop 路线、动态仪表刻度和载具拓扑图的静态设计稿残留。

## 尚需人工飞行验收

下列项目必须在用户选定存档和活动载具后验证，自动启动停留在加载/主菜单阶段不会修改存档：

- Flight Pannel 与当前 MechJeb 面板逐项对照。
- MARK、节点创建/执行、Porkchop、Smart A.S.S.、自动着陆和自动对接的实际行为。
- SAS/RCS/动作组/分级/部署件权威回显。
- 触控连续控制、浏览器断线 350 ms 归零以及手机+平板同时操纵的飞行安全测试。

## 目标、上升与载具快速分组（重启前验证）

- 本地 MechJeb DLL 文件版本固定为 2.14.3.0；新增代码仅按该版本公开类型编译。
- Release 构建成功，0 警告、0 错误；`app.js` 通过 `node --check`。
- 真实 KSP 服务静态页面重载后无浏览器 console warning/error。
- 载具页已完全移除模块状态列表；Echo Shuttle 的动态分类结果为：引擎 11、货舱 17、起落架 3、灯光 13、控制面 14、对接口 1、能源 1。
- 自动控制页显示 5 个 MJ 模块；Gravity Turn、PVG、Classic 面板会随制导器切换，自动交汇显示 3 个可写参数。
- 节点页 Auto-warp 开关在普通节点与高级 Porkchop 页面均可见。
- 768×1024 与 390×844 视口没有横向溢出；390 px 视口姿态球实际宽度约 203 px，底部导航保持可触控。

## 新 DLL 飞行场景复验（2026-09-04）

- KSP 以前台窗口重启，日志确认从 `LOADING` 到 `MAINMENU`；ArmorControl 随 DLL 加载自动监听 `0.0.0.0:8765`。
- `game.quicksave.load` 在主菜单返回成功回执，场景随后从 `MAINMENU` 进入 `FLIGHT (Async)`；KSP 保持运行。
- 扩展版 `ArmorControl.LiveAudit.ps1` 全部通过：原有跨通道数据一致性、Smart A.S.S. OFF 状态，以及 `mechjeb.ascent`、`mechjeb.rendezvous`、`mechjeb.autowarp`、`target.catalog` 能力与对应遥测均通过。
- 目标页在实际存档中收到 105 个目录项；Earth 分支可展开并列出其载具。未在测试中更改 KSP 目标。
- Smart A.S.S. 回读 Canvas 为 170×170；当前 Pitch +90° 时显示航天球极区收敛网格，页面无 console warning/error。
- Auto-warp 从自动控制页切换后，节点页立即显示同一值；测试结束已恢复原始启用状态。
- 载具页确认不存在“模块状态”文案；实际快速分组均有结果。完整传输层套件 15 项全部通过。
- 本次 DLL：146,944 bytes，SHA-256 `69BC1AA7BCB41D44701C64A9EFADAACFB4E6BD0FEB3B5E4E1699CDFE30329E02`。
