# VScreenPilot

把 Windows 桌面应用放到一块**用完即走、看不见的虚拟屏**上，让 AI 通过 MCP 工具完成截图、点击、输入、语义定位和结果验证。你在主屏继续工作，它不抢鼠标、不挡窗口、不误点你的桌面。

- 对外：一个 MCP Server，22 个工具（含 `snapshot` / `click_ref` / `read_screen_text` / `click_text`）。
- 对内：自动挂载 1280×720 虚拟屏、启动/接管应用、归位、清理、记日志。
- 运行期不需要管理员权限；首次安装一次 Parsec VDD 即可。
- 语义后端：UIA 为主，MSAA 兜底（Office 旧对话框），OCR 兜底（画布型应用）；支持 UWP `ApplicationFrameWindow`。
- 共享实例与 as-built 细节见 [`技术文档.md`](技术文档.md)、[`docs/architecture.md`](docs/architecture.md)；行为变化与错误目录见 [`docs/changes-2026-11.md`](docs/changes-2026-11.md)、[`docs/errors.md`](docs/errors.md)。
- 测试与复现见 [`docs/audit/第一轮测试报告.md`](docs/audit/第一轮测试报告.md)（Word/CCW 与重构）与 [`docs/audit/第二轮测试报告.md`](docs/audit/第二轮测试报告.md)（PPT 与修复）；原始记录已汇总进这两份报告。

> 版本 0.2.2（多客户端共享实例）。这是 **0.x 技术预览**：工具名与参数已按契约冻结，但 0.x 阶段仍可能做不兼容调整；升级前请看 [CHANGELOG](CHANGELOG.md)。
> 0.2.2 的 as-built 以 [技术文档.md](技术文档.md) 和 [architecture](docs/architecture.md) 为准；[技术文档.md](技术文档.md) 已更新到 0.2.2，0.1.0 的实现细节查 git 历史。需求契约见 [需求文件.md](需求文件.md)。

---

## 它解决什么问题

传统桌面自动化的三个麻烦：

1. **会干扰你**：鼠标被抢、窗口被切、输入打到你的文档里。
2. **要手动准备**：先开虚拟屏、拖窗口、调分辨率，结束后还要收尾。
3. **会留垃圾**：虚拟屏没关、应用进程残留、窗口位置被改。

VScreenPilot 的做法：

- 应用跑在**虚拟屏**上，物理屏不被遮挡；
- 鼠标默认**不移动**：优先 UIA 语义动作，其次窗口消息，只有显式 `mode=input` 才用 `SendInput`；
- 键盘输入前强制切前台，切不过去就报错，**绝不盲打**；
- 应用默认用独立实例/新窗口，不征用你已开的窗口；
- 宿主退出时自动归还窗口、关闭本会话应用、摘除虚拟屏；会话内 `release_environment` 默认只归还/关闭，保留虚拟屏；
- 驱动心跳停止约 1 秒后自动回收虚拟屏。

---

## 能做什么

### 观察

| 工具 | 用途 |
| --- | --- |
| `get_environment` | 驱动、虚拟屏、主屏、前台、光标、焦点控件、已接管应用 |
| `snapshot` | 一次返回语义树 + ref + 窗口/对话框/前台 + 可选标注截图 |
| `capture_screen` | 截虚拟屏或指定区域，返回 PNG + `frame_id` + 区域元数据 |
| `read_screen_text` | OCR 读取屏幕/区域文本（画布型应用兜底） |
| `get_ui_tree` | 读取裁剪后的语义控件树，节点带 `element_ref` |
| `find_element` | 按名称/类型/AutomationId/角色查找控件，返回 `element_ref` |
| `get_element_text` | 读正文：优先 TextPattern（Word），其次 ValuePattern |
| `match_visual` | 模板/颜色/单点像素定位 |

### 操作

| 工具 | 用途 |
| --- | --- |
| `click` | 坐标点击；默认 UIA/窗口消息，不动光标；返回 `observed.changed` |
| `click_ref` | 按 `element_ref` 点击，优先 UIA pattern |
| `click_text` | OCR 定位文本并点击（画布型应用兜底） |
| `gesture` | 拖拽、滚轮、移动 |
| `type_text` | 向焦点输入文本，支持中文；分块回读确认 |
| `press_keys` | 组合键；支持 `sequence` 连续菜单导航 |
| `invoke_element` | 按 `element_ref` 触发控件（Invoke/Toggle/Select/Expand/Legacy） |
| `set_element_value` | 按 `element_ref` 写值；支持 append/insert/replace |
| `launch_or_place_app` | 启动/接管应用，移动到虚拟屏并最大化 |
| `release_environment` | 归还窗口、关闭应用、按需摘屏；默认 `abort`，强杀需 `allow_data_loss=true` |
| `run_script` | 兜底 PowerShell；**默认关闭** |

### 验证

| 工具 | 用途 |
| --- | --- |
| `wait_for` | 等元素/颜色/窗口/像素变化/视觉匹配/文本，减少客户端轮询 |
| `assert_region` | 区域颜色覆盖率、是否变化、与指定帧比较 |
| `get_log` | 读内存日志或文件日志，含最后错误 |
| `--cli` | 同一 exe 的 CLI 入口：`--cli env|snapshot|screen` 输出纯 JSON |

---

## 怎么工作

```text
OpenCode / MCP 客户端
        │ JSON-RPC over stdio
        ▼
VScreenPilot.exe（安装版 / 绿色版）
        ├─ 自动挂载 1280×720 虚拟屏（主屏右侧）
        ├─ 启动/接管应用并最大化到虚拟屏
        ├─ 截图 / 语义树 / 元素引用 / 输入 / 验证
        ├─ 焦点护栏：本会话应用抢前台时自动归还
        ├─ 光标护栏：光标误入虚拟屏时弹回物理屏
        └─ 退出时归还窗口、关闭应用、摘屏
```

虚拟屏默认在**整个 OpenCode 会话期间常驻**，避免每次调试都黑屏一次。需要立即摘屏时调用：

```json
{ "keep_display": false }
```

---

## 环境要求

- Windows 10/11 x64，单用户交互式会话。
- 首次使用安装一次 Parsec VDD（一次 UAC，无需重启）。
- 运行 VScreenPilot 本身**不需要管理员权限**。
- 从源码构建需要 .NET 10 SDK。

### 安装驱动

```powershell
.\scripts\install-driver.ps1
```

或手动：

```powershell
winget install --id Parsec.ParsecVDD -e --accept-package-agreements --accept-source-agreements
```

只检查不安装：

```powershell
.\scripts\install-driver.ps1 -CheckOnly
```

---

## 安装 VScreenPilot

### 方式 A：安装版（推荐）

从 Releases 下载 `VScreenPilot-Setup-0.2.2.exe` 并双击安装：

- 安装到 `%LOCALAPPDATA%\Programs\VScreenPilot`，**per-user，不需要管理员**；
- 可选加入当前用户 `PATH`（默认勾选），之后可直接运行 `vscreenpilot --doctor`；
- 卸载走“设置 → 应用”，日志与截图数据默认保留；
- 静默安装参数与 winget 一致：`/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-`。

> 安装包当前未签名，SmartScreen 会提示“未知发布者”；请先核对 Release 里的 SHA256。

### 方式 B：绿色版

下载 `VScreenPilot-win-x64-v0.2.2.zip`，解压到固定目录（如 `C:\Tools\VScreenPilot`），直接运行 `VScreenPilot.exe`。

zip 内含 exe、`LICENSE`、`THIRD-PARTY-NOTICES.md`、`README.md`；`.pdb` 在单独的 `VScreenPilot-symbols-v0.2.2.zip`，普通用户不需要。

### 方式 C：winget（提交审核通过后可用）

```powershell
winget install --id VScreenPilot.VScreenPilot -e
```

当前清单已提交/待审核；审核通过前请用方式 A 或 B。winget 安装的就是 per-user 安装包，卸载、升级走 winget 或“设置 → 应用”。

### 从源码构建

```powershell
.\scripts\release.ps1 -Version 0.2.2
```

一次生成绿色 zip、安装包和 symbols zip。

### 诊断

```powershell
VScreenPilot.exe --version
VScreenPilot.exe --help
VScreenPilot.exe --doctor
```

`--doctor` 输出 JSON，包含驱动状态、虚拟屏、显示器、日志路径、状态文件、内存/句柄等；**驱动未就绪时 exit 1**。

---

## 接入 OpenCode

把下面片段合并到 OpenCode 配置的 `mcp.servers`：

```jsonc
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "servers": {
      "vscreenpilot": {
        "type": "local",
        "command": ["E:\\path\\to\\VScreenPilot.exe"],
        // 需要 run_script 时改成：
        // "command": ["E:\\path\\to\\VScreenPilot.exe", "--allow-script"],
        "codemode": false,
        "environment": {
          // 可选：只允许启动这些目录下的 exe
          // "VSP_APP_ALLOWLIST": "C:\\Program Files\\MyApp;D:\\Tools"
        },
        "timeout": {
          "startup": 120000,
          "catalog": 60000,
          "execution": 600000
        }
      }
    }
  }
}
```

说明：

- `codemode: false`：让 `capture_screen` 返回的图片直接进入模型上下文。
- **多工作目录/多客户端**：第一个 MCP 进程会优先启动独立 `--daemon`（带 Job breakaway；失败回退进程内 daemon），后续目录的进程自动成为瘦代理；不需要为每个目录配置 `--profile`。客户端断开后会释放屏幕租约并在空闲后清理自己的窗口，daemon 在 `VSP_DAEMON_IDLE_MS` 后退出。旧 0.1.0 实例没有共享管道，升级后先关闭旧实例一次。
- `get_environment.session.clients[]` 只返回 id/name/connected_at/holds_lease；`session.screen_lease` 给出当前租约。`get_log` 默认只返回本客户端日志，`include_other_clients=true` 可查看全部。
- `--allow-script` 或 `VSP_ALLOW_SCRIPT=1`：启用 `run_script`。默认关闭，避免任意 PowerShell。
- `VSP_APP_ALLOWLIST`：可选，用分号分隔目录，限制 `launch_or_place_app` 通过 `path` 能启动哪些 exe；按 `window` 标题接管不受此限制。
- `VSP_DISPLAY_WIDTH` / `VSP_DISPLAY_HEIGHT` / `VSP_DISPLAY_REFRESH`：可选，修改虚拟屏分辨率与刷新率，默认 1280×720@60（范围 800–3840 / 600–2160 / 30–240）。这些值在 daemon 启动时读取；daemon 已运行时改环境变量不会生效，需先 `--cli stop` 或关闭客户端。改分辨率不会改变 96 DPI（100% 缩放）。
- `VSP_DAEMON_IDLE_MS`：最后一个客户端断开后的空闲宽限，默认 5000；`0` 立即退出。调大会让虚拟屏在空闲期继续挂在真实桌面上（退出时才摘屏），按需取舍。`VSP_SCREEN_LEASE_WAIT_MS`、`VSP_TOTAL_BUFFER_MB`、`VSP_CLIENT_LIMIT` 分别控制租约等待、总缓冲和客户端上限。
- `VSP_INPUT_AUTOFALLBACK=1`：允许 `click`/`click_ref`/`click_text` 在 UIA/窗口消息失败后自动降级到 `SendInput`（会移动真实光标，默认关闭）。
- `VSP_CLIENT_CWD`：可选，覆盖客户端路径基准（`save_path`/`target_path`/相对 exe）；默认使用 proxy 的 cwd。
- `--cli env|snapshot|screen|status|stop`：`env/snapshot/screen` 在实例存在时通过只读控制管道查询；`stop` 有活跃客户端时需 `--force`。`snapshot` 返回管理员 `apps[]` 视图（同时保留旧顶层字段）。
- `--daemon`：高级用法，手动启动独立共享实例；通常不需要使用，首个 MCP 客户端会自动 spawn。
- 完整示例见 [docs/mcp-config.example.jsonc](docs/mcp-config.example.jsonc)。

---

## 快速上手

一次典型调试：

```text
1. get_environment                 看驱动/虚拟屏/主屏
2. launch_or_place_app             启动记事本、Edge、Word 等
3. capture_screen                  看画面
4. find_element / get_ui_tree      拿 element_ref
5. invoke_element / set_element_value / type_text / click
6. wait_for / assert_region        验证结果
7. release_environment             归还窗口；默认保留虚拟屏
```

> 下例坐标假设主屏宽 3072、虚拟屏从 `(3072, 0)` 开始。实际使用前先看 `get_environment.virtual_display` 的 `x/y`，把所有坐标/区域替换成虚拟屏绝对坐标；不能直接使用主屏的 `(0,0)`。

### 例子：打开记事本并输入

```json
{ "path": "C:\\Windows\\System32\\notepad.exe", "wait_ms": 15000 }
```

```json
{ "text": "Hello, 虚拟屏！" }
```

```json
{ "ref": "e1", "value": "第一行\n第二行", "mode": "replace", "allow_destructive": true, "allow_keyboard": true }
```

### 例子：点击菜单并保持打开

```json
{ "x": 3192, "y": 80, "keep_foreground": true }
```

```json
{ "sequence": ["Down", "Down", "Enter"], "keep_foreground": true }
```

`keep_foreground=true` 会暂停焦点护栏 30 秒且不立即归还前台，适合连续菜单操作；到期会自动恢复，也可调用 `type_text`/`press_keys` 或 `release_environment` 提前恢复。

### 例子：语义定位 Word 正文

```json
{ "role": "document_body", "prefer": "focused" }
```

```json
{ "ref": "e3", "value": "追加一段", "mode": "append", "allow_keyboard": true }
```

```json
{ "ref": "e3", "max_chars": 4096 }
```

### 例子：视觉定位

```json
{ "mode": "color", "target": "#FF0000" }
```

```json
{ "mode": "template", "target_path": ".\\button.png", "threshold": 0.9 }
```

`target_path` 必须位于当前工作目录或系统临时目录下；模板按捕获时的像素比例匹配，要求同一渲染缩放/DPI、无旋转。若模板来自不同缩放或 DPI，传 `scales`（如 `[0.75, 1, 1.25]`，范围 0.5–2.0，默认 `[1]`）搜索。

### 例子：验证区域变化

先对同一区域截图建立基线（`capture_screen` 参数）：

```json
{ "region": { "x": 3072, "y": 0, "width": 400, "height": 200 } }
```

再做断言（`assert_region` 参数）：

```json
{ "region": { "x": 3072, "y": 0, "width": 400, "height": 200 }, "expectation": { "changed": true } }
```

`assert_region changed` 默认引用同区域最近一次 `capture_screen`；也可传 `baseline_frame_id`。

### 例子：结束调试

```json
{ "keep_display": true, "on_dirty": "discard" }
```

- `keep_display=true`：保留虚拟屏，下一次调试不再黑屏。
- `on_dirty=force/discard/save/abort`：未保存内容处理。
- 想立即摘屏：`{ "keep_display": false }`。

---

## 安全边界

- **只在虚拟屏内操作**：截图、断言、点击、拖拽、滚轮、移动都会被限制在虚拟屏矩形内，越界返回 `E_OUT_OF_BOUNDS`。
- **只操作本会话应用**：落点窗口必须属于已接管的应用，否则 `E_FOREIGN_WINDOW`。
- **不盲打**：键盘输入前强制切前台，切换失败返回 `E_FOCUS`；输入中前台丢失返回 `E_FOCUS_LOST`。
- **不泄露你的主屏**：`get_environment.foreground` 只在本会话窗口位于前台时返回标题/PID，用户主屏窗口只返回 `tracked:false`。
- **不静默覆盖内容**：`replace` 或覆盖非空 `insert` 需要显式 `allow_destructive=true`。
- **不默认执行脚本**：`run_script` 需要显式开启；开启后以当前用户权限运行，**没有沙箱**。
- **不误杀共享进程**：优先只关窗口；专用实例，或本会话启动且没有其他窗口的进程，才强杀；窗口已关闭且需要清理的后台进程（如 Edge 临时配置）按 exe 路径核对，避免 PID 复用误杀。
- **不覆盖用户文件**：截图/模板落盘只允许工作目录或系统临时目录下的 `.png`，且 `capture_screen` 不覆盖已有文件。
- **不发送系统级快捷键**：`Win+*`、`Alt+Tab`、`Ctrl+Esc`、`Ctrl+Shift+Esc` 等默认拒绝；确需使用显式传 `allow_system_keys=true`。
- **不假装消息输入成功**：Chromium/Electron/WinUI 会忽略窗口消息点击/拖拽/滚轮，服务端直接返回 `E_INPUT_UNSUPPORTED`。`click`/`click_ref`/`click_text` 可显式传 `channel=input` 或 `fallback_input=true`，`gesture` 用 `mode=input`；也可用 `VSP_INPUT_AUTOFALLBACK=1` 全局开启输入降级。
- **不是安全沙箱**：虚拟屏与用户共享同一 Windows 会话。`session.isolation=shared_desktop` 是明确提示；处理不可信网页/文档/脚本时，请使用 VM、Windows Sandbox 或独立 Windows 账户。

---

## 运行期行为

- 启动时后台预热挂屏，第一次黑屏发生在 OpenCode 启动阶段。
- 虚拟屏默认 1280×720@60、DPI 96、主屏右侧；可用 `VSP_DISPLAY_WIDTH/HEIGHT/REFRESH` 覆盖。DPI 目前固定 96，分辨率变大只增加工作区、不改变缩放比例。
- 单实例应用（如 Win11 记事本）无法创建独立窗口时会自动接管现有窗口：响应里 `reused=true` 且 `warnings[]` 会说明；`release_environment` 只归位、不关闭该窗口。Win11 记事本无参数启动还可能恢复上次会话的标签，先 `snapshot` 确认标题，或传 `args` 指定文件。
- 鼠标默认不动；只有 `mode=input`、`channel=input`、`fallback_input=true` 或 `VSP_INPUT_AUTOFALLBACK=1` 才用 `SendInput`；键盘输入（`type_text`/`press_keys`）也使用 `SendInput`，但不移动光标。
- ref 只在创建它的 `snapshot` 内有效；跨快照使用返回 `E_STALE_SNAPSHOT`。轮询状态用 `get_ui_tree`（默认复用当前代），需要新 ref 时传 `new_snapshot=true`。
- 只操作虚拟屏内、本会话接管的窗口；物理屏上的用户窗口不会被接管（隔离设计）。虚拟屏上限 3840×2160，坐标边界随 `VSP_DISPLAY_WIDTH/HEIGHT` 变化。
- 焦点护栏每 200ms 检查：本会话应用意外占前台时归还用户窗口；菜单操作可暂停。
- 光标护栏每 150ms 检查：光标误入虚拟屏时弹回最近物理屏。
- EOF、Ctrl+C、控制台关闭、SIGTERM、父进程退出都会进入统一清理。
- 强杀后驱动心跳停止约 1 秒自动摘屏；如仍有残留，下次启动会按 `state.json` 接管孤儿屏并还原窗口。
- 日志：内存 500 条 + 文件按天/8MB 轮转保留 3 份，14 天清理。
- 截图产物：落在 `%LOCALAPPDATA%\VScreenPilot\artifacts\<profile>\<会话目录>`，默认保留 7 天；用 `VSP_ARTIFACT_RETENTION_DAYS` 调整，设为 `0` 关闭清理。画面可能含敏感内容，请在共享机器上按需调低或关闭保留。

---

## 性能

最终 50 轮压测（M4.1）：

| 指标 | P95 |
| --- | ---: |
| 启动握手 | 116ms |
| 挂屏 | 1952ms |
| 截图 | 30ms |
| release | 2098ms |
| EOF 退出 | 229ms |

- 50/50 轮通过，总耗时 374.6s。
- 峰值工作集 65.8MB；release 后工作集 1.1MB（清空工作集读数）。
- 历史实测：截图 26–41ms，消息点击总耗时 68–132ms，空闲约 36.6MB。

---

## 常见问题

**`--doctor` 显示驱动未就绪**
先运行 `scripts\install-driver.ps1`；仍失败请看设备管理器是否有 `Parsec Virtual Display Adapter`，以及 `driver.status` 的具体值。

**检测到孤儿虚拟屏 `E_ORPHAN_DISPLAY`**
说明有一块虚拟屏挂载着，但没有本会话的记录。等待 2 秒让驱动自动回收；仍存在时重启驱动或手动摘除。

**截图/点击报 `E_OUT_OF_BOUNDS`**
坐标必须使用虚拟桌面绝对坐标，并完全落在虚拟屏内。先用 `get_environment.virtual_display` 看 `x/y/width/height`。

**`E_FOREIGN_WINDOW`**
落点窗口不是本会话接管的窗口。先 `launch_or_place_app`，或检查是否点到了用户主屏/其他应用。

**键盘输入报 `E_FOCUS`**
目标窗口无法切到前台。检查窗口是否最小化、是否被其他窗口挡住、是否仍在虚拟屏内。

**`E_FOCUS_LOST`**
输入过程中用户或其他应用抢走了前台，服务端已中止，避免继续打到别处。重试即可。

**`E_ELEMENT_STALE`**
控件已销毁或无法唯一重定位。重新 `get_ui_tree` / `find_element` 获取新的 `element_ref`。

**`E_DESTRUCTIVE_REQUIRED`**
要覆盖非空内容。确认后传 `allow_destructive=true`；或改用 `append`。

**`E_PATTERN_UNSUPPORTED`**
控件没有对应 UIA 模式。可以尝试 `invoke_element { allow_click:true }`，或 `set_element_value { allow_keyboard:true }`。

**消息点击/滚轮没反应**
Electron/Chromium 可能忽略窗口消息。用 `wait_for`/`assert_region` 验证；必要时显式 `mode="input"`（会移动光标）。

**`run_script` 返回 `E_DISABLED`**
默认关闭。启动服务端时加 `--allow-script`，或设置 `VSP_ALLOW_SCRIPT=1`。

**非 Windows 安装报 `EBADPLATFORM`**
这是预期行为：包只支持 Windows x64。

---

## 当前限制

- 只支持 Windows 10/11 x64。
- 首次挂载/宿主退出各黑屏一次，无法完全避免。
- 无法操作管理员权限窗口（UIPI）。
- 无法语义化所有自绘/WebView 控件（如 CCW 的 ag-grid 变量表）。
- Word 在 1280×720 下功能区默认折叠，需要 `Ctrl+F1`；默认树深度看不到正文/功能区。
- 消息注入可能被 Electron/Chromium 忽略。
- `on_dirty=save/discard` 目前识别中英文保存对话框。
- 没有 TextPattern/ValuePattern 的应用无法精确回读输入。
- 单元测试覆盖 PNG/KeyMap/参数校验/错误目录/Profile/JobObject/截图保留策略；UIA、输入注入与真实窗口仍需集成脚本验收。

---

## 项目结构

```text
src/VScreenPilot.Abstractions/ 错误、JSON、视图、选项、BuildInfo、Metrics
src/VScreenPilot.Platform/     Win32/COM、显示、VDD、窗口、输入、进程、完整性校验
src/VScreenPilot.Automation/   UIA/MSAA、截图、PNG、OCR、模板匹配、脚本
src/VScreenPilot.Server/       MCP、工具层、会话、服务、CLI、IPC、诊断
tests/                         单元测试与契约/架构测试
scripts/                       驱动安装、发布与安装包构建
packaging/                     Inno Setup 与 winget 清单
tools/                         VddProbe、AotSmokeTest、audit 审计套件
artifacts/server/               发布 exe
artifacts/release/              绿色 zip、安装包、symbols
技术文档.md                     0.2.2 综合技术手册（机制、共享实例、重构史）
需求文件.md                     需求契约
docs/architecture.md           分层与数据流（含自动度量）
docs/changes-2026-11.md        重构变更摘要
docs/errors.md                 错误目录
docs/mcp-config.example.jsonc  OpenCode 接入示例
docs/releasing.md              发布手册（GitHub Release + winget）
```

## 测试脚本

```powershell
dotnet publish src\VScreenPilot.Server\VScreenPilot.Server.csproj -c Release -o artifacts\server
node tools\TestTool\smoke-v2.mjs
node tools\TestTool\share-smoke.mjs
node tools\TestTool\daemon-smoke.mjs
node tools\TestTool\run-all.mjs --arm A --task all --runs 3
node tools\TestTool\run-all.mjs --stability --sessions 50
node tools\TestTool\run-all.mjs --report
```

`smoke-v2.mjs` 不挂屏、不注入输入，可在普通桌面运行；`run-all.mjs` 含 GUI 与输入用例，必须在隔离 VM 或独立账户中执行。详见 `tools/TestTool/README.md`。

---

## 许可证

MIT，见 [LICENSE](LICENSE)。随包第三方组件与 Parsec VDD 的许可说明见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
