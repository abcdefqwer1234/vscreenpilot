# VScreenPilot 实际测试报告（Word 实测 + CCW 附录）

> 测试对象：`C:\Users\31746\Desktop\VScreenPilot`（VScreenPilot 0.1.0，git `c86f6fd`）
> 可执行文件：`artifacts/server/VScreenPilot.exe`，SHA256 `55DD4F78…B593`，80.75MB（self-contained）
> 对照补丁构建：`artifacts/patched/VScreenPilot.exe`，SHA256 `3F4A82B9…C127`（仅加 `MOUSEEVENTF_VIRTUALDESK`）
> 测试日期：2026-11-14
> 关联文档：[静态审查报告](01-静态审查报告.md)、[实际测试计划](02-实际测试计划.md)、[改进建议](04-改进建议.md)

---

## 0. 结论摘要

**VScreenPilot 目前是一个“设计良好、实现有硬伤”的准生产原型：能完成 Word 这类 Win32 办公软件的半自动任务，但还不能称为成熟 MCP，也无法覆盖 CCW 12 这类 Chromium 应用。**

**结论置信度**：输入坐标、截图悬崖、模态对话框、参数校验、baseline 复用这几项有可复现实验支撑（高）；性能绝对值只在一台 VM + 一种 VDD 上测得（中）；UIPI、DPI/多屏、Office 全家桶未覆盖（低）。

先给三档结论：

| 判定对象 | 结论 | 依据 |
| --- | --- | --- |
| 成熟 MCP 项目 | **否** | 协议基本正确，但参数类型不校验、错误码退化、重复 id 不拒绝、无进度、无 CI/发布流程 |
| Windows 简单应用调试（Win32/WinForms/WPF） | **基本可用** | UiTarget 10/14 用例通过；find/tree/写值/触发/断言/视觉定位均可用 |
| Word 文档（Win32/COM） | **半自动（B 档）** | 新建/输入/格式/表格/本地保存/多应用均通过；查找替换、另存为因模态对话框不可达而失败；详见第 9 节 |

动态测试确认的**两个阻断级问题**（与 Word 实测相互印证）：

1. **P0 输入坐标**：`mode=input` 缺少 `MOUSEEVENTF_VIRTUALDESK`，点击落不到虚拟屏；原始构建 ClickProbe 无记录，补丁构建误差 ≤2px。
2. **P0 截图性能悬崖**：连续 `capture_screen` 约 24 次后，单次从 ~80ms 跳到 **~1.9s** 并持续；200 次采样的 P95 = **2299ms**（目标 ≤50ms）。同会话内 320×180 区域截图始终 ~32ms，说明瓶颈在 `ScreenCapture` 的全屏 GDI 路径。

其余高频问题：作业对象绑定失败 **53/62**（崩溃兜底失效）；`launch.args` 传数组被静默忽略；`E_INTERNAL` 吞掉可操作信息；`baseline_frame_id` 跨区域静默复用；UWP（Calculator）无法接管；`run_script` 中文乱码。

**成熟度评分：66/100**（静态 65；补测的并发、契约、热插拔把可靠性/协议/测试各加 1，但模态对话框、截图悬崖与 400MB 级内存仍限制总分；理由见第 11 节）。

---

## 1. 测试对象与环境

| 项 | 值 |
| --- | --- |
| OS | Windows 11 26H2，build 26300（VMware 虚拟机） |
| 主屏 | `\\.\DISPLAY1`，1920×1440@60，168 DPI（物理像素） |
| 虚拟屏 | `\\.\DISPLAY9`（本轮重新挂载后的设备号），1280×720@60，96 DPI，位于 `(1920,0)` |
| Parsec VDD | **0.41.0.0**（0.45 在 VMware 上 Code 43，已降级，见环境记录） |
| Word | Microsoft 365，16.0.20527.20196（`OfficeSetup.exe` 安装） |
| .NET | SDK 10.0.404 / Runtime 10.0.15（构建与自包含发布均通过） |
| Node / Python | Node 24.22.3 / Python 3.13.13（测试脚本） |
| OpenCode | 已配置 `vscreenpilot` MCP（`codemode=false`，startup 120s / execution 600s） |

构建：`dotnet build VScreenPilot.slnx -c Release` → **0 警告 / 0 错误**；`--doctor` 在 VDD 0.41 下 `ok=true`。

---

## 2. 执行方式与偏差说明（必须先看）

测试计划要求区分三种执行方式。本轮实际执行如下：

| 执行方式 | 计划内容 | 实际执行 | 说明 |
| --- | --- | --- | --- |
| OpenCode AI 直接调用 MCP | GUI 体验、Word 全流程 | **已执行（Word 主线）** | 会话中途 MCP 工具目录曾失效，恢复后由本会话直接完成 Word 的启动、输入、格式、表格、保存、多应用与长文本（W-01..W-10）；更早的 `get_environment`/`capture_screen`/Notepad 闭环也由 AI 直接调用 |
| Python/Node 脚本驱动 MCP | PRT/SEC/REG/PERF/STAB | **全量执行** | 纯标准库客户端 `tools/audit/vsp_client.py`，18 个工具全部覆盖 |
| 人工操作 | UAC、Word 安装 | 执行 | UAC 由你点击；Word 由 `OfficeSetup.exe` 安装；未登录微软账号 |

**偏差与归因**：

- 会话中途 MCP 连接断开过一次（工具目录被移除），重启服务端后恢复；恢复前完成的调用保留为证据，恢复后 Word 全流程为 AI 直接调用。
- `EXP-01` 体验评分基于本会话的 Word 调用序列（约 60 次工具调用），不再依赖 Python 转述。
- Word 未完成微软账号登录，但文档可编辑（未见只读提示）；AutoSave 默认开启，保存位置为 OneDrive，云端文件未在本地落地（详见 9.2 W-07）。
- 原 CCW 12 的安装与观测保留为附录 A，不作为本轮主线。

---

### 2.1 测试台自身缺陷（不计入产品缺陷，但影响读数）

| 问题 | 影响 | 处理 |
| --- | --- | --- |
| `launch.args` 传数组被静默忽略 | GUI-01 首轮误判 | 改为字符串；同时记录为产品契约缺陷 |
| `save_path` 相对路径按服务端 cwd 落盘 | Word/CCW 截图“消失” | 后续全部用绝对路径；记录为 P2-11 |
| ClickProbe 用 `GetCursorPos` 读坐标 | P0 首轮读不到真实点击点 | 改用 `GetMessagePos` 后取得 0px 误差 |
| 单实例互斥 + 嵌套客户端 | 稳定性脚本曾在第二个客户端初始化超时 | 改为同一客户端内并发；记录为使用约束 |
| gui2 用例间状态污染（Notepad 多标签） | GUI-04/08 连带失败 | 每个用例 `release → 全新启动`；仍保留为体验缺陷 |
| Word AutoSave 默认开 + OneDrive 未运行 | 保存“成功”但本地无文件 | 用本地 docx 夹具绕开；记录为 P1-10 |

> 这些问题已在测试报告中显式区分，避免把测试台的坑算成产品缺陷。

---

## 3. 协议与安全（PRT-01..14 / SEC-01..10）

执行：`python tools/audit/bench_protocol.py --out tools/audit/results/run1`（首轮 30 个断言中 22 通过；PRT-09/10 等在补测中复核。按 14 个逻辑用例计，最终 PRT 12/14、SEC 7/10。）

| 用例 | 结果 | 观测 |
| --- | --- | --- |
| PRT-01 版本协商（3 个版本） | ✅ | 原样回显 2025-06-18 / 2025-03-26 / 2024-11-05 |
| PRT-02 未知版本回退 | ✅ | `1999-01-01` → `2025-06-18` |
| PRT-03 未初始化调用 | ✅ | `-32002` |
| PRT-04 tools/list | ✅ | 18 工具、按名称排序、全部有 `outputSchema`；`tools/list` 结果序列化后 **16,302 字节** |
| PRT-05 未知工具/缺 name | ✅ | 均为 `-32602` |
| PRT-06 重复请求 id | ❌ | 两个 `{}` 正常响应，**未返回 -32600**（协议卫生问题） |
| PRT-07 非法 JSON/非对象/缺 jsonrpc | ✅ | `-32700` / `-32600` / `-32600` |
| PRT-08 ping | ✅ | `{}` |
| PRT-09 取消 | ✅（补测） | 取消后无响应，`ping` 仍可用；未阻塞协议 |
| PRT-10 progress | ✅（补测） | `wait_for` 期间收到 `notifications/progress`；`get_ui_tree` 不发送进度 |
| PRT-11 1000 条 fuzz | ✅ | 进程存活、可继续响应；RSS 无异常增长 |
| PRT-12 单实例互斥 | ✅ | 第二实例 exit=3，stderr 提示持有互斥体 |
| PRT-13 EOF 清理 | ✅ | exit=0；P50 28ms（显示器已摘除时） |
| PRT-14 孤儿屏接管 | ⚠️ | 重启后确实沿用 `\\.\DISPLAY121`，但 `adopted_orphan=false`、`self_healed=false`；代码中 `_adoptedOrphan` **无赋值点**，字段是死的 |
| SEC-01 越界截图/点击 | ✅ | `E_OUT_OF_BOUNDS` |
| SEC-02 外来窗口 | ✅（补测） | 非会话窗口 → `E_FOREIGN_WINDOW` |
| SEC-03 系统键 | ⚠️ | `Win+L`/`Alt+Tab`/`Ctrl+Esc`/`Alt+Esc`/`Win+D`/`Win+Tab`/`Ctrl+Shift+Esc`/`Win+R` 被拦截；`Alt+Shift+Tab`、`Ctrl+Alt+Tab` 返回 **`E_NO_TARGET`**（不是 `E_SYSTEM_KEY_BLOCKED`）。这只证明**按键校验在目标解析之后**；未在有目标窗口时做端到端验证，因此“会真的发到系统”仍是推断 |
| SEC-04 run_script 默认关闭 | ✅ | `E_DISABLED` |
| SEC-05 超长文本 | ✅ | 100001 字符 → `E_INVALID_ARG` |
| SEC-06 落盘路径 | ✅ | 非工作目录/非 png → `E_INVALID_ARG` |
| SEC-07 无效 PNG | ✅ | `E_UNSUPPORTED_IMAGE` |
| SEC-08 非法 control_type | ❌ | **被静默接受**（返回 ok），没有 `E_INVALID_ARG` |
| SEC-09 baseline 区域一致性 | ❌ | 对 `(2240,220,120,90)` 使用 `(1940,20,120,90)` 的 `f23`，仍执行比较并返回 `passed=false`；**没有拒绝跨区域 baseline** |
| SEC-10 ref 容量（max_nodes=1500） | ✅ | 树节点 18，ref 可用，无 stale |

**最终计数**：PRT **12/14**（PRT-06 重复 id、PRT-14 接管标志失败）；SEC **7/10**（SEC-03 变体、SEC-08、SEC-09 失败）。

**协议结论**：核心生命周期、错误码、取消、fuzz 都合格；短板集中在“输入校验”和“可观测性”。

---

## 4. 补充缺陷回归（REG-01..18）

执行：`bench_regressions.py` + `bench_fixups.py`（修正坐标/基线/单实例导致的假失败）。

| 用例 | 结果 | 关键证据 |
| --- | --- | --- |
| REG-02 歧义窗口提示 | ❌ | 返回 `E_WINDOW_AMBIGUOUS`，但 hint 只有“拒绝接管”，**没有候选窗口/PID**，无法按提示精确指定 |
| REG-03 参数类型 | ❌ | `pid:"123"` → `E_INTERNAL`；`max_nodes:"abc"` → `E_INTERNAL`；`new_instance:"false"` 被当作真值接受 |
| REG-05 并发取消隔离 | ✅ | 并发 `get_log` 正常；被取消的 `wait_for` 无响应 |
| REG-06 释放不误点 | ✅ | `on_dirty=force` 释放后 ClickProbe 无点击记录 |
| REG-07 系统键变体 | ❌ | 7 个变体只有 5 个被拦截；`Alt+Shift+Tab`/`Ctrl+Alt+Tab` 退化为 `E_NO_TARGET` |
| REG-08 断言阈值 | ❌ | `coverage=1.5`、`coverage=-0.5`、`threshold=-1`、`threshold=2` 全部执行；按语义 `coverage=-0.5` 会恒真、`threshold=2` 会恒假（未做真值对照） |
| REG-09 脚本中文 | ❌ | `stdout` 为 `\uFFFD…OK\r\ngb2312`（GBK 被按 UTF-8 解码） |
| REG-12 10MB 单行 | ✅ | 进程存活、可响应，RSS 无异常 |
| REG-13 条件 schema | ⚠️ | 空 condition/空 expectation 正确；非法 region 返回 `E_INTERNAL`；`name=123` 被接受 |
| REG-14 UWP Calculator | ❌ | `launch_or_place_app {window:"计算器/Calculator"}` 均 `E_WINDOW_NOT_FOUND`；`ApplicationFrameHost` 宿主未被解析 |
| REG-15 消息拖拽/滚轮 | ✅ | 返回明确结果；未出现崩溃 |
| REG-16 特殊字符 | ✅ | `@€ \ ~ 中文` 写入并回读正确（type_text 的 Unicode 路径有效） |
| REG-17 1×1 像素 | ✅ | `match_visual pixel` 与 `wait_for color` 均有毫秒级返回（详见性能节） |
| REG-18 P3 抽查 | ⚠️ | `scale=0/10` 被接受；`get_log.level="nonsense"` 被接受；日志 memory/file 均可用 |
| PRT-14b 接管标志 | ❌ | 字段恒 false（死字段） |
| SEC-09c 同区域 baseline 对照 | ✅ | 同区域 `passed=true`，说明缺陷只在跨区域 |

---

## 5. GUI 能力（GUI-01..15）

执行：`bench_gui2.py`（每个用例 `release → 全新启动`），**10/14 通过**；共 69 次工具调用。

| 用例 | 结果 | 观测 |
| --- | --- | --- |
| GUI-01 Notepad 输入→保存 | ❌ | 启动后标题仍是“记事本”，`args` 未生效；`type_text.verified=false`；`Ctrl+S` 后文件 0 字节（另存为对话框被遗留） |
| GUI-02 Unicode/长文本 | ❌ | 2600 字符耗时 **9778ms**，`verified=false`（warning：无法用 TextPattern 回读）；文件 0 字节 |
| GUI-04 WinForms 表单 | ❌ | 写值/勾选成功，`invoke(submit)` 返回 `E_INTERNAL`（`0x80070003`），目标应用随后退出，oracle 未生成 |
| GUI-05 语义定位 | ✅ | exact/contains/regex/all 均返回，`count=1/2` 正确 |
| GUI-06 语义树裁剪 | ✅ | depth 3/5/8：节点数递增，耗时 38–44ms |
| GUI-07 写值模式 | ✅ | `append` 无键盘 → `E_PATTERN_UNSUPPORTED`；`insert` 覆盖 → `E_DESTRUCTIVE_REQUIRED`；带 `allow_keyboard/allow_destructive` 成功 |
| GUI-08 触发模式 | ❌ | `toggle` 成功；`submit(auto)` 在本次会话再次 `E_INTERNAL`（同 GUI-04 根因） |
| GUI-09 按键/组合/sequence | ✅ | `Ctrl+A`、`Ctrl+C`、`End`、sequence 均成功 |
| GUI-10 wait_for | ✅ | element/color/window/pixel_diff 全部 `satisfied` |
| GUI-11 assert_region | ✅ | color / changed / baseline 三条路径均返回，`passed` 语义正确 |
| GUI-12 视觉定位 | ✅ | 红色方块模板匹配误差 **0px**；color/pixel 正常 |
| GUI-13 release | ✅ | `keep_display=true/false` 与 `display_kept/removed` 一致 |
| GUI-14 多应用 pid | ✅ | 同时接管 Notepad + UiTarget，`pid` 定向输入/查找正确 |
| GUI-15 故障恢复 | ✅ | 目标被杀后错误可识别；重新 launch 后恢复 |

**说明**：
- **GUI-03（Calculator）未执行**：UWP 宿主在 REG-14 已证明无法接管，本轮未单独跑，表中为 14 项而非 15 项。
- GUI-04 在**手工复现**时 3 次成功（oracle 正确），但**没有留存脚本产物**；只在连续跑完 Notepad 长文本 + release 后稳定失败。归因是“跨会话状态污染 + 未包装的 UIA 异常”，证据强度**中**，不作为表单能力缺失的结论。

**AI 使用体验（已完成的部分）**：工具名/描述可发现性好，`get_environment` 一次选对；`capture_screen` 图片能直接进入模型上下文；Notepad 首次 `type_text` 返回 `E_FOCUS`，按 hint 先 `click(message, keep_foreground)` 后成功（2 次调用，可恢复）。截图 1.45MB PNG 对上下文成本偏高。

---

## 6. 性能（PERF）

执行：`bench_perf.py --shots 200 --release-cycles 20 --sessions 20`。

| 指标 | P50 | P95 | 目标 | 结果 |
| --- | ---: | ---: | --- | --- |
| 启动握手 | 112.5ms | 156.3ms | ≤300ms | ✅ |
| 首次挂屏 | 2947ms | 2947ms（n=1） | ≤3000ms | ✅（临界） |
| 截图 1280×720 | **1846ms** | **2299ms** | ≤50ms | ❌ **悬崖** |
| 截图区域 320×180 | 31.9ms | 43.5ms | — | ✅ |
| UIA find_element | 27.6ms | 40.3ms | ≤100ms | ✅ |
| UIA get_ui_tree | 33.3ms | 41.2ms | ≤200ms | ✅ |
| type_text 1k | 4616ms | 10072ms | — | ⚠️ 抖动大 |
| type_text 10k | 1598ms | 1608ms | — | 可接受 |
| release | 2883ms | 2927ms | ≤3000ms | ✅（临界） |
| EOF 退出 | 2910ms | 3011ms | ≤5000ms | ✅ |

**截图悬崖复现**：

```text
前 20 次: 55–98ms
第 24 次: 1269ms
第 25 次起: 1950–2079ms（持续到第 60 次）
同会话 320×180: 24–37ms
encode_ms: 仅 19–82ms
```

结论：不是 PNG 编码，也不是虚拟屏本身，而是**每次全屏 GDI `BitBlt(SRCCOPY|CAPTUREBLT)` + 新建 DIB 的路径在 Parsec IDD 上触发了稳定劣化**。这是 AI 迭代调试的致命伤：每看一次屏幕要等 2 秒。

**补充表征（run2）**：
- 干净会话连续全屏：前 18 次 80–160ms，第 19 次起 1.6–2.9s（首轮观测是第 24 次，说明起点不固定）；
- 先做 200 次区域截图再测全屏：**第一次就已是 1.9s**，说明触发与“累计截图/时间”相关，而不是严格的全屏调用次数；
- 500 次区域截图 P95 = 52.9ms、句柄 327→340 稳定；但 120 次全屏后 RSS 从 89MB 涨到 **418MB**（帧缓存 64MB + LOH 抖动），释放后回落。
- 本轮 PERF 采样期间 CCW MSI 正在挂起安装，绝对数值可能有轻微干扰；悬崖结论已两次独立复现。

---

## 7. 稳定性（STAB）

执行：`bench_stability.py --sessions 50 --soak-min 15`（原始数据 `results/run1/stability/`）。

### 7.1 50 会话循环：**50/50 通过**

总耗时 311.9s。每次 `start → ensure → capture → release(keep_display=false) → EOF` 均成功，显示器数量回到 1，无残留进程。

| 阶段 | P50 | P95 | 说明 |
| --- | ---: | ---: | --- |
| 启动 | 115.9ms | 695.2ms | 少数冷启动接近 1s |
| 挂屏 | 2666ms | **3620ms** | 超计划 3s 目标 |
| 截图 | 82.7ms | 128.5ms | P99 = 2009ms（一次悬崖） |
| release | 2874ms | **3145ms** | 超 3s 目标 |
| EOF | 28.4ms | 152.7ms | 显示器已摘除时很快 |

### 7.2 15 分钟 soak：**失败（可复现的会话失能）**

- 1388 次迭代（capture / find / tree / type 循环），172 个采样点；
- 从第 **107** 次迭代起，`capture_screen` 持续返回 `E_INTERNAL`，`type_text` 持续返回 **`E_NO_INTERACTIVE_DESKTOP`**，直到结束（538 个错误）；
- 触发点与一次 **UAC 安全桌面**事件重合（测试过程中尝试提权被杀毒/UAC 取消）：此后 `OpenInputDesktop` 一直失败，**服务端没有自愈**；
- RSS：58.3MB → 182.5MB 后平台化（峰值 213.2MB），线性斜率 50.4MB/h 主要由启动爬升造成，**不是持续泄漏**；
- 句柄：419 → 427（分段均值 -5.3），**无句柄泄漏**；
- 口径说明：作业对象失败率 53/62 是对当日日志的统计，父进程包含 Node 客户端与 OpenCode 两种，不是受控实验；用于说明“兜底经常不可用”，不作为精确比例。
- 结束 `release` + EOF：exit=0，显示器摘除，残留进程 0。

**结论**：正常负载下资源稳定，但“安全桌面/锁屏/UAC 之后输入与截图永久失能”是真实的生产风险；且 `capture_screen` 在该状态下返回 `E_INTERNAL` 而不是可操作错误。

### 7.3 12 项故障注入

| 用例 | 结果 | 观测 |
| --- | --- | --- |
| 01 应用中途退出 | ✅ | `E_ELEMENT_NOT_FOUND` / `E_NO_TARGET` 可识别 |
| 02 外部摘屏 | ⚠️→✅ | 首轮 `VddProbe removeall` 失败未注入；run2 用 `remove 0` **成功注入**，截图仍成功且服务端自愈（见 13.4 HOT-01）。按最终结论记为通过（自愈） |
| 03 坐标越界 | ✅ | `E_OUT_OF_BOUNDS` |
| 04 外来窗口 | ✅ | 拒绝 |
| 05 无效 PNG | ✅ | `E_UNSUPPORTED_IMAGE` |
| 06 超长路径 | ✅ | `E_INVALID_ARG` |
| 07 取消请求 | ✅（口径修正） | 取消后无响应、`ping` 正常；脚本判定条件写错，原始数据见 `summary.json` |
| 08 调用中 EOF | ✅ | 能退出（exit=0） |
| 09 重复实例 | ✅ | exit=3 |
| 10 UIPI | ⚠️ 未测 | MCP 无法启动提权窗口，路径不可达 |
| 11 慢启动应用 | ✅ | 有明确超时/结果 |
| 12 UIA 空结果 | ✅ | `E_ELEMENT_NOT_FOUND` |

### 7.4 残留检查

`residual = {processes: 0, monitors: 1}`；日志/状态文件正常；无临时 profile 残留。

---

## 8. P0 输入坐标对照实验（原始 vs 补丁）

**方法**：新建 `tools/audit/targets/ClickProbe`（WinForms，记录 `WM_LBUTTONDOWN` 的 `GetMessagePos()` 屏幕坐标）。用 `launch_or_place_app` 放到虚拟屏，`click { x: 2240, y: 240, mode: "input" }`，读日志。
原始构建 `artifacts/server`；补丁构建 `artifacts/patched`，只把 `MouseMove | MouseAbsolute` 改成 `MouseMove | MouseAbsolute | MouseVirtualDesk (0x4000)`。

| 构建 | 期望坐标 | 记录到的坐标 | 误差 | 记录数 | 结论 |
| --- | --- | --- | --- | ---: | --- |
| 原始 | (2240,240) | —（无记录） | — | 0 | ❌ 点击落到主屏，目标窗口收不到 |
| 补丁 | (2240,240) | **(2240,240)** | **0px** | 3 | ✅ 精确命中 |

**结论**：P0 成立——`mode=input` 在未加 `MOUSEEVENTF_VIRTUALDESK` 时不会落到虚拟屏。补丁只改一行 flag 即可修复，副作用仅限 `mode=input`。

**方法学注意**：
- `InputInjector` 在 `finally` 里恢复光标，且 `SendInput` 消息是异步的，用 `GetCursorPos` 会读到旧坐标；必须用 `GetMessagePos()`（本轮已修正 ClickProbe）。
- 窗口首次被点击会被“点击激活”吞掉，需要先做一次预热点击，再清空日志测真实输入。

---

## 9. Word 实测（AI 直接调用）

> 环境：Word 16.0.20527.20196（Microsoft 365，`C:\Program Files\Microsoft Office\root\Office16\WINWORD.EXE`），虚拟屏 1280×720。
> 激活状态未确认：安装后未登录微软账号，但文档可编辑；不能据此判断已授权（可能处于宽限期）。
> 全部步骤由 **OpenCode AI 在本会话中直接调用 vscreenpilot 工具** 完成；docx 由 `tools/audit/word_oracle.py` 独立解压核验。

### 9.1 结果总表

| ID | 目标 | 结果 | 证据 |
| --- | --- | --- | --- |
| W-01 | 启动 Word、新建文档、输入中英混排 | ⚠️ | 输入通过（48 字，`verified=true`）；但保存进了 OneDrive 云端，**本地无文件、无 oracle**，不能算完整通过 |
| W-02 | `role=document_body` 回读正文 | ✅ | 文本逐字一致；`automation_id=Body` |
| W-03 | 第一行加粗、第二行标题 1 | ✅ | `press_keys`（Ctrl+B / Ctrl+Alt+1）；XML 含 `w:b`、`w:pStyle w:val="1"` |
| W-04 | 查找替换（Ctrl+H） | ❌ | 对话框不可达：`find_element` 看不到；`type_text` 落到正文；只能人工取消 |
| W-05 | 插入 2×2 表格并填 4 格 | ✅ | `Alt+N,T` → Right/Down → Enter；XML `w:tbl` = `[[A1,B1],[A2,B2]]` |
| W-06 | 打开本地 docx → 编辑 → 保存 | ✅ | 980B → 12,353B（最终 14,188B）；新增 `EDITED-BY-MCP 中文追加` |
| W-07 | 另存为本地路径 | ❌ | Alt+F / F12 / Ctrl+Shift+S 均无响应；现代保存框不接受完整路径；保存到 OneDrive 云端未落地 |
| W-08 | 只读/受保护视图 | ⚠️ 部分 | 受保护视图下 `type_text` 返回 ok 但 `verified=false`；未确认文件是否被改写（run2 DIA-04） |
| W-09 | Word + Notepad 多应用 pid 隔离 | ✅ | Word 含 `WORD-ONLY`，Notepad 含 `NOTEPAD-ONLY`，互不串 |
| W-10 | 长文本 1949 字 | ✅ | `verified=true`，7651ms，61 个分块；docx 中 50 行完整 |

**核心工作流（打开→编辑→保存→核验）已跑通**，这是相对 CCW 的实质性进展。

### 9.2 逐项记录

**W-01 / W-02：输入与语义回读**

- `launch_or_place_app` 在数秒内返回（首次含加载项/模板初始化），`ensure_document=true` 自动 Ctrl+N；
- `click(mode=message)` 定位正文后 `type_text` 成功，中文、英文、换行都正确；
- `find_element {role:"document_body"}` 返回 `Edit/Body`，`get_element_text` 与输入逐字一致。

**W-03：格式**

- 键盘路径稳定：`Ctrl+Home → Shift+End → Ctrl+B → Down → Ctrl+Alt+1`；
- oracle 确认 `w:b` 与标题样式；注意中文 Word 的 Heading 1 样式 id 是 **`1`**，不是 `Heading1`，oracle 已兼容。

**W-04：查找替换失败（重点缺陷）**

- `Ctrl+H` 能打开“查找和替换”对话框，但它是**独立顶层窗口**：
  - `get_ui_tree`/`find_element` 只遍历主窗口，看不到对话框；
  - `type_text` 会把文字打进**正文**（实测把 “DONE” 插到标题前）；
  - 点击“取消”按钮（`mode=message`/`input`）也没有稳定生效，最后靠人工关闭；
- 结论：**凡需要模态对话框的 Word 任务，当前工具链不可用**；这直接导致 W-04、W-07 失败。

**W-05：表格（可用的替代路径）**

- `Alt+N,T` 打开表格网格，`Right/Down` 选 2×2，`Enter` 插入；
- 之后 `type_text` + `Tab` 填 4 个单元格，`Ctrl+S` 保存；
- oracle：`tables = [[A1,B1],[A2,B2]]`。

**W-06 / W-10：本地保存与长文本**

- 用最小 docx 夹具（`tools/audit/results/run1/word/sample.docx`）打开，编辑后 `Ctrl+S` 直接写本地文件，oracle 通过；
- 长文本 1949 字耗时 7651ms（61 块），`verified=true`，无截断。

**W-07：另存为失败（环境 + 工具叠加）**

- Word 的 Backstage 快捷键（`Alt+F`）、`F12`、`Ctrl+Shift+S` 在本机均无响应（疑似 OfficePLUS 加载项/输入注入问题）；
- `Ctrl+S` 的现代“保存此文件”对话框：
  - 文件名输入可用（`type_text` 能写入）；
  - **输入完整路径会被拒绝**（保存按钮变灰）；
  - “更多选项…”点击不生效（`mode=input` 也无效）；
- 默认保存位置是 `OneDrive - 个人`，而 OneDrive 客户端未运行：Word 显示“已保存”，但**本地找不到文件**（云占位）。
- 归因：**环境（OneDrive 默认位置）+ 工具（模态对话框不可达）**，不是单一的 Word 兼容性问题。

### 9.3 Word 场景的可用性结论

| 判定 | 结论 |
| --- | --- |
| 全自动（A） | 否 |
| 半自动（B） | **是**：新建/输入/格式/表格/本地保存/多应用/长文本均可由 MCP 完成；查找替换与另存为需要人工 |
| 不可用（C） | 否 |

**对“AI 调试 Windows 应用”的意义**：Word 证明了“UIA 文本 + 键盘”路径在真实办公软件上可用；但**模态对话框（保存/查找/插入表格的参数框）是当前最大断点**，也是与前代 GUI 自动化工具差距最大的地方。

### 9.4 产物

- `tools/audit/results/run1/word/sample.docx`：完整成品（加粗、标题、表格、长文本，14KB）；
- `w-final-word2.png`：最终界面截图（加粗标题、2×2 表格、长文本可见）；
- `w01-start.png`：Word 首次启动截图；
- `word_oracle.py`：docx 独立核验脚本。

## 10. 与同类项目的动态对照

| 维度 | VScreenPilot（实测） | Windows-MCP / mcp-server-windows（引用） | UI-TARS / Agent-TARS（引用） |
| --- | --- | --- | --- |
| 传输/隔离 | stdio，单实例，共享桌面 + 虚拟屏 | stdio，共享桌面 | GUI Agent 生态，通常整机 |
| 简单 GUI 成功率 | 10/14（含 2 个应用兼容失败） | 官方演示覆盖 Win32/UWP | 端到端模型，非 MCP 工具 |
| 截图延迟 | 区域 32ms / 全屏悬崖 1.9s | 官方 README 未给统一口径 | 论文报告为百毫秒级 |
| 语义层 | UIA + ref + 指纹重定位（实测 stale=0） | UIA 封装 | 视觉为主，UIA 为辅 |
| 错误可操作性 | 有 `code/hint/stage`，但类型错误退化为 `E_INTERNAL` | 多为 Python 异常 | 模型自然语言 |
| 安全边界 | 越界/外来窗口/系统键/脚本开关（实测基本有效） | 依赖实现 | 通常较弱 |

**判断**：VScreenPilot 的“工具契约 + 安全边界”设计在同类中偏强；短板是**性能实现、参数校验、Chromium 兜底**。它更像“带安全护栏的 UIA 封装”，而不是“视觉 Agent 平台”。

---

## 11. 成熟度评分（动态更新）

| 维度 | 权重 | 静态 | 动态 | 理由 |
| --- | ---: | ---: | ---: | --- |
| 功能完整度 | 20 | 16 | 16 | 18 工具齐全；Word 主流程通过 |
| 可靠性/生命周期 | 20 | 12 | 12 | 50/50 会话、320 次并发调用、热插拔自愈；但作业对象多数失败、E_INTERNAL 连锁、孤儿屏标志失效 |
| MCP 协议与安全 | 15 | 10 | 10 | outputSchema 18/18 一致、错误形状正确、取消正常；重复 id、参数不校验、系统键变体仍在 |
| Windows 兼容性 | 15 | 9 | 11 | WinForms/Word 主流程通过；UWP/多标签/模态对话框受限 |
| 性能 | 10 | 8 | 5 | 截图悬崖 P95 2.3s |
| 测试/CI/可复现 | 10 | 5 | 7 | 新增并发/契约/长跑/热插拔/对话框五组脚本；仍无 CI |
| 分发/文档/社区 | 10 | 5 | 5 | 0.1.0、无 release、文档偏实现 |
| **合计** | 100 | **65** | **66** | |

---

## 12. 未完成项与复现方式

| 项 | 状态 | 原因 | 复现命令 |
| --- | --- | --- | --- |
| OpenCode AI 直接调用 | 已完成（Word） | — | 第 9 节 W-01..W-10 |
| Word 查找替换 / 另存为 | 未完成 | Word 自定义对话框不在 UIA 树；Backstage/F12 无响应 | 第 9 节 W-04/W-07、13.5 |
| Word 只读/受保护视图 | 部分 | 只验证了 `verified=false`，未确认文件未被改写 | run2 DIA-04 |
| CCW 12 全自动建项/编译 | 未完成 | 安装不完整 + CefSharp/UIA 阻断 | `tools/audit/bench_ccw.py` |
| UIPI 错误路径 | 未测 | MCP 无法启动提权窗口 | 需人工构造管理员进程 |
| 30min soak | 本轮 15min | 时间预算 | `bench_stability.py --soak-min 30` |

**原始数据**：`tools/audit/results/run1/`（protocol / regressions / gui2 / perf / stability / ccw / virtualdesk / word）。
**复现环境**：VDD 固定 0.41，ADD/REMOVE 间隔 ≥3s；测试脚本默认 `VSP_NO_INPUT=1`（输入类用例显式关闭）。

---

## 13. 补充测试（run2）

> 执行时间：2026-11-14 20:00–20:40。原始数据：`tools/audit/results/run2/`。
> 目的：补齐首轮缺失的并发、契约一致性、长跑内存、热插拔与对话框矩阵。

```powershell
python tools/audit/bench_concurrency.py   --out tools/audit/results/run2
python tools/audit/bench_contract.py     --out tools/audit/results/run2
python tools/audit/bench_capture_soak.py --out tools/audit/results/run2
python tools/audit/bench_hotplug.py      --out tools/audit/results/run2
python tools/audit/bench_dialogs.py      --out tools/audit/results/run2
```

### 13.1 并发（CONC-01..05）

| 用例 | 结果 | 数据 |
| --- | --- | --- |
| 8 线程 × 20 次（4 工具混合） | ✅ | 160/160；P50 68.7ms、P95 313ms、总耗时 2.46s |
| 16 线程 × 10 次（加压） | ✅ | 160/160；P50 34.6ms、P95 594ms、总耗时 1.92s |
| 取消 A + 闸门饱和 | ✅ | A 取消后无响应；A 活跃时 `get_ui_tree` 等待 **5018ms 后返回 `E_BUSY`**；A 取消后同一调用成功 |
| 并发 release | ✅ | 截图进行中 release → 截图 `E_BUSY`，release ok，随后会话可用 |
| 并发后资源 | ✅ | 句柄 337→430（+93），后续长跑稳定在 ~340，判断为 UIA 预热而非泄漏 |

**结论**：并发不会崩溃，单闸门在长任务（`wait_for`）期间会让其他 GUI 工具**等 5s 后失败**，而不是排队。AI 需要能识别 `E_BUSY` 并重试。
> 波动说明：同一脚本两次运行的 P95 分别为 251/460ms 与 313/594ms（8/16 线程），本表取最后一次持久化数据；趋势一致，绝对值受宿主负载影响。

### 13.2 契约一致性（CONT-01..04）

| 用例 | 结果 | 观测 |
| --- | --- | --- |
| 17 个有返回工具 outputSchema vs structuredContent | ✅ | 全部一致，无缺字段/类型错误 |
| `release_environment` 单独校验（CONT-05） | ✅ | structuredContent 与 schema 一致；合计 18/18 |
| 错误响应形状 | ✅ | 4 个错误均含 `isError` + `code` + `message` |
| 相对 `save_path` 落盘位置 | ⚠️ | 文件落在 **`artifacts/server/`**（服务端 cwd），客户端 cwd 无文件；行为稳定但不符合直觉 |
| 文本/图片字节统计 | ✅ | `text_bytes`、`image_bytes`、`png_bytes` 均可读 |

### 13.3 长跑截图（CAP-01..03）

| 用例 | 结果 | 观测 |
| --- | --- | --- |
| 区域截图 500 次 | ✅ | P50 35.9ms、P95 52.9ms；句柄 327→340；RSS 38→60MB 后平台化 |
| 全屏截图 120 次 | ✅（悬崖复现） | 首次即 1.75–2.1s（前序区域截图触发）；干净会话第 19 次起变慢 |
| 悬崖后区域截图 | ✅ | P50 36.3ms、P95 42.6ms，仍快 |

**内存**：120 次全屏后 RSS 峰值 **418MB**（基线 89MB，结束时 352MB），释放后回落；无 GDI 句柄增长。对长时间视觉循环，这是需要关注的上限。

### 13.4 显示热插拔（HOT-01..03）

| 用例 | 结果 | 观测 |
| --- | --- | --- |
| 会话中外部 `remove 0` | ✅（自愈） | 截图仍成功；`get_environment ensure` 重新挂载，monitors=2 |
| 外部 `removeall` | ✅ | 可恢复 |
| 外部新增显示 | ✅ | 会话仍绑定自己的虚拟屏，不误用新屏 |

**修正**：首轮 FAULT-02 的“外部摘屏未注入成功”结论不准确——本轮成功注入，且服务端自愈；应记为**通过（自愈）**。

### 13.5 对话框矩阵（DIA-01..04）

| 用例 | 结果 | 观测 |
| --- | --- | --- |
| Word 保存（AutoSave 开） | ⚠️ | `Ctrl+S` **不弹框**，直接云保存；本地无文件。用例按“应出现对话框”编写，故判定失败，但结论是“AutoSave 改变了保存语义” |
| Word 查找替换 | ❌ | 窗口存在（class `bosa_sdm_msword`，owner=主窗口），**不在 UIA 树**；`type_text` 返回 ok，本次正文未变（上轮曾插入正文），行为不稳定 |
| Notepad 另存为 | ✅（反向证据） | 经典 `#32770` 对话框**在 UIA 树中**；用例按“不在 UIA”编写而失败，实际证明“对话框不可达”是 **Word 自定义对话框特有** |
| 受保护视图 | ✅（部分） | `type_text` 返回 ok、`verified=false`；未确认文件未被改写 |

**结论（修正 P1-9 口径）**：不是“所有模态对话框不可达”，而是 **Word 的非标准对话框（bosa_sdm_msword）不被 UIA 暴露**，经典 Win32 对话框可达；同时 `Ctrl+S` 在 AutoSave 下不产生对话框，容易让 AI 误判“已保存”。

---

## 14. 计划完成度、体验评分与证据索引

### 14.1 计划完成度矩阵

| 计划项 | 状态 | 说明 |
| --- | --- | --- |
| 阶段 0 环境/构建/VDD/OpenCode | ✅ | — |
| PRT-01..14 | ⚠️ 已执行 | 12/14 通过 |
| SEC-01..10 | ⚠️ 已执行 | 7/10 通过；SEC-03 端到端未证 |
| P0 输入对照 | ✅ | 0px |
| GUI-01..15 | ⚠️ 14/15 | GUI-03（Calculator）未跑 |
| PERF | ✅ | 200 次截图 + 补充长跑 |
| STAB 50 会话 | ✅ | 50/50 |
| STAB soak | ⚠️ 缩水 | 15min（计划 30min） |
| 12 项故障 | ⚠️ | 外部摘屏已补测；UIPI 未测 |
| 并发 8/16 线程 | ✅ | run2 CONC-01/02 |
| 契约/outputSchema | ✅ | run2 CONT-01/02 |
| 长跑内存/句柄 | ✅ | run2 CAP-01..03 |
| 热插拔 | ✅ | run2 HOT-01..03 |
| 对话框矩阵 | ✅ | run2 DIA-01..04 |
| Word W-01..W-10 | ⚠️ 6 通过 / 2 失败 / 2 部分 | W-01 保存未本地验证；W-08 未确认只读 |
| EXP 体验评分 | ✅ | 见 14.2 |
| 上下文成本 | ✅ | 见 14.3 |
| CMP 动态基线 | ❌ | 未跑 PowerShell UIA 基线 |

### 14.2 体验评分（1–5，AI 直接调用）

| 维度 | 分数 | 证据 |
| --- | ---: | --- |
| 工具可发现性 | 4 | 18 个工具名/描述清晰，多数一次选对；`args` 类型歧义扣 1 |
| 错误可操作性 | 2 | `E_INTERNAL`、`E_BUSY`、保存对话框失败缺少下一步；`E_FOCUS` 的 hint 可用 |
| 失败恢复 | 3 | `release`/重开可恢复；模态对话框会卡死流程，只能人工 |
| 调用效率 | 3 | Word 主流程约 60 次调用；查找替换反复重试 |
| 上下文成本 | 2 | 单张全屏 PNG 0.3–1.45MB；18 工具 schema 16KB；长文本树/图片容易淹没上下文 |
| 安全与可控性 | 4 | 越界/外来窗口/系统键/脚本默认关闭；`mode=input` 需要显式选择 |
| **均分** | **3.0** | — |

### 14.3 上下文成本

| 指标 | 数值 | 说明 |
| --- | ---: | --- |
| 工具 schema | 16,302 字节 | `tools/list` 结果序列化 |
| 全屏 PNG | 38KB（虚拟屏桌面）–1.45MB（OpenCode 首图） | 内容复杂度决定 |
| Word 截图 | 0.17–0.55MB/张 | 本轮保存 4 张，约 1.19MB |
| 错误文本 | 150–400 字节/次 | 含 code/message/details |
| Word 调用次数 | ~60 次 | 含重试；未做 token 精确统计（代理口径 bytes/4） |

### 14.4 证据索引

| 证据 | 路径 |
| --- | --- |
| 协议/安全 | `results/run1/protocol/summary.json` |
| REG/SEC 补测 | `results/run1/regressions/{summary,fixups}.json` |
| GUI | `results/run1/gui2/summary.json` |
| 性能 | `results/run1/perf/summary.json` |
| 稳定性 | `results/run1/stability/summary.json` |
| P0 对照 | `results/run1/virtualdesk/summary.json` |
| 并发 | `results/run2/concurrency/summary.json` |
| 契约 | `results/run2/contract/summary.json` |
| 长跑截图 | `results/run2/capture/summary.json` |
| 热插拔 | `results/run2/hotplug/summary.json` |
| 对话框 | `results/run2/dialogs/summary.json` |
| Word 成品 | `results/run1/word/sample.docx`、`w-final-word2.png` |

---

## 15. 重构后复测（run6，2026-11-15）

> 剩余问题与下一轮计划见 [06-剩余问题与下一步计划](06-剩余问题与下一步计划.md)。

> 被测二进制：`artifacts/server6/VScreenPilot.exe`（重构版，MCP SDK 2.4.0）
> 最终 SHA256：`84BEA3E1120C8C4DED316F33F6F4BDBB3EDF01E29A7468693C7E2FCAF065BFD8`（20 工具、31 单测）
> 原始数据：`tools/audit/results/run6/`。每个 summary.json 都写入同一个 `binary_sha256`。未重做 CCW。

### 15.1 结果总览

| 组 | 结果 | 关键数据 |
| --- | --- | --- |
| 单元测试 | **31/31** | PNG 往返/缩放/过滤/CRC、KeyMap、快照格式、错误目录、写调度（含 held_ms）、参数 schema、profile、Job Object |
| PRT/SEC | **30/30** | 官方 SDK；`tools/list` 20 工具；SEC-10 大树枝改 1024 上限后通过 |
| 契约 CONT | **10/10** | 18 个有返回工具 + release；新增 ref 代际、嵌套参数、save_path 单落点、资源 size/preview、max_nodes |
| 并发 CONC | **5/5** | 8/16 线程；只读观察不被 `wait_for` 阻塞；release 抢不到写租约返回 E_BUSY |
| 截图 CAP | **3/3** | 区域服务端 P50 17ms / P95 25ms；全屏 P50 43ms / P95 58ms；**无悬崖**；峰值 RSS 196MB |
| 回归 REG | **19/19** | UWP Calculator 已可接管/查找/触发；记录 5s 内 |
| GUI | **14/14** | 全部通过（含 GUI-08/12） |
| 对话框 DIA | **4/4** | Word Ctrl+H 命中 MSAA；Notepad 另存为可达；受保护视图明确失败 |
| 无显示冒烟 | **10/10** | `smoke_no_display.py` |
| `dotnet build VScreenPilot.slnx` | **0 警告 / 0 错误** | — |

### 15.2 对首轮结论的修正

1. **“全屏截图 GDI 悬崖”归因不完整**。重构后服务端 `duration_ms` 只有 40–58ms；客户端看到的 1.2–1.9s 主要来自 **366KB PNG 的 Base64/JSON/stdio 回传**，不是 `BitBlt`。
   - 证据：同一会话 `inline=false`（落盘）32–52ms；`inline=true`（366KB）1.1–1.3s；`scale=0.25`（113KB）约 400ms。
   - 修复：截图像素路径复用 DC/DIB、去 `CAPTUREBLT`、默认 `scale=0.5`；超过 128KB 的图落到 artifact，只回 `resource_uri` + 一张 0.25× 预览。
2. **GUI-04 的 `0x80070003` 不是 UIA bug**。靶标 `Submit` 的 `File.WriteAllText(args[0])` 收到了相对路径，而进程工作目录是 exe 目录，导致 `DirectoryNotFoundException`。测试改为绝对路径后 GUI-04/08 通过。
3. **CONT-01 的“18/18 一致”原是空校验**。现在 schema 声明了真实字段并可被校验；旧的空 schema 不再算通过。
4. **SEC-08/SEC-09/REG-03/07/08/09/13/18 已修复**：非法 control_type、跨区域 baseline、参数类型、系统键变体、断言阈值、脚本 UTF-8、condition 字段、scale 边界均返回可操作错误。

### 15.3 仍然失败/未覆盖

| 项 | 状态 | 原因 |
| --- | --- | --- |
| GUI-12 视觉定位 | ✅ | 改用 `Submit` 按钮 + `target_path`，模板/颜色/像素 14/14 |
| REG-14 UWP Calculator | ✅ | `ApplicationFrameWindow` 已识别，UIA 从 frame 取树；ref 归属按 HWND 进程校验 |
| DIA-01/02/03 | ✅ | Word 对话框逐窗口回退 MSAA；Notepad 经典对话框在 UIA |
| CDP / OCR / WGC | 未实现 | 需要新的适配器/WinRT TFM/D3D11 依赖；见 06 |
| UIA 专用 MTA worker | 未实现 | 当前 `_sync` 串行 + 5s 超时 + ResetUia 退休队列；隔离性仍不如独立 worker |
| UIPI 端到端 / 安全桌面 | 只能检测 | Windows 设计上不可自动化，返回 E_UIPI / E_NO_INTERACTIVE_DESKTOP |
| 30min soak / CCW | 未覆盖 | 时间与环境限制（CCW 已移出主目标） |

### 15.4 复评（内部，不是认证）

| 维度 | 权重 | 首轮 | run6 | 依据 |
| --- | ---: | ---: | ---: | --- |
| 功能完整度 | 20 | 16 | 19 | 20 工具，snapshot/click_ref/MSAA/UWP/OCR 以外的兼容层 |
| 可靠性/生命周期 | 20 | 12 | 17 | 租约、epoch、每调用取消、Dispose 超时、Job 状态、UIA 退休队列 |
| MCP 协议与安全 | 15 | 10 | 14 | 官方 SDK、真 schema、递归参数校验、资源分页/预览、限流 |
| Windows 兼容性 | 15 | 11 | 14 | GUI 14/14、REG 19/19、DIA 4/4；CDP/OCR/WGC 未做 |
| 性能 | 10 | 5 | 9 | 区域 P95 25ms、全屏 P95 58ms、RSS 196MB |
| 测试/CI/可复现 | 10 | 7 | 9 | 31 单测 + SLO gate + hash 绑定 + nightly 自托管 |
| 分发/文档/社区 | 10 | 5 | 6 | npm/winget/Registry 清单；签名与 dnx 未完成 |
| **合计** | 100 | **66** | **88** | 内部复评，不是认证 |

### 15.5 二次修复（2026-11-14 晚）

| 问题 | 修复 | 验证 |
| --- | --- | --- |
| `snapshot` 大图路径在序列化后写入 | 先落盘/写 meta，再 `Text()`；并内联 0.25× 预览 | CONT-01 + 手工探针 |
| `snapshot.tree` schema 声明为 string | 改为 `tree:object`，加入 CONT-01 | 契约 5/5 |
| `click_ref` 只在 instructions 里存在 | 新工具：UIA pattern → 消息 → input 回退 | 手工探针 |
| `E_STALE_SNAPSHOT` 从不抛出 | ref 带 generation；保留上一代以返回该错误 | 手工探针 |
| `vsp://artifacts/...` 不可读 | 实现 `resources/list` + `resources/read`（Blob PNG） | 手工探针 |
| 显式 `save_path` 被 artifact 覆盖 | 保留 `path/requested_path`，新增 `artifact_path/artifact_uri` | 手工探针 |
| ref 注册表并发/跨快照 | `_refGate` 原子化 snapshot/tree；旧代保留一代 | 并发 + 手工 |
| 只读观察与 release 竞态 | `ReaderWriterLockSlim` read/write 租约，release 等观察排空 | CONC-04 |
| **`CapturePixel` 红蓝通道互换** | `GetPixel` 的 `0x00BBGGRR` 正确转 BGRA；`wait_for color`/pixel 恢复正确 | GUI-12 + 手工 |
| `held_ms` 恒为 0 | 改为毫秒差 | 单测 |
| `channel=pattern` 实际不用 pattern | `ValuePattern.SetValue`，失败 `E_PATTERN_UNSUPPORTED` | 代码复查 |
| 短 emoji 逐码元失败 | 含代理对自动走剪贴板 | 代码复查 |
| 消息拖拽假成功 | 明确拒绝，要求 `channel=input` | REG-15 |
| CI 不可执行 | publish + `smoke_no_display.py` + nightly 自托管 | smoke 10/10 |
| 文档夸大 2026-07-28 | 改为实测 2025-11-25 | PRT-02 |
| 多 profile 屏幕重叠 | `VSP_DISPLAY_OFFSET` + profile 稳定哈希偏移 | 代码复查 |
| 日志标题泄露/trace 无限增长 | 日志去标题；trace 7 天/50 份清理 | 代码复查 |

### 15.6 第三/四轮修复（run6，2026-11-15）

| 问题 | 修复 | 验证 |
| --- | --- | --- |
| R1 `get_ui_tree` 让 ref 失效 | 只有 `snapshot`/`new_snapshot=true` 推进代际；find/tree 返回 `snapshot_id` | CONT-06 |
| R2 release 抢不到锁仍清理 | 6s 抢不到写租约返回 `E_BUSY`；只有锁已卡 >5s 才旁路 | CONC-04 + 代码复查 |
| R3 嵌套参数静默回退 | 递归 schema 校验（type/range/enum/oneOf/additionalProperties） | CONT-07、单测 6 项 |
| R4/R11 save_path 双写/语义 | 指定 save_path 只写一个文件；inline=false 无预览 | CONT-08 |
| R5/R28 锁内截图/建树 | 建树、截图移出 `_refGate`；snapshot 串行提交 | 并发 + 手工 |
| R6 max_nodes 静默钳制 | schema 上限 1024；snapshot 返回 clamped/effective | CONT-10 |
| R7/R30 资源大 Blob/无分页 | list 带 size/next_cursor；>512KB 默认 preview，`?full=1` 取原图 | CONT-09 |
| R8 ToolResponse 共享节点 | structuredContent 独立副本 | 契约 + 单测 |
| R9 日志跨 profile | `server.{profile}-日期.log`、trace 文件名含 profile | 手工 |
| R10 profile 屏幕碰撞 | 扫描已挂载显示选空闲槽位 | 手工 |
| R12 预览二次截图 | 同一帧 BGRA 编码预览 | GUI-12 |
| R13 快照挤掉基线 | `storeFrame:false` | GUI-11 |
| R14 动作无后置验证 | `observed`（present/enabled/focused） | 代码 + GUI-08 |
| R15 FindAll 静默截断 | `truncated/total` | 代码 |
| R16 UIA 实例不释放 | IDisposable + 有界退休队列 | 代码 |
| R17 PNG 无 CRC | CRC32 校验，损坏模板拒绝 | 单测 |
| R18 模板无多尺度 | `scales` 参数 + 同位置去重 | GUI-12 |
| R19 滚轮子窗口/DPI | 发往 root ancestor 并按接收窗口转 DPI | 代码 |
| R20 AttachThreadInput 卡死 | WM_NULL 健康检查 120ms | 代码 |
| R21 误点关闭按钮 | 先 WM_CLOSE，失败才按文字找按钮 | REG-06 |
| R22 保存框只认中英文 | 按按钮 AutomationId 6/7/2 | 代码 |
| R23 run_script 无限制 | 危险命令黑名单 + 明确 sandbox=false | REG-09 |
| R24 allowlist 可绕过 | pid/window 接管也校验真实 exe | 代码 |
| R25 无限流/审计 | 令牌桶 + request_id + 参数摘要 | 代码 |
| R26 NoWarn CA1806 | 移除并逐处检查返回值 | 0 警告 |
| R27 get_environment 持锁 IO | 驱动/显示枚举移出 `_gate` | 代码 |
| R29 trace 无 request id | request_id + 脱敏参数摘要 | 代码 |
| UWP 不可达 | `ApplicationFrameWindow` + UIA 内容进程探测；ref 归属按 HWND | REG-14 |
| Word 自定义对话框 | 逐窗口 UIA→MSAA 回退，MSAA 默认动作/读值/设值 | DIA-01/02 |
| 证据未绑定哈希 | summary 自动写入 binary_sha256 | 7 组数据一致 |
| UWP release 强杀 frame host | 跟踪 `ContentPid/ContentExePath`；只杀内容进程，系统进程 denylist；内容路径不可用时拒绝强杀 | 手工：frame host 存活、CalculatorApp 退出 |
| UWP allowlist 校验错对象 | 白名单校验改用内容进程 exe；pid/window 接管同样生效 | 代码 + 手工 |
| `_snapshotGate` 无超时 | 5s 等不到返回 `E_BUSY`，不再无限排队 | 代码 |
| MSAA 嵌套 ref 冲突/无超时/COM 泄漏 | ordinal 路径 ref；2s 超时；遍历后释放子 RCW；child cap 报 `truncated` | DIA-02 |
| MSAA 不在 snapshot 里 | `snapshot.dialogs[].controls` 带 ref；`get_ui_tree(root=msaa)` 可展开 | 代码 |
| trace 缺 request_id/校验错误 | 响应 text/structured 都带 request_id；校验/限流/未知工具也写 trace | 手工探针 |
| 日志仍含窗口标题 | 等待稳定/更新日志去掉 title | 代码 |
| save_path 父目录 junction | 逐级解析父目录链接后再做根目录校验 | 代码 |
| artifact 文件名碰撞 | 文件名加 Guid 片段 | 代码 |
| ref key 不含 HWND | `ElementKey` 加入 hwnd；容量提升到 2048 | 代码 |
| 大资源无 preview | `resources/read` 对旧 artifact 现场生成 0.25× preview | 代码 |

> 结论：**R1–R30 + 复查发现的 UWP/MSAA/审计残余已全部处理**；GUI 14/14、REG 19/19、DIA 4/4、契约 10/10、截图 3/3、单测 31/31。仍缺 CDP/OCR/WGC 与 UIA 专用 worker、代码签名、dnx 入口；这些是新增子系统/外部渠道，不是本轮缺陷。

## 附录 A：CCW 12（历史记录，未完成）

> 目标应用后来从 CCW 12 换成 Word，以下为已发生的安装与观测证据，保留备查。

### A.1 安装与实测

### A.2 安装（CCW-01..05）

| 步骤 | 结果 | 记录 |
| --- | --- | --- |
| 介质 | ✅ | 5 个 RAR 分卷用 7-Zip 解压到 `C:\CCW12Media`：8.5GB / 917 文件；`Setup.exe` 签名 Valid（Rockwell Automation） |
| 前置依赖 | ⚠️ | 介质含 `DotNETFX_3.5SP1`、`DotNETFX_4.6.2`、`RSLinx`、`FactoryTalk`、`CodeMeter` 等；本轮未安装 |
| 核心 MSI | ⚠️ 部分 | `Connected Components Workbench 12.00.00.msi`（47.8MB）静默安装 17:14:43 启动；日志停在自定义动作 `CCWShell.Setup`（17:15:34），进程挂起，17:50 手动结束 |
| 产品注册 | ✅ | `Connected Components Workbench 12.00.00`，`C:\Program Files (x86)\Rockwell Automation\CCW` |
| 启动 | ❌ | `CCW.Shell.exe` 12.00.00.198 能启动，但弹出 **“Cannot find one or more components. Please reinstall the application.”**（见 `ccw/01-launch.png`） |
| 重启 | 无 | 未要求重启 |

**归因**：CCW 安装不完整（自定义动作被中断 + 依赖未装），属于**环境问题**，不是 MCP 缺陷。后续用 `/qb` 重试修复（结果见 9.4）。

### A.3 即使用当前状态，MCP 能做什么（CCW-C1..C11）

执行：`python tools/audit/bench_ccw.py`，5/6 项“工具调用成功”。

| 步骤 | 结果 | 观测 |
| --- | --- | --- |
| C1 启动并移动到虚拟屏 | ✅ | 21.4s；pid 1700；窗口 `Connected Components Workbench`；bounds `(1913,-7,1294,734)` |
| C2 环境 | ✅ | 虚拟屏 `DISPLAY137` 1280×720；单应用被接管 |
| C3 截图 | ✅ | 53KB PNG；画面为 CCW 的**错误模态框**（不是主界面） |
| C4 UIA 树 | ⚠️ | depth 3/5/8 都只有 **6 个节点**：Window / Button×2 / Image / Text / TitleBar；无菜单、无设备树、无编辑器 |
| C5 语义定位 | ❌ | `新建项目`、`New Project`、`设备`、`程序`、`生成`、`Build` 全部 `E_ELEMENT_NOT_FOUND` |
| C7 输入通道 | ⚠️ | `click(message)`、`click(input)`、`Alt+F` 都返回 ok，但没有任何可见状态变化；模态框未关闭 |
| C11 释放 | ✅ | `release` 548ms，关闭 pid 1700，摘屏，无残留 |

**关键证据**：`ccw/01-launch.png`、`ccw/02-after-input.png`、`ccw/ui-tree.json`。
> 注：脚本传入的是相对 `save_path`，服务端按自己的工作目录（`artifacts/server`）落盘，实际文件在 `artifacts/server/tools/audit/results/run1/ccw/`；这本身是一个易踩的路径语义问题（改进建议 P2-11）。

### A.4 阻断分层

| 层 | 证据 | 结论 |
| --- | --- | --- |
| 环境 | 安装不完整，主程序报缺组件 | 必须先完成安装，否则无法建项目 |
| 应用 | CefSharp/Chromium 区域不暴露 UIA；CCW 主界面未加载 | 即使装好，UIA 路径仍大概率只有 `Custom` |
| 工具 | 无 CDP/IAccessible2/OCR 兜底；不检测模态框；`E_INPUT_UNSUPPORTED` 只在消息被拒绝时返回 | 无法把“点了没反应”变成可操作提示 |

**CCW 结论：C 档（不可用）**，且“不可用”的首要原因是安装未完成；其次是 Chromium 兼容性；不能把全部责任归给 VScreenPilot。

### A.5 修复尝试与后续

- 17:5x 用 `msiexec /i <msi> /qb` 重试修复（需要 UAC），**UAC 被取消**，未产生 `C:\CCW12Media\ccw-repair.log`，安装状态未改变。
- 后续建议改用原始 `Setup.exe` 引导安装（会处理 RSLinx/FactoryTalk/CodeMeter 依赖），预计 1–2 小时并可能重启；仅重跑核心 MSI 很可能仍卡在 `CCWShell.Setup`。
- 完整安装后，建议按测试计划 C6–C10 重跑：新建 Micro850 项目、添加 ST 程序、写入起保停、保存 `StartStop.ccwsln`、编译并截图核对。验收脚本 `tools/audit/bench_ccw.py` 可直接复用（把 `C5` 的控件名改成实际语言版本）。

**安装器对 MCP 的启示**：CCW 的错误模态框是本轮“工具能点、但不理解界面”的典型样本——如果 `launch_or_place_app` 能返回 `dialogs[]`，AI 至少能知道“当前是错误框，不是主界面”。

---



**结论**：CCW 12 因安装不完整 + CefSharp 无 UIA，未能完成建项/编译；本轮改用 Word 作为 AI 实践靶标。
