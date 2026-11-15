# VScreenPilot 静态审查报告（PPT 轮）

> 审查对象：`C:\Users\31746\Desktop\VScreenPilot`（重构后，20 工具，0.1.0）
> 审查日期：2026-11-15
> 审查方式：全量源码阅读（`src/` 16,506 行，195 个方法）+ 文档交叉核对 + MCP 2025-11-25 规范对照 + 同类项目源码/文档对照 + 本机构建与运行期探测
> 基线二进制：`artifacts/server7/VScreenPilot.exe`
> SHA256：`A90D33A19F74B0A61F6E5924EBBF20CD45CA0A9FE865EA41C7CA07267F7F3EB0`
> 基线提交：`2ef5334 test: 补齐审计套件与重构文档，修复 UWP/MSAA/调度残余`
> 关联文档：[08-实际测试计划-PPT](08-实际测试计划-PPT.md)、[03-Word 轮报告](03-实际测试报告-Word.md)
> 说明：本文是**测试前**的静态结论；实际测试（§10 的待验证项）完成后，结论与评分会在 09 报告中复核。

---

## 0. 结论

**一句话**：协议层与运行期工程质量已经接近“成熟 MCP 项目”；拖后腿的是**感知广度、动作可验证性、分发与生态、以及少数会误导调用方的语义缺陷**。对 OpenCode 调试 Windows 应用而言，属于“Office/Win32/UWP 中等任务可用，画布型应用（PPT）需视觉兜底、暂不可依赖”。

- 成熟度评分：**64 / 100**（新口径，与 01 报告的 65 分不可直接比较；权重见 §9）。
- 阻断级缺陷：**0 个**（无“完全跑不起来”的问题）。
- P1（会误导调用方或造成数据/并发风险）：**8 项**，其中 1 项已在环境检查中实测复现（S1 多 profile 挂载）。
- P2：**10 项**；P3：**2 项**。
- 最值得肯定的设计：虚拟屏隔离 + 焦点/光标护栏 + 写操作调度 + 快照代际 ref + 统一错误目录。这五点在同 star 量级的项目里没有等价物。
- 最该补的三件事：**画布/Web 的第二感知通道（OCR/CDP/带标注截图）**、**动作后置验证**、**可发布的分发链路**。

---

## 1. 审查范围与方法

### 1.1 覆盖范围

| 范围 | 内容 |
| --- | --- |
| 源码 | `src/VScreenPilot.Core`（VDD/拓扑/PInvoke）、`src/VScreenPilot.Server`（MCP、Session、UIA、MSAA、输入、感知、基础设施） |
| 工具与脚本 | `tools/audit/*.py`、`tools/TestTool`、`tools/AotSmokeTest`、`tools/VddProbe` |
| 打包 | `npm/`、`packaging/winget/`、`scripts/`、`Directory.Build.props`、`global.json`、`.github/workflows/ci.yml` |
| 文档 | `README.md`、`docs/*.md`、`docs/audit/00–06`、`需求文件.md`、`技术文档.md` |
| 运行期探测 | `--doctor`、`--version`、stdio 握手、`tools/list`、`get_environment`/`release_environment`、多 profile 挂载 |

### 1.2 方法

1. 逐文件阅读并标注证据（文件:行）。
2. 用脚本统计错误码使用率（`ErrorCatalog` 中 53 条 vs 实际抛出）。
3. 对照 MCP 2025-11-25 规范（tools/annotations/outputSchema/resource/transport 变更点）。
4. 对照同类项目**源码**（不是只看 README）：Windows-MCP 的 `control_policy.py`/`snapshot.py`、agent-desktop 的 ref/stale 语义、playwright-mcp 的 CLI-vs-MCP 论述。
5. 在本机做最小运行期探测（不进入 PPT 场景），复现了 1 个静态推断的缺陷（S1）。

### 1.3 本轮已执行的命令与结果

| 检查 | 结果 |
| --- | --- |
| `dotnet build VScreenPilot.slnx -c Release` | 0 警告 / 0 错误 |
| `dotnet test`（31 项） | 31/31 通过 |
| 同 commit 连续两次 `dotnet publish` | 哈希一致 `A90D33…`（可复现） |
| `VScreenPilot.exe --doctor` | `ok:true`，驱动 0.41，主屏 1920×1440@168dpi，`run_script_enabled:false` |
| OpenCode 内 `get_environment(ensure=true)` | 成功挂载 `\\.\DISPLAY9 1280×720@60 @ (1920,0) dpi=96`，耗时约 3.5s |
| OpenCode 内 `release_environment(keep_display=false,on_dirty=force)` | `display_removed:true` |
| 第二实例（同 profile）启动 | 立即退出，stderr 提示互斥体被占用（退出码 3） |
| 非默认 profile 挂载 | **失败** `E_DISPLAY_MODE`，5.70s，见 S1；`VSP_DISPLAY_OFFSET=0` 时成功（0.46s） |
| 协议协商 | 请求 2025-11-25 → 协商为 2025-11-25；请求 2025-06-18 → 回退为 2025-06-18 |

> 注意：同 commit 可复现；**跨 commit 哈希不同**（SDK 默认把 source revision 写进 informational version），所以“代码没改”不能推断哈希不变。所有报告必须绑定哈希。

---

## 2. 工程事实

| 项 | 数值 |
| --- | --- |
| 源码规模 | 16,506 行 / 195 个方法（C#） |
| 最大文件 | `UiaService.cs` 1,602 行、`SessionManager.cs` 1,577 行、`McpServer.cs` 1,493 行 |
| 工具面 | 20 个，均带 `title`、`outputSchema`、`annotations` |
| 错误码 | 53 条（5 条从未抛出，见 S9） |
| 单元测试 | 31 项 / 4 个文件；核心 `SessionManager` 无单测 |
| 外部审计脚本 | 17 个 Python 文件（协议/契约/并发/截图/GUI/回归/对话框/soak/SLO） |
| 依赖 | `ModelContextProtocol 2.4.0`、`Interop.UIAutomationClient 10.19041.0`、`Microsoft.Extensions.Hosting 10.0.14` |
| 产物 | 单文件 self-contained R2R，100,158,798 字节，未签名 |
| 仓库 | 无 remote、无 Release、tag 仅 `v0.1.0`、npm 未发布 |
| CI | 1 个 workflow，3 个 job（unit / smoke / nightly-self-hosted） |

---

## 3. 架构与并发

### 3.1 分层（成立）

`MCP 传输（SDK）→ 工具层（校验/调度/追踪）→ 会话层（虚拟屏/接管/快照/输入/断言）→ 感知层（GDI/PNG/模板）→ 平台层（Win32/COM/显示）`。职责边界清楚，工具层没有泄漏 Win32 细节。

### 3.2 并发模型（强）

- 只读观察不排写锁；`snapshot`/`capture`/`find`/`tree`/`wait`/`assert`/`match`/`get_log` 均不进 `WriteScheduler`。
- 写操作串行，`E_BUSY` 带 `holder` 与 `retry_after_ms`。
- `release_environment` 等 6s 写租约；只有锁已持有 >5s 才旁路（`WriteScheduler.cs:36-42`）。
- 会话代际 epoch：release 先推进，旧 handler 结果作废（`SessionManager.cs:999,483-492`）。
- 快照串行门 `_snapshotGate` 带 5s 超时（`SessionManager.Snapshot.cs:31-40`）。

### 3.3 锁层次（弱）

存在四层互斥：`_refGate`、`_gate`、`_frameLock`、`_sessionLock`（读写租约），再加上 `_snapshotGate`、`WriteScheduler`。顺序只靠注释约定（`SessionManager.cs:1125` 明确提醒 `_refGate → _gate`），没有断言、没有超时、没有死锁检测。当前代码未发现反序获取，但这是**回归高危区**：任何新代码在 `_gate` 内取 `_refGate` 都会与 ref 路径互锁。

### 3.4 生命周期（强）

互斥体、`state.<profile>.json`、Job Object（`KillOnJobClose`，如实上报 `assign_failed`）、孤儿屏接管、VDD 心跳重开、Ctrl+C/SIGTERM/stdin EOF/父进程退出统一清理。`ReleaseEnvironment` 先推进 epoch 再清理，避免半清理状态被读取。

---

## 4. MCP 协议与工具契约

### 4.1 做得对的部分

| 项 | 证据 |
| --- | --- |
| 官方 SDK 2.4.0；请求 2025-11-25 时协商成功（旧客户端请求 2025-06-18 则回退到该版本） | `McpServer.cs:75-94`；实测握手 |
| `serverInfo` + `instructions` | `McpServer.cs:78-83,109-117` |
| 20 个工具均带 `title`/`inputSchema`/`outputSchema`/`annotations` | `McpServer.cs:120-145` |
| 递归参数校验：type/required/enum/const/min/max/items/oneOf/additionalProperties | `ArgumentValidator.cs:13-303` |
| 未知字段、拼错枚举均报 `E_INVALID_ARG`（不再静默用默认值） | `ArgumentValidator.cs:106-147` |
| 输入校验错误以**工具错误**返回，符合 SEP-1303 | `McpServer.cs:326-335` |
| 错误统一 `code/message/details/retryable/hint/stage/docs`，目录化 | `ToolResponse.cs:68-104`、`ErrorCatalog.cs` |
| `request_id` 同时写入 text 与 structuredContent | `ToolResponse.cs:120-138` |
| 资源分页 + `size` + preview | `McpServer.cs:151-256` |
| 限流、未知工具/校验失败也进 trace | `McpServer.cs:281-335` |
| `outputSchema` 非空泛（逐字段声明，允许 null） | `McpServer.cs:1053-1096` |

这部分已经超过多数“能跑就行”的 MCP 服务器，接近 playwright-mcp 的契约严谨度。

### 4.2 契约缺陷

**S2（P1）`readOnlyHint` 与真实副作用不符。**
- `get_environment` 标注 `readOnly:true`，但 `ensure=true` 会**挂载显示器**（`McpServer.cs:712-720` + `SessionManager.cs:363-564`）。
- `snapshot` 标注 `readOnly:true`，但会**推进 ref 代际**并可能**写 artifact 文件**（`SessionManager.Snapshot.cs:99-103,252-263`）。
- `capture_screen` 标注 `readOnly:true`，但会写帧缓存/落盘（`SessionManager.Capture.cs:49-51,80-111`）。
- 影响：客户端按 `readOnlyHint` 做自动批准、批量并发或只读代理时，会得到与标注相反的行为；也让“只读不排写锁”的并发假设更难解释。
- 建议：把“会挂屏/换代/写盘”的工具标为非只读；或者新增独立工具（`ensure_display`）承载副作用，`get_environment` 纯读。

**S14（P2）传输与协议新特性缺失。**
- 仅 stdio（`McpServer.cs:90`）；无 SSE / Streamable HTTP。对比 Windows-MCP 支持 `--transport sse|streamable-http`。
- 未使用 2025-11-25 的 `tasks`（长任务轮询）、`elicitation`（结构化提问）、`icons`、`resource_link` 内容块。
- 影响：多客户端/远程不可用；长任务只能靠 progress 通知与客户端超时；大图只能以自定义 `vsp://` URI 字符串暴露，而不是标准 `resource_link`。

**S9（P2）错误码目录有“死码”和失效锚点。**
- 53 条中 5 条从未抛出：`E_DISPLAY_IN_USE`、`E_ENCODING`、`E_DIALOG_UNREACHABLE`、`E_SNAPSHOT_LIMIT`、`E_NOT_SUPPORTED`。
- 每条错误都带 `docs: docs/errors.md#e_xxx`，但 `errors.md` 只有分组表、没有 `### E_XXX` 标题，锚点不存在。
- `E_DIALOG_UNREACHABLE` 的 hint 指向“app profile 的按键序列”，代码里**没有 app profile 机制**（全仓 grep 无命中）。
- 影响：客户端拿到的“可操作指引”部分是虚构的；错误目录作为“唯一事实来源”的承诺打折。

**S15（P2）动作结果验证不均衡。**
- `click` 恒返回 `verified:false`（`SessionManager.Input.cs:307`），只给一句“请用 wait_for/assert_region 验证”。
- `press_keys` 无任何回读。
- `click_ref` 有 `observed{present,enabled,focused}`（`SessionManager.Input.cs:131-160`），`type_text` 有回读。
- 影响：模型每次按键后必须再截一张图确认，token 与往返成本翻倍；对 PPT 这类“菜单/功能区”操作尤其伤。

---

## 5. 感知后端

### 5.1 现状

| 通道 | 状态 | 证据 |
| --- | --- | --- |
| UIA（缓存树、裁剪、时序预算 3s） | 可用 | `UiaService.cs:1269-1508` |
| MSAA 第二后端（Office 旧对话框） | 可用，ordinal ref、2s 超时、COM 释放 | `MsaaService.cs` |
| GDI BitBlt 截图（资源复用 + 预览同帧） | 可用 | `ScreenCapture.cs:111-153` |
| 模板/颜色/单点匹配 | 可用，多尺度 | `TemplateMatcher.cs` |
| OCR | **无** | 全仓 grep 无 `Windows.Media.Ocr` |
| CDP / IAccessible2（WebView/CefSharp） | **无** | 全仓 grep 无 `Cdp`/`DevTools` |
| DXGI / Windows.Graphics.Capture | **无** | 全仓 grep 无 `DXGI`/`DwmFlush` |
| 带元素框/序号的标注截图 | **无** | `snapshot` 只有 tree + 原图 |

### 5.2 关键缺陷

**S4（P1）默认裁剪会裁掉非交互文本/画布节点。**
`InteractiveControlTypes` 只含 Button/Edit/ListItem/MenuItem/Document 等 18 类（`UiaService.cs:225-245`），**不含 `Text(50020)`、`Custom(50025)`、`Image(50006)`、`Pane(50033)`**。`interactive_only=true`（默认）时，无子节点且不可聚焦的节点会被剪掉（`UiaService.cs:1475-1486,1510-1514`）。
对 PowerPoint：幻灯片画布上的文本框/形状正是 `Text`/`Custom`/`Pane`，很可能在 `snapshot` 里完全消失——模型只能看到功能区，看不到内容。这正是本轮 PPT 场景要验证的第一风险点。

**S21（观察）`snapshot` 时间预算固定 3s，且没有“按窗口类型”策略。**
Office 的 UIA 树很大（功能区 + 状态栏 + 任务窗格），3s 预算下容易以 `time_budget` 截断；`max_nodes` 默认 300 会先到上限。两者都会把“内容区域”挤掉，而功能区控件排在前面。

---

## 6. 输入通道与护栏

### 6.1 做得对的部分

- `channel=auto` 的优先级：UIA pattern → 窗口消息 → 显式 `input`（`SessionManager.Input.cs:262-281`）。
- SendInput 绝对坐标带 `MOUSEEVENTF_VIRTUALDESK`（`InputInjector.cs:271-286`），DPI 感知、光标复位。
- 消息点击使用 `WM_LBUTTONDBLCLK` 生成多击序列（`MessageInput.cs:54-60`），比“连发两次 DOWN/UP”可靠。
- 非客户区在 message/auto 下明确拒绝，不返回假成功（`SessionManager.Input.cs:1249-1263`）。
- 输入前强制前台 + 焦点元素必须落在前台窗口（`SessionManager.Input.cs:366-388`）。
- 长文本（≥200 字）走剪贴板，短文本走 Unicode 注入（`SessionManager.Input.cs:390-456`）。
- 系统键按语义拦截 `Win`/`Alt+Tab`/`Alt+Esc` 变体（`KeyMap.cs:151-180`）。
- 焦点护栏 200ms 巡检、注入期间暂停；光标护栏 150ms 巡检、注入期间暂停。

### 6.2 关键缺陷

**S5（P1）超时不取消 handler。**
`Task.Run(..., CancellationToken.None)`（`McpServer.cs:390-454`），`WaitAsync(timeout)` 超时后返回 `E_TIMEOUT`，但 handler 仍在操作 UI；写锁靠 `ReleaseWhen(handlerTask, lease)` 兜底（`:469`）。`callCts` 在方法结束时释放，handler 后续读 `Cancellation` 会拿到已释放 CTS（有 `ObjectDisposedException` 兜底，`SessionManager.cs:167-174`），但**没有真正中止**。
影响：一次卡住的 UIA 调用后，模型已收到“超时”，屏幕上却可能继续出现点击/输入；若随后 `release_environment`，epoch 会让结果作废，但已发生的副作用无法撤销。

**S6（P1）release 默认 `on_dirty=force` 与文档承诺矛盾。**
Schema 与描述写“Default force”，同时描述写“Never auto-discards when the result is unknown”（`McpServer.cs:997-1005`）。实现上 `force` 分支只发 `WM_CLOSE`，等 2.5s 后直接 `KillProcessTreeIfMatches`（`SessionManager.cs:1258-1277`），不处理“保存/不保存”对话框。
影响：PPT/Word 有未保存内容时，默认 `release_environment` 会静默丢数据。对本轮 PPT 场景是**必须规避**的操作。

**S7（P1）剪贴板只保存/还原文本。**
`Capture()` 只读 `CF_UNICODETEXT`（`ClipboardText.cs:53-56,58-94`），`Restore(null)` 会写入空字符串（`:56`）。长文本输入路径（`WriteTextViaClipboard`）会先 Capture 再 Restore。
影响：用户剪贴板里若是图片/文件列表，会被清空且无法恢复。属于“隔离层不应破坏用户环境”的违反。

**S22（观察）双击进入文本编辑依赖应用自行处理 `WM_LBUTTONDBLCLK`。**
PowerPoint 画布的文本编辑需要双击命中形状；消息通道已发 DBLCLK，但 PPT 是否按 lParam 命中（而不是当前光标位置）未知。这是 PPT 场景的第二个待验证点。

---

## 7. 应用兼容

| 应用类型 | 支持度 | 依据 |
| --- | --- | --- |
| Win32/WinForms/WPF | 好 | UIA + 消息 + 输入三通道 |
| Word（含旧对话框） | 好 | Word 文档检测、`bosa_sdm_msword` MSAA、保存对话框处理 |
| UWP | 较好 | `ApplicationFrameWindow` + 内容进程跟踪 + 拒绝杀 frame host |
| Chromium/Electron/CefSharp | **差** | 消息注入被主动拒绝，只有 `channel=input`；无 CDP |
| PowerPoint（画布型） | **未知/偏弱** | 无画布语义、无 OCR、无标注截图；Office 适配未建模 PPT |
| 自绘/游戏/远程桌面 | **差** | 只有视觉（GDI）+ 输入，无 DXGI |

**S3（P1）Office 适配只建模了 Word。**
`SessionManager.Apps.cs:356-388`：`bool word = exeName.Equals("winword")`，其余应用 `documentReady = true`。PowerPoint 停在“开始屏幕/登录/激活/隐私提示”时也会被报告为“文档就绪”。同时没有对 Office 首次运行弹窗、许可证提示、受保护视图的专门处理。
影响：本轮 PPT 场景第一步就可能被误导；模型会直接 `snapshot` 一个没有演示文稿的窗口。

**S8（P1）多 profile 无输入所有权仲裁。**
`--profile` 隔离了互斥体/状态/日志/artifact，但同一时刻只有一个前台窗口（`docs/architecture.md` §6 已承认）。两个 profile 并行时，`EnsureForeground` 会互相抢前台，输入可能落到错误的会话。工具没有任何“前台租约/所有权”提示。

---

## 8. 安全、隐私、可观测性

### 8.1 优点

- 应用白名单 `VSP_APP_ALLOWLIST`，启动/接管/内容进程三条路径都校验（`SessionManager.cs:1745`、`Apps.cs:398-406`）。
- 系统进程 denylist，UWP 只杀内容进程（`SessionManager.cs:1516-1535`）。
- 截图落盘限制在 cwd/temp，解析 junction，`FileMode.CreateNew` 不覆盖（`SessionManager.Capture.cs:232-338`）。
- `run_script` 默认关闭，Job Object 回收，输出 64KB 截断。
- `get_environment`/`snapshot` 只对本会话窗口返回标题；日志去标题。
- trace 参数摘要脱敏 `text/value/code/args/password/window/path/save_path/target_path`（`McpServer.cs:585-600`）。
- `--doctor` 输出驱动/拓扑/进程/检查项，可用于排障。

### 8.2 缺陷

**S13（P2）`run_script` 黑名单是“安全剧场”。**
`UnsafeScriptTokens` 只匹配字面量（`SessionManager.Script.cs:163-197`）：`iwr`/`curl`/`&`/字符串拼接/`iex` 都能绕过；`Add-Type` 被禁但 `[Reflection.Assembly]::Load` 变体可绕。文档已说明“不是沙箱”，但工具目录仍把它当安全边界呈现，容易让调用方误判。

**S16（P2）可观测性只有本地文件。**
trace 是 JSONL（`%LOCALAPPDATA%\VScreenPilot\sessions\*.jsonl`），日志按天轮转；没有 metrics、没有 OpenTelemetry、没有健康端点、没有“动作→屏幕变化”的关联指标。对比 computer-use-linux 的 `doctor` 工具 + 就绪检查 + 单次往返验证，差距在“可运维性”。

**S19（P3）`--doctor` 不检查 Office/PPT 与 artifact 目录。**
对本轮场景，PPT 是否存在、是否可写 `%LOCALAPPDATA%\VScreenPilot` 都只能靠运行时失败发现。

---

## 9. 测试工程、分发、文档

### 9.1 测试工程（S12，P2）

- 31 个单测覆盖：参数校验、错误目录元数据、写调度、profile、KeyMap、快照格式、PNG 往返/缩放/CRC。
- **未覆盖**：`SessionManager`（约 7,000 行）、`UiaService`、`InputInjector`、`MessageInput`、`MsaaService`、`ScreenCapture`。
- 外部 Python 套件很强（协议/契约/并发/截图/GUI/回归/对话框/soak/SLO，全部绑定 `binary_sha256`），但**依赖真机 + 虚拟屏**，只能在 self-hosted nightly 跑。
- CI 没有覆盖率门槛、没有 schema 快照测试（工具 schema 变更不会被单测发现）、没有 `dotnet format --verify-no-changes`、分析器级别是 `latest-minimum`。

### 9.2 分发与生态（S11，P2）

| 渠道 | 状态 |
| --- | --- |
| GitHub Release | 无（无 remote、无 release 产物） |
| npm | 未发布；README 自述“尚未 publish” |
| winget | 清单存在但 `InstallerUrl`/`InstallerSha256` 是占位符（`packaging/winget/VScreenPilot.VScreenPilot.yaml:16-17`），仓库地址 `github.com/vscreenpilot/vscreenpilot` 不存在 |
| MCP Registry | 无 `server.json`（`changes-2026-11.md` 称“清单已就绪”，与实际不符） |
| 代码签名 | 无 |
| 产物体积 | 100MB 单文件（self-contained + R2R） |

对比：Windows-MCP 已上 PyPI（`uvx windows-mcp`）、MCP Registry、Claude 目录，并支持 3 种传输；playwright-mcp 是 `npx @playwright/mcp@latest` 一行接入。

### 9.3 文档（S10/S17，P2）

- `技术文档.md`（2,200+ 行）仍描述“手写 JSON-RPC”“18 个工具”（`:110,:819,:940`）；`需求文件.md` 同样写 18 工具、手写实现（`:92,:164,:229`）。这与当前实现（官方 SDK、20 工具）冲突，而 README 仍把它当“实现细节”入口。
- `README.md:10` 仍写“当前 run5：GUI 14/14…”，实际已到 run6。
- `docs/changes-2026-11.md:56` 称“winget/Registry 清单已就绪”，实际是占位符 + 无 `server.json`。
- `changes` 写“release 3s”，实现是 6s；写“全屏 P95 ≈53ms”，run6 数据是 58ms。
- 无英文 README、无 `CONTRIBUTING.md`/`SECURITY.md`/`CHANGELOG.md`；`LICENSE` 是 MIT，作者写 “VScreenPilot contributors”。

文档质量本身不差（分层、并发、错误目录、审计链都写清楚了），问题是**主文档与实现脱节**——对“成熟项目”而言，这比缺文档更伤。

---

## 10. 性能（静态评估 + 已知数据）

| 项 | 数据 | 评价 |
| --- | --- | --- |
| 区域截图 | P50 17ms / P95 25ms（run6） | 好 |
| 全屏 1280×720 | P50 43ms / P95 58ms | 好（GDI 上限内） |
| 首次挂屏 | 约 3.5s（本次实测，含 ADD + 模式设置） | 可接受，但与“进程启动即预热”配合才行 |
| `snapshot` 预算 | 3s 时间预算 + 300 节点默认 | 对 Office 偏紧，可能截断内容区 |
| 服务端 RSS | 196MB 峰值（run6） | 偏高（单文件 R2R + UIA 缓存），可接受 |
| 工具往返 | 每次调用一次 JSON-RPC | 正常；缺少批处理/脚本化接口，多步任务往返多 |

对比 peers：Windows-MCP 用 `dxcam`（DXGI）做截图、单次 0.2–0.5s 动作间隔；VScreenPilot 的 GDI 在同一量级，但没有“脏矩形/增量”能力。

---

## 11. 问题清单

> 编号 S1–S22，供 09 报告与修复跟踪引用。等级：P1 会误导/造成风险，P2 影响成熟度，P3 体验细节。

### P1

| ID | 问题 | 证据 | 影响 | 建议 |
| --- | --- | --- | --- | --- |
| S1 | **非默认 profile 挂载失败** `E_DISPLAY_MODE` | 实测 profile=envcheck3：5.77s，`set=0 apply=0 current=DISPLAY9 @(1920,0)`；`SessionManager.cs:748-785`、`DisplayTopology.cs:160-193` | `--profile` 的“多会话隔离”在默认偏移下不可用；并行测试被迫串行 | 激活后**再调用一次** `ChangeDisplaySettingsEx` 设置位置；或在 `settled` 失败时用 `ExtendDesktop` + 重设；或接受“实际位置”并记录 |
| S2 | `readOnlyHint` 与副作用不符 | `McpServer.cs:134-140,712-720,740-758`；`Snapshot.cs:99-103,252-263` | 客户端自动批准/并发假设错误；审计口径失真 | 挂屏/换代/写盘的工具改为非只读，或拆出 `ensure_display` |
| S3 | Office 只建模 Word | `SessionManager.Apps.cs:356-388` | PPT 停在开始屏幕也报 `document_ready:true`；无登录/隐私弹窗处理 | 增加 `powerpnt`/`excel` 的“有演示文稿/工作簿”检测；启动后检查 owned 对话框 |
| S4 | `interactive_only` 裁掉 Text/Custom/Image | `UiaService.cs:225-245,1475-1486,1510-1514` | 画布型应用语义树为空，模型被迫转视觉 | 增加“内容节点”白名单；或对 Office 默认 `interactive_only=false` |
| S5 | 超时不取消 handler | `McpServer.cs:390-481` | 超时后仍可能点击/输入；幽灵副作用 | handler 接受 CancellationToken 并在关键步骤检查；或超时后进入“隔离态”拒绝新写操作直到结束 |
| S6 | release 默认 `force` 会丢未保存数据 | `McpServer.cs:997-1005`；`SessionManager.cs:1258-1277` | 默认调用即可杀进程，违背“结果未知不丢弃” | 默认改 `abort`；或 `force` 前必须显式 `allow_data_loss=true` |
| S7 | 剪贴板只还原文本 | `ClipboardText.cs:53-56,58-94` | 用户剪贴板图片/文件被清空 | 用 `OleGetClipboard`/`OleSetClipboard` 保存全部格式，或至少检测非文本时拒绝走剪贴板通道 |
| S8 | 多 profile 无输入所有权 | `architecture.md` §6；`Focus.cs` | 并行会话互相抢前台，输入可能错投 | 前台租约（`take_foreground_lease`）+ 冲突时 `E_FOCUS_OWNED` |

### P2

| ID | 问题 | 证据 | 建议 |
| --- | --- | --- | --- |
| S9 | 5 条死错误码；docs 锚点不存在；`E_DIALOG_UNREACHABLE` 指向不存在的 app profile | `ErrorCatalog.cs`；`docs/errors.md` 无 `###` | 删除或实现；给每条错误加锚点；hint 改成真实能力 |
| S10 | 主技术文档与实现冲突（18 工具/手写 JSON-RPC） | `技术文档.md:110,819,940`；`需求文件.md:92,164,229` | 重写为“重构后实现”，或降级为历史文档 |
| S11 | 分发声明不实（winget 占位、无 Registry、无 Release/npm） | `packaging/winget/*.yaml:16-17`；无 remote；`npm/README.md` | 先打通 GitHub Release + 签名，再改清单 |
| S12 | 单测只覆盖外围；CI 无覆盖率/schema 快照 | `tests/*.cs`；`ci.yml` | 给 `SessionManager`/`UiaService` 抽接口做单测；加 schema golden test |
| S13 | `run_script` 黑名单可绕过 | `SessionManager.Script.cs:163-197` | 明确标注“非安全边界”，或移除黑名单只留开关 |
| S14 | 无 HTTP/SSE、无 tasks/elicitation/icons/resource_link | `McpServer.cs:84-94` | 至少加 streamable HTTP；大图用 `resource_link` |
| S15 | 动作后置验证不均衡 | `SessionManager.Input.cs:307` | `click`/`press_keys` 返回 `observed`（前后截图 diff 或焦点/标题变化） |
| S16 | 只有本地 trace/日志，无 metrics | `TraceLog.cs`；`LogRing.cs` | 加 `/metrics` 或 OTel exporter；trace 增加 `effect` 字段 |
| S17 | 文档口径漂移（run5、3s、53ms、Registry） | `README.md:10`；`changes-2026-11.md:30,51,56` | 统一由测试数据生成摘要 |
| S18 | 无英文 README/治理文件；npm README 未跟上 20 工具 | 根目录清单 | 补 `README.en.md`、`CONTRIBUTING`、`SECURITY`、`CHANGELOG` |

### P3

| ID | 问题 | 证据 | 建议 |
| --- | --- | --- | --- |
| S19 | `--doctor` 不检查 Office/artifact 可写性 | `Program.cs:66-68` | 加 `office.powerpoint`、`artifacts.writable`、`display.free_slot` |
| S20 | 构建哈希随 commit 变化，易被误读为“代码变了” | 实测 server6/server7 | 报告与发布说明统一写 commit + hash |

### 待实测确认（不预设结论）

- S4 在 PowerPoint 上的实际影响（画布节点是否真的被裁掉）。
- S22 双击进入文本编辑是否成功。
- S1 的修复是否只影响“位置”而不影响“挂载可用性”。
- 长文本输入在 PPT 占位符里的回读准确率。

---

## 12. 与高 star 同类项目的对照

数据取数：2026-11-15，GitHub API。

| 维度 | VScreenPilot | Windows-MCP (11.7k) | agent-desktop (23.8k) | playwright-mcp (39.7k) | terminator (1.7k) |
| --- | --- | --- | --- | --- | --- |
| 感知 | UIA + MSAA + GDI | UIA + DOM(IAccessible2) + DXGI 截图 | 可访问性树（跨平台） | 浏览器 a11y 树 | 像素 + DOM + a11y |
| 隔离 | **虚拟屏 + 护栏**（独有） | 直接操作用户桌面 | 直接操作，默认不抢焦点 | 独立浏览器 | 直接操作，不抢光标 |
| ref 语义 | 快照代际 + 上一代保留 | 坐标为主，无 ref | snapshot 级 + `STALE_REF`/`AMBIGUOUS_TARGET` | 页面快照 + ref | 元素 id |
| 验证 | 局部（click_ref/type_text） | 依赖下一次 snapshot | 每次动作后要求重新 snapshot | 自动等待 | 有 OCR/像素校验 |
| 治理 | allowlist/限流/denylist | **control policy（strict/balanced/relaxed）+ ownership lock + ControlStatus** | 权限引导 | 无 | 无 |
| 传输 | stdio | **stdio/SSE/HTTP** | CLI | stdio | stdio |
| 分发 | 无（本地 exe） | **PyPI/uvx/Registry/Claude 目录** | **npm** | **npm** | crates.io/npx |
| 测试 | 17 个外部脚本 + 31 单测 | 15+ pytest 模块（按工具） | 单测 + CI | Playwright 测试套件 | 有限 |
| 文档 | 中文为主，主文档滞后 | 英文，完整 | 英文，完整 | 英文，完整 | 英文 |

**结论**：VScreenPilot 在“隔离性”和“MCP 契约严谨度”上**强于**同 star 量级项目；在“感知广度、分发、治理、英文生态”上**弱于** Windows-MCP 与 agent-desktop。Windows-MCP 的 `control_policy` 与 `snapshot` 的 `use_annotation/use_vision/region` 是最值得抄的两处；agent-desktop 的 `STALE_REF`/`AMBIGUOUS_TARGET` 与“skeleton → root 下钻”验证了 VScreenPilot 的 ref 代际方向，也提示还需要“分层下钻”。

**一个必须正面回答的问题**：playwright-mcp 的 README 明确写“编码代理越来越偏向 CLI+SKILLS，因为 MCP 的工具 schema 会长期占用上下文”。对本项目，20 个工具、单个 `snapshot` 可能上千 token 的返回，确实是成本项。建议在 09 报告里给出“CLI 伴生入口”的方案（同一 exe 支持 `vscreenpilot snapshot --json`），而不是只做 MCP。

---

## 13. 成熟度评分（新口径）

| 维度 | 权重 | 得分 | 依据 |
| --- | --- | --- | --- |
| MCP 协议与工具契约 | 15 | 12.5 | SDK/annotations/outputSchema/递归校验/错误目录；扣 readOnlyHint、死码、缺 tasks/HTTP |
| 语义感知广度 | 15 | 7.0 | UIA+MSAA 扎实；无 OCR/CDP/标注截图；画布型应用风险 |
| 输入保真与兼容 | 15 | 9.0 | 三通道 + DPI + 前台校验；缺后置验证、PPT 未验证、剪贴板只还原文本 |
| 隔离与安全 | 10 | 9.0 | 虚拟屏隔离独有；扣多 profile 抢前台、默认 force、黑名单剧场 |
| 可靠性与生命周期 | 10 | 7.5 | Job/Mutex/epoch/自愈/快照门；扣超时不取消、profile 挂载 bug、锁顺序靠约定 |
| 可观测性 | 8 | 6.0 | trace/request_id/doctor/get_log；扣无 metrics/OTel、无动作效果关联 |
| 测试工程 | 10 | 5.0 | 外部套件强；扣核心无单测、无覆盖率、CI 门槛弱 |
| 分发与生态 | 8 | 2.0 | 无 Release/npm/签名/Registry，100MB |
| 文档与治理 | 5 | 2.5 | 中文文档质量高；主文档滞后、无英文/治理文件 |
| 性能 | 4 | 3.0 | 截图/往返达标；GDI 无增量、snapshot 预算偏紧 |
| **合计** | **100** | **63.5 ≈ 64** | |

**判定**：
- “成熟 MCP 项目”：**未达到**。协议层达标，生态与感知层未达标。
- “可供 OpenCode 调试 Windows 应用”：**有条件可用**。Win32/Word/UWP 的中等任务可完成；PPT/Chromium/自绘控件需要视觉兜底，当前只能“可尝试、不可依赖”。

---

## 14. 与 Word 轮（run6）相比的静态进步

| 项 | Word 轮（run6） | 本轮静态状态 | 变化 |
| --- | --- | --- | --- |
| 协议实现 | 官方 SDK 2.4.0（已换） | 同 | 保持 |
| 工具数 | 20 | 20 | 不变 |
| UWP 释放 | 已修（内容进程） | 代码复核通过 | 保持 |
| MSAA | ordinal + 超时 + COM 释放 | 代码复核通过 | 保持 |
| 错误码 | 53 条 | 发现 5 条死码 + 锚点缺失 | **新发现** |
| 多 profile | 文档称“独立槽位” | 实测非默认 profile 挂载失败 | **新发现（P1）** |
| readOnlyHint | 未审 | 3 个工具标注与副作用不符 | **新发现（P1）** |
| 剪贴板 | 未审 | 只还原文本，非文本会被清空 | **新发现（P1）** |
| PPT/Excel | 未涉及 | 未建模，`document_ready` 恒 true | **新发现（P1）** |
| 主技术文档 | 未审 | 仍写 18 工具/手写 JSON-RPC | **新发现（P2）** |
| 分发 | 06 已列为缺口 | winget 占位符、Registry 无清单 | 缺口确认 |
| 安全 | 有 allowlist/限流 | `run_script` 黑名单可绕过 | **新发现（P2）** |

**结论**：上一轮修掉的是“实现缺陷”；本轮静态审查暴露的是“**语义与承诺缺陷**”（标注、文档、默认值、多会话边界）。这类问题不会让测试直接失败，但会让调用方做出错误决策——恰恰是“成熟度”的核心。

---

## 15. 附：文档与代码不一致清单

| 文档 | 声明 | 实际 | 处理 |
| --- | --- | --- | --- |
| `README.md:10` | 当前 run5 | run6 数据已入库 | 改为 run6，并链接 03 |
| `技术文档.md:110/819/940` | 18 工具、手写 JSON-RPC | 20 工具、官方 SDK | 重写或标注历史 |
| `需求文件.md:92/164/229` | 手写 JSON-RPC、18 工具 | 同上 | 同上 |
| `docs/changes-2026-11.md:56` | winget/Registry 已就绪 | 占位符、无 server.json | 改为“待发布” |
| `docs/changes-2026-11.md:30` | release 3s | 6s | 更正 |
| `docs/changes-2026-11.md:51` | 全屏 P95 53ms | run6 58ms | 更正 |
| `docs/errors.md` | 每条错误可跳转 | 无锚点 | 加 `###` 标题 |
| `ErrorCatalog.cs` | 唯一事实来源 | 5 条从未抛出 | 清理或实现 |
