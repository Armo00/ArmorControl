# ArmorControl

ArmorControl 是运行在 KSP 1.12.5 游戏进程内的局域网飞行控制网页。DLL 自带 HTTP/WebSocket 服务；手机、平板或电脑直接访问游戏主机，无需另行启动 kRPC 或 Web 服务。

## 依赖与安装

- KSP 1.12.5，.NET 4.x Runtime。
- MechJeb2。原生载具控制不依赖它，但 Flight Pannel、Flight Recorder、节点规划和自动驾驶页面需要当前安装中的 MechJeb2。
- 保持目录结构：`GameData/ArmorControl/Plugins/ArmorControl.dll` 与 `GameData/ArmorControl/`。

ArmorControl 现使用独立目录，不需要放在 ArmorOverhaul 中。升级时不要同时保留旧位置的
`ArmorControl.dll`，否则 KSP 可能重复加载。网页、Localization 和 settings.cfg 均随本目录保存。
开发源码位于 `Source/ArmorControl`；专用构建与测试脚本仍在 KSP 根目录的 `BuildTools`。
编译中间文件和审核 DLL 保存在 GameData 以外，不随安装包发布。

## 首次使用

1. 启动 KSP，点击游戏右侧工具栏中的 ArmorControl 图标。
2. 在面板中确认或修改端口，点击 `Start Server`。图标变为绿色后服务已经运行。
3. 在游戏主机防火墙中只对可信的专用网络放行所配置 TCP 端口（默认 8765）。
4. 其他设备直接打开 `http://<游戏主机局域网IP>:8765/`。

当前默认关闭访问令牌，只适用于可信局域网。如以后配置了 `accessToken`，新设备首次访问使用 `http://<游戏主机局域网IP>:8765/#token=<accessToken>`。当前传输是局域网明文 HTTP/WebSocket，不应直接暴露到公网。

## 配置

- `enabled`：总开关。
- `autoStartServer`：是否在 DLL 加载后自动启动；默认 `True`。仍可通过游戏内面板停止或重新启动服务。
- `bindAddress`：监听地址；`0.0.0.0` 允许其他设备访问，`127.0.0.1` 仅限本机。
- `port`：1024–65535。
- `fastTelemetryHz`：1–60 Hz，默认 30 Hz。
- `regularTelemetryHz`：1–20 Hz，默认 5 Hz。
- `accessToken`：共享访问令牌；当前默认为空，即关闭鉴权。

## 控制安全

- 多设备的离散控制进入同一个全局 FIFO，没有用户角色或控制权租约。
- 相同 `commandId` 幂等处理，网络重试不会重复执行分级等操作。
- 分级、加载 quicksave 和 EVA 需要单击后将确认滑块拖到最右端。
- 连续控制每 100 ms 续期，350 ms 超时；断线、切换载具、页面隐藏或主动解除都会归零。
- 页面只在收到成功回执或后续权威状态时确认开关状态。

## 诊断

- `http://<主机>:8765/health` 返回客户端数量以及快慢采样、序列化平均/峰值耗时。
- 若页面显示演示模式，检查 KSP 日志中的 `[ArmorControl]`、端口占用、防火墙和 URL 中的令牌。
- 若端口被占用，面板会显示启动失败并每 5 秒重试；点击 `Cancel Start` 可取消。
- 若只有 MechJeb 功能不可用，确认活动载具有可用 MechJeb 核心并已设置所需目标。
- Porkchop 解算是异步的；切换目标或重新解算会取消旧任务，矩阵只在新 revision 完成后获取一次。

## 构建与发布

- `BuildTools/ArmorControl.VerifyRelease.ps1`：编译 Release DLL，运行多客户端/协议回归、前端语法检查，并核对必要页面、控制命令、动作组数量和“禁止模拟遥测”安全约束。
- `BuildTools/ArmorControl.PackageRelease.ps1`：先执行完整验收，再生成保留 `GameData/ArmorControl/...` 安装路径的 ZIP。
- 当前安装包位于 `BuildTools/Releases/ArmorControl-0.1.0.zip`；解压到 KSP 根目录即可。
