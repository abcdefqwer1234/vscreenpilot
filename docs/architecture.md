# 架构说明

> 状态：第二轮 R2 + 文件/方法级 R3 已完成；第三轮 T3 进行中。进度见 `docs/第二轮重构执行报告.md` 与 `docs/第三轮全局重构方案.md`。
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
2. 一个命名空间只属于一个程序集；当前 SPLIT 为 0，任何新增即失败。
3. IVT 必须在表内；新增即失败。
4. `Automation` 禁止 `System.Text.Json.Nodes`。

## 2. 目录与命名空间

命名空间与目录同名；跨程序集同名命名空间已清零（当前 SPLIT 0）。

| 命名空间 | 归属 |
|---|---|
| `VScreenPilot.Session` | Server（元素 ref、应用跟踪、artifact/frame/session） |
| `VScreenPilot.Infrastructure` | Abstractions（`BuildInfo`、`MetricsRegistry`） |
| `VScreenPilot.Mcp` | Server（工具传输与目录；错误契约迁到 `Errors`，参数读取迁到 `Json`） |
| `VScreenPilot.Ui` | Server（`ElementRefStore`/文本格式化） |
| `VScreenPilot.Apps` | Server（`TrackedApp`/注册表） |

当前 SPLIT 命名空间 **0 个**；其余命名空间单归属：`VScreenPilot.Uia`、`Perception`、`Scripting`、`Platform.*`、`Views`、`Cli`、`Ipc`、`Input`、`Composition`、`Diagnostics`。`Tools/` 目录当前仍声明为 `VScreenPilot.Mcp`，T3-3 拆出 `VScreenPilot.Tools`。

## 3. 启动、组合根与会话

- 入口：`Program.Main` 只委托 `Composition.Bootstrap.Run`；`Bootstrap` 解析 `--version` / `--doctor` / `--cli` / `--daemon` / `--profile` 等，然后进入 `ServerHost`。
- 组合根：`SessionManager`（18 个 partial 文件；类型跨度约 1,609 行）。它持有会话锁、显示控制器、应用注册表、元素 ref、UIA 宿主、写调度器与日志；`SessionFacade`/`SessionExecutionControl` 把它收窄为 IPC/CLI 与工具层边界。
- 已知双 wiring：`Program` 与 `ServerHost` 各构造一次 `SessionManager`；R3 未合并，T3-2 收敛为单一组合根。
- 组件通过 `I*Host` 反向依赖门面：18 个接口、187 个成员（唯一名 108），最大 `IInputHost` 26。组件不做跨组件 `new`；T3-2～T3-6 替换为端口。
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
- `ToolExecutor` 统一处理写锁、屏幕租约、超时、取消与 epoch；`ToolContext` 已改为 `IExecutionControl` + `ClientInfo`，不再持有 `SessionManager`。失败路径的屏幕租约在 `InvocationGate` 中回滚。
- 错误统一走 `ToolException` + `ErrorCatalog`；错误包字段、错误码、`docs` 锚点是冻结契约。
- 内容块顺序固定：`text` → `structuredContent` → `image` → `resource_link`。
- 传输：官方 `ModelContextProtocol` SDK（stdio）；首个进程可 spawn 独立 daemon，后续进程经 `StdioProxy` 转发。

## 5. 并发模型

- **只读不排队**：`snapshot`、`capture_screen`、`find_element`、`get_ui_tree`、`wait_for`、`assert_region`、`match_visual`、`get_log`。
- **写操作串行**：`click`、`gesture`、`type_text`、`press_keys`、`invoke_element`、`set_element_value`、`launch_or_place_app`、`release_environment`、`run_script`；忙时 `E_BUSY` + `holder` + `retry_after_ms`。
- **超时不释放写锁**：handler 真正结束后才释放。
- **epoch**：`release_environment` 校验通过后递增；在途结果返回 `E_CANCELLED`。
- **每调用取消**：`AsyncLocal<CancellationTokenSource>`。
- **UIA 单 MTA worker**：`UiaHost` 串行执行服务级调用；MSAA 与句柄操作经 `InvokeMsaa`/`InvokeOn` 回到同一 worker。调用超过 15s 会退役当前 generation 并重建（`WaitCompletionOrRetire`），`ResetUia` 仍可显式恢复。
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
| 单元 | 205 用例（176 测试方法）；视图序列化、策略、路径、拓扑、租约、组件 fake | `tests/VScreenPilot.Tests.Unit` |
| 契约 | 16 用例；schema 快照、架构方向、命名空间/IVT/棘轮 | `tests/VScreenPilot.Tests.Contract` |
| 架构度量 | `arch_metrics.py --selftest/--check`（fixtures 20 正向 + 3 反向；文件/方法/宿主/SPLIT/catch/测试数） | `.github/workflows/ci.yml` |
| 基线 diff | `capture_baseline.py` + `compare_baseline.py --only server.json --only errors.json` | CI `smoke` |
| GUI / protocol | 14/14、30/30；需要 VDD | 自托管 nightly |
| stability / UIA | 14/14、3/3；`bench_stability.py`、`bench_uia_stress.py` | 自托管 nightly |

`GUI-15` 当前只覆盖“零应用/单应用”场景：先 `release_environment` 再启动单个 UiTarget，验证“杀目标 → 报错 → 重启恢复”。尚未覆盖“指定 pid 被杀、其他应用仍存活”，该断言在 T3-8 补齐。

## 8. 已知债务（T3 输入）

- 178 个源文件、32,293 行；0 个文件 >600，最大 599（`MsaaService.cs`）；15 个类型 >400，最大 `UiaService` 类型跨度 1,869。
- 0 个方法 >150，最大 120；但仍有约 48 个 >80 行、32 个 >12 判断点、39 个 >6 参数。
- 无类型 `catch` 0；无日志静默 `catch` 0（`arch_metrics.py` 口径）。
- 18 个 `I*Host`、187 个成员声明、108 个唯一名；25 个有行为组件只有约 5 个有直接单测。
- SPLIT 命名空间 0；`ToolContext` 仍持有门面；`Program`（251 行）与 `ServerHost` 仍各构造一次 `SessionManager`；`partial` 声明 63 个 / 11 个类型。
- `Program` 254 行、`Main` 97 行；`SessionManager` 52 个 public 成员。
- T3 目标：类型 ≤400、文件 ≤450、方法 ≤80/12/6、partial 0、`I*Host` 0、`SessionManager` ≤350、`Program` ≤200，详见 `docs/第三轮全局重构方案.md`。

## 9. 架构度量（自动生成）

> 本节由 `python tools/audit/doc_metrics.py --write` 生成；CI 用 `--check` 校验，禁止手改。

<!-- arch-metrics:begin -->
| 指标 | 实测 | 硬上限 |
|---|---:|---:|
| 源文件 / 行数 | 190 / 32448 | - |
| 最大文件 | 599 | ≤450 |
| 类型 >400 | 16 | 0 |
| 最大类型跨度 | 1881 | ≤400 |
| partial 类型 | 11 | 0 |
| 方法 >80 行 | 49（最大 120） | 0 / ≤80 |
| 方法 >12 判断点 | 46（最大 34） | 0 / ≤12 |
| 方法 >6 参数 | 54（最大 12） | 0 / ≤6 |
| I*Host | 10 / 156 成员 | 0 |
| SessionManager | 1531 行 / 41 public | ≤350 / ≤12 |
| Program | 9 行 / Main 1 | ≤200 / ≤80 |
| 测试方法 | 180 | ≥230 |
| 无日志 catch | 66 | ≤66 |
| SPLIT 命名空间 | 0 | 0 |
| public 类型 | Abstractions=55, Automation=0, Platform=6, Server=9 | 不增加 |
<!-- arch-metrics:end -->
