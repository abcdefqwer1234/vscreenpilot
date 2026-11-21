# 架构说明

> 目标：把“能跑”变成“可探测、可归因、可验证”。本文描述当前实现与后续替换点。

## 1. 分层与程序集

重构后按编译期边界拆成 4 个程序集，引用方向只能自上而下：

```
Server ──────> Automation ──────> Platform ──────> Abstractions
   │                │                  │
   └────────────────┴──────────────────┴──────────> Abstractions
```

| 程序集 | 目标框架 | 职责 |
|---|---|---|
| `VScreenPilot.Abstractions` | `net10.0` | 错误模型（`ErrorCatalog`/`ToolException`）、JSON 参数读取、序列化口径（`VspJson`）、输出视图（`Views/*`）、版本信息、进程内指标 |
| `VScreenPilot.Platform` | `net10.0-windows` | Win32/COM 原语、显示拓扑与 VDD、窗口枚举、输入注入/消息输入/键位/剪贴板、Job 对象、管道完整性校验 |
| `VScreenPilot.Automation` | `net10.0-windows10.0.19041.0` | UIA/MSAA、屏幕捕获与 PNG、OCR、模板匹配（会话组件正在从 Server 迁入） |
| `VScreenPilot.Server` | 可执行 | MCP 传输与工具层、会话编排、CLI、IPC、组合根 |

约束由 `tests/VScreenPilot.Tests.Contract/ArchitectureTests.cs` 断言：`Abstractions` 不引用任何内部工程，`Platform` 只引用 `Abstractions`，`Automation` 不引用 `Server` 与 MCP SDK。

层内分工：

```
MCP 传输层   官方 ModelContextProtocol SDK（stdio；协议协商/分帧/取消/进度由 SDK 负责）
             多客户端：首个进程优先 spawn 独立 daemon（PipeHost），失败回退进程内 daemon；后续进程用 StdioProxy 转发
工具层       McpTransport（协议适配）+ ToolRegistry/ToolDescriptor（目录与 schema）
             + ToolExecutor（策略驱动的租约/写锁/超时/epoch）+ ToolHandlers（参数解析与调用）
会话层       SessionManager：虚拟屏、应用接管、快照、UIA、输入、断言、脚本
             ClientSessionRegistry：per-client 上下文；ClientPathPolicy：路径与截图落盘边界
感知层       ScreenCapture（复用 GDI 资源）/ PngWriter（池化 + Up 过滤）/ PngReader / TemplateMatcher
平台层       Interop（Win32/COM）、DisplayTopology、VddDevice、JobObject、CursorGuard、InstanceRegistry
```

> 过渡状态：`Session/*` 仍留在 `Server`，因为它依赖 `LogRing`/`InstanceRegistry`/`StateStore`/`TraceLog`，而 `Automation` 不能反向引用 `Server`。
> `Automation` 已接收无 Server 依赖的会话组件（`ArtifactStore`、`FrameStore`、`SessionGuard`、`ClientSessionRegistry`、`ClientPathPolicy`、`ElementRefStore`、`TrackedApp` 等）。
> 不变量清单见 `docs/refactor-invariants.md`。


## 2. 并发模型

- **只读观察不排队**：`snapshot`、`capture_screen`、`find_element`、`get_ui_tree`、
  `wait_for`、`assert_region`、`match_visual`、`get_log` 不获取写锁，因此长等待
  不会让“看一眼屏幕”返回 `E_BUSY`。
- **写操作串行**：`click`、`gesture`、`type_text`、`press_keys`、`invoke_element`、
  `set_element_value`、`launch_or_place_app`、`release_environment`、`run_script`
  通过 `WriteScheduler` 串行；`E_BUSY` 带 `holder` 与 `retry_after_ms`。
- **超时不释放写锁**：卡在 UIA 的 handler 仍在操作 UI，锁等它真正结束后再释放。
- **会话代际（epoch）**：`release_environment` 先递增 epoch；旧 handler 的结果被丢弃，
  返回 `E_CANCELLED`，避免“清空集合后旧 handler 继续写”。
- **每调用取消**：`AsyncLocal<CancellationTokenSource>`，并发工具不会互相清取消。
- **UIA 串行**：`UiaHost` 用专用 MTA 线程串行执行 `UiaService` 的服务级调用（建树、查找、读取焦点）；
  超时后 `ResetUia` 只退休当前代际并让新调用走新线程，旧线程把手上的 COM 调用做完再释放。
  `UiaElementHandle` 的 Focus/Invoke/GetText 仍由 `UiaService._sync` 串行（MTA 对象可跨线程使用）。
- **MSAA 回退**：单个窗口 UIA 完全不可见时，`MsaaService` 用 `IAccessible` 枚举子控件；
  Word `bosa_sdm_msword` 对话框走这条路径。
- **UWP**：顶层类是 `ApplicationFrameWindow`，内容进程没有独立 HWND；UIA 从 frame 取树，
  ref 归属按 HWND 的进程校验，避免跨进程误判。
- **多客户端共享**：首个 MCP 进程优先 spawn 独立 `--daemon`（Job breakaway，失败回退进程内 daemon），
  由它启动 `PipeHost`/`ctl`；后续进程只做 `StdioProxy`。每个连接有独立 `ClientContext`
  （refs/apps/epoch/帧/artifacts/路径基准）；`ScreenLease` 保证同一时刻只有一个客户端执行 GUI/视觉操作，
  其他客户端得到 `E_SCREEN_LEASED`，空闲后 daemon 按 `VSP_DAEMON_IDLE_MS` 退出。

## 3. 快照模型

`snapshot` 一次返回：

- `snapshot_id`：快照作用域；`snapshot`（或 `get_ui_tree(new_snapshot=true)`）推进代际，旧代保留一代用于报错；
- `tree`：结构化树；`text`：行式紧凑格式（`- Button "保存" [e12] {disabled}`）；
- `windows[]`：本会话应用；
- `dialogs[]`：owned windows（经典 `#32770`、Office `bosa_sdm_msword`、`#32768` 菜单）；
- `foreground`：仅对本会话窗口返回标题；
- 可选 `screenshot`（默认 0.5 缩放）。

`find_element`/`get_ui_tree` 复用当前代并返回 `snapshot_id`，不会让刚拿到的 ref 失效。
ref 只在该代内有效；跨代使用返回 `E_STALE_SNAPSHOT`，未知/被淘汰返回 `E_ELEMENT_STALE`。

## 4. 输入通道

`channel=auto` 的选择顺序：

1. UIA pattern（Invoke/Value），不移动光标；
2. 窗口消息（client 区；DPI-unaware 目标先做 Per-Monitor 转换）；
3. `channel=input`：SendInput，绝对坐标带 `MOUSEEVENTF_VIRTUALDESK`；
4. 长文本（≥200 字）默认走剪贴板 `Ctrl+V`，保存/还原文本剪贴板；
5. 非客户区（标题栏/边框/系统按钮）在 message/auto 下明确返回
   `E_INPUT_UNSUPPORTED`，提示改用 `channel=input`。

系统键按语义拦截：任何含 `Win`、`Alt+Tab`、`Ctrl+Alt+Tab`、`Alt+Esc` 的组合默认拒绝。

## 5. 感知与内存

- `ScreenCapture` 复用 screen DC / memory DC / DIB（最多 4 个尺寸），不再每帧新建 3.7MB DIB；
- `PngWriter` 使用 `ArrayPool` 扫描线与行缓冲，缩放与行转换一遍完成；
- 默认 `VSP_PNG_FILTER=up`（可用 `adaptive`/`none`）；UI 截图体积通常降 2–4 倍；
- 帧缓存 8 张 / 32MB；`capture_screen` 全屏默认 `scale=0.5`；
- 大图默认落 artifact 目录并返回 `vsp://artifacts/...`，不默认内联 Base64。

## 6. 已知未完成（下一阶段）

- **会话组件化**：`SessionManager` 尚未拆到薄门面（P4），快照/应用/显示/输入仍在 partial 中。
- **领域视图**：22 个工具中 5 个已走强类型视图，其余仍由 handler 直接拼 `JsonObject`（P3）。
- **UIA 句柄亲和**：服务级调用已进 `UiaHost` 的 MTA worker；句柄级操作仍走 `UiaService._sync`。
- **全屏截图**：复用资源 + 去 `CAPTUREBLT` + 大图不内联后，服务端 P95 已降至 ~56ms；
  早期“1.2–1.4s”是内联大 PNG 的回传成本，已由 preview/artifact 方案消除。
  Windows.Graphics.Capture（`CreateForMonitor` + D3D11 staging readback）仍是后续后端优化。
- **UIA 专用 MTA worker**：当前用锁串行化；下一阶段改为单线程队列，彻底消除跨 apartment 风险。
- **MSAA 第二后端**：已接入，按窗口回退；Word 查找替换可读/可触发。
- **UWP**：`ApplicationFrameWindow` 已解析；REG-14 通过。
- **OCR**：已接入 Windows.Media.Ocr（`read_screen_text` / `click_text`）。
- **CDP 通道 / WGC**：未实现；需要 WebSocket/DOM 适配器、D3D11 staging readback。
- **多会话输入所有权**：`--profile` 已隔离互斥体/状态/artifact；同一时刻仍只有一个前台窗口。
