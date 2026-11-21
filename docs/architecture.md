# 架构说明

> 状态：第二轮重构 R2 进行中（以 `refactor/r2` HEAD 为准）。
> 数字口径：`python tools/audit/arch_metrics.py`；行数用 `ReadAllLines`/`splitlines()`，不含 `obj/`、`bin/`。
> 冻结清单：`docs/refactor-invariants.md`。第一轮验收：`docs/全局重构执行报告.md`。第二轮方案：`docs/第二轮全局重构方案.md`。

## 1. 程序集与依赖

```
Server (VScreenPilot) ──> Automation ──> Platform ──> Abstractions
        │                     │              │
        └─────────────────────┴──────────────┴──────> Abstractions
```

| 程序集 | 目标框架 | 职责 |
|---|---|---|
| `VScreenPilot.Abstractions` | `net10.0` | 错误模型（`ErrorCatalog`/`ToolException`/`ToolDetails`）、`JsonArgReader`、`VspJson`、`Views/*`、`BuildInfo`、`MetricsRegistry` |
| `VScreenPilot.Platform` | `net10.0-windows` | Win32/COM 原语、显示拓扑、VDD 设备、窗口枚举、输入注入/消息/键位/剪贴板、`JobObject`、`IntegrityCheck` |
| `VScreenPilot.Automation` | `net10.0-windows10.0.19041.0` | UIA/MSAA worker、屏幕捕获、PNG、OCR、模板匹配、脚本运行；**0 个 public 类型**，只经 IVT 暴露 |
| `VScreenPilot.Server`（`AssemblyName=VScreenPilot`） | `net10.0-windows10.0.19041.0` | MCP 传输与工具层、会话组合根、CLI、IPC、诊断、审计入口 |

`InternalsVisibleTo` 现状（也是 allowlist，架构测试锁定）：

| 来源 | 目标 |
|---|---|
| Abstractions | `VScreenPilot`、`VScreenPilot.Automation`、`Tests.Unit`、`Tests.Contract` |
| Platform | `VScreenPilot`、`VScreenPilot.Automation`、`VddProbe`、`Tests.Unit`、`Tests.Contract` |
| Automation | `VScreenPilot`、`Tests.Unit`、`Tests.Contract` |
| Server | `Tests.Unit`、`Tests.Contract` |

约束由 `tests/VScreenPilot.Tests.Contract/ArchitectureTests.cs` 与 `ArchitectureFitnessTests.cs` 断言：

1. 引用方向单向；`Automation` 不引用 Server/MCP SDK；`Platform` 只引用 Abstractions。
2. 一个命名空间只属于一个程序集；现有 5 个 SPLIT 先 allowlist，只允许减少。
3. IVT 必须在表内；新增即失败。
4. `Automation` 禁止 `System.Text.Json.Nodes`。

## 2. 目录与命名空间

命名空间与目录同名；跨程序集同名命名空间是待清理债务：

| 命名空间 | 归属 |
|---|---|
| `VScreenPilot.Session` | Server（元素 ref、应用跟踪、artifact/frame/session） |
| `VScreenPilot.Infrastructure` | Abstractions（`BuildInfo`、`MetricsRegistry`） |
| `VScreenPilot.Mcp` | Server（工具传输与目录；错误契约迁到 `Errors`，参数读取迁到 `Json`） |
| `VScreenPilot.Ui` | Server（`ElementRefStore`/文本格式化） |
| `VScreenPilot.Apps` | Server（`TrackedApp`/注册表） |

当前 SPLIT 命名空间 **0 个**；其余命名空间单归属：`VScreenPilot.Uia`、`Perception`、`Scripting`、`Platform.*`、`Views`、`Cli`、`Ipc`、`Tools`、`Input`、`Composition`、`Diagnostics`。

## 3. 启动、组合根与会话

- 入口：`Program.Main` 解析 `--version` / `--doctor` / `--cli` / `--daemon` / `--profile` 等，然后进入 `ServerHost`。
- 组合根：`SessionManager`（17 个 partial / 1,774 行）。它持有会话锁、显示控制器、应用注册表、元素 ref、UIA 宿主、写调度器与日志。
- 已知双 wiring：`Program` 与 `ServerHost` 各构造一次 `SessionManager`；R3 再合并。
- 组件通过 `I*Host` 反向依赖门面：18 个接口、187 个成员，最大 `IInputHost` 26。组件不做跨组件 `new`。
- 会话组件（Server/Session）：
  - 显示：`VirtualDisplayController`
  - 应用：`AppLauncher`、`AppReleaser`、`TrackedAppTracker`、`AppAllowlistPolicy`、`SessionStateKeeper`
  - 输入：`InputRouter`、`FocusGuard`
  - 元素：`ElementFinder`、`ElementResolver`、`ElementOperations`
  - 快照/环境：`SnapshotBuilder`、`EnvironmentReporter`、`EnvironmentReleaser`
  - 断言/等待/视觉：`RegionVerifier`、`WaitService`、`VisualMatchService`、`ScreenTextService`
  - 维护：`MaintenanceService`

## 4. 工具层与 MCP

- 22 个工具定义在 `src/VScreenPilot.Server/Tools/Definitions/*.json`，由 `ToolCatalog` 加载（113 行）；顺序按 `Ordinal`。
- `ToolDescriptor`/`ToolPolicy`/`ToolRegistry` 负责 schema、权限、超时与并发策略；`ToolHandlers` 负责参数解析与调用。
- `ToolExecutor` 统一处理写锁、屏幕租约、超时、取消与 epoch；`ToolContext` 仍持有 `SessionManager`。
- 错误统一走 `ToolException` + `ErrorCatalog`；错误包字段、错误码、`docs` 锚点是冻结契约。
- 内容块顺序固定：`text` → `structuredContent` → `image` → `resource_link`。
- 传输：官方 `ModelContextProtocol` SDK（stdio）；首个进程可 spawn 独立 daemon，后续进程经 `StdioProxy` 转发。

## 5. 并发模型

- **只读不排队**：`snapshot`、`capture_screen`、`find_element`、`get_ui_tree`、`wait_for`、`assert_region`、`match_visual`、`get_log`。
- **写操作串行**：`click`、`gesture`、`type_text`、`press_keys`、`invoke_element`、`set_element_value`、`launch_or_place_app`、`release_environment`、`run_script`；忙时 `E_BUSY` + `holder` + `retry_after_ms`。
- **超时不释放写锁**：handler 真正结束后才释放。
- **epoch**：`release_environment` 校验通过后递增；在途结果返回 `E_CANCELLED`。
- **每调用取消**：`AsyncLocal<CancellationTokenSource>`。
- **UIA 单 MTA worker**：`UiaHost` 串行执行服务级调用；MSAA 与句柄操作经 `InvokeMsaa`/`InvokeOn` 回到同一 worker。没有自动换代，卡死时 `ResetUia` 是唯一恢复入口。
- **多客户端**：每连接独立 `ClientContext`；`ScreenLease` 保证同一时刻只有一个客户端做 GUI/视觉操作。
- 限流 300 突发 / 100 每秒；写等待 30s，`release_environment` 6s；release 读锁 3s。

## 6. 感知与输入

- `ScreenCapture` 复用 screen DC / memory DC / DIB；`PngWriter` 用 `ArrayPool`，默认 `VSP_PNG_FILTER=up`。
- 帧缓存 8 帧 / 32MB；大图默认落 artifact，返回 `vsp://artifacts/...`。
- `channel=auto`：UIA pattern → 窗口消息（仅客户区）→ SendInput（需 `fallback_input` 或 `VSP_INPUT_AUTOFALLBACK=1`）。
- 非客户区在 message/auto 下返回 `E_INPUT_UNSUPPORTED`；`Win`、`Alt+Tab` 等系统键默认拒绝。
- 长文本（>500 字）走剪贴板并保存/还原。
- UWP：`ApplicationFrameWindow` 归属 frame host，内容进程只做 UIA 归属校验。

## 7. 测试与审计

| 闸门 | 内容 | 位置 |
|---|---|---|
| 单元 | 200 用例；视图序列化、策略、路径、拓扑、租约、组件 fake | `tests/VScreenPilot.Tests.Unit` |
| 契约 | 16 用例；schema 快照、架构方向、命名空间/IVT/棘轮 | `tests/VScreenPilot.Tests.Contract` |
| 架构度量 | `arch_metrics.py --selftest/--check`（文件/方法/宿主/SPLIT/catch/测试数） | `.github/workflows/ci.yml` |
| 基线 diff | `capture_baseline.py` + `compare_baseline.py --only server.json --only errors.json` | CI `smoke` |
| GUI / protocol | 13/14、30/30；需要 VDD | 自托管 nightly |
| stability / UIA | 14/14、3/3；`bench_stability.py`、`bench_uia_stress.py` | 自托管 nightly |

已知失败：`GUI-15`（应用被杀后按 exe 重定位到另一个同名窗口）保持既有语义。

## 8. 已知债务与 R3

- 6 个文件 >600：`VirtualDisplayController` 759、`SessionManager.cs` 746、`McpTransport` 721、`ToolHandlers` 659、`SnapshotBuilder` 645、`VisualMatchService` 609（R2-3 已拆 `InputRouter`/`AppLauncher`/`ElementOperations`）。
- 16 个方法 >150，最大 `AppLauncher.Launch` 542；27 个 ≥120（R2-4 处理中）。
- 无类型 `catch` 37 处（其中无日志 34 处）；83 处宽捕获只写 `Debug.WriteLine`。
- 18 个组件只有 5 个有直接单测；编排主要靠 Python 审计。
- SPLIT 命名空间已在 R2-2 清零；`ToolContext` 仍持有门面；`Program`/`ServerHost` 双 wiring。
- R2 目标：3 个组件拆分、16 个方法 ≤150、文件 ≤800、SPLIT ≤2、catch 收口；R3 再降到 ≤600 / ≤120 并清理剩余 partial。
