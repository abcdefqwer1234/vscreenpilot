# VScreenPilot

把 Windows 桌面应用放到一块**用完即走、看不见的虚拟屏**上，让 AI 通过 MCP 工具完成截图、点击、输入、语义定位和结果验证。你在主屏继续工作，它不抢鼠标、不挡窗口、不误点你的桌面。

- 对外：一个 MCP Server，18 个工具。
- 对内：自动挂载 1280×720 虚拟屏、启动/接管应用、归位、清理、记日志。
- 运行期不需要管理员权限；首次安装一次 Parsec VDD 即可。

> 版本 0.1.0（M4.1）。实现细节见 [技术文档.md](技术文档.md)，需求契约见 [需求文件.md](需求文件.md)。

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
| `capture_screen` | 截虚拟屏或指定区域，返回 PNG + `frame_id` + 区域元数据 |
| `get_ui_tree` | 读取裁剪后的语义控件树，节点带 `element_ref` |
| `find_element` | 按名称/类型/AutomationId/角色查找控件，返回 `element_ref` |
| `get_element_text` | 读正文：优先 TextPattern（Word），其次 ValuePattern |
| `match_visual` | 模板/颜色/单点像素定位 |

### 操作

| 工具 | 用途 |
| --- | --- |
| `click` | 坐标点击；默认 UIA/窗口消息，不动光标 |
| `gesture` | 拖拽、滚轮、移动 |
| `type_text` | 向焦点输入文本，支持中文；分块回读确认 |
| `press_keys` | 组合键；支持 `sequence` 连续菜单导航 |
| `invoke_element` | 按 `element_ref` 触发控件（Invoke/Toggle/Select/Expand/Legacy） |
| `set_element_value` | 按 `element_ref` 写值；支持 append/insert/replace |
| `launch_or_place_app` | 启动/接管应用，移动到虚拟屏并最大化 |
| `release_environment` | 归还窗口、关闭应用、按需摘屏 |
| `run_script` | 兜底 PowerShell；**默认关闭** |

### 验证

| 工具 | 用途 |
| --- | --- |
| `wait_for` | 等元素/颜色/窗口/像素变化/视觉匹配，减少客户端轮询 |
| `assert_region` | 区域颜色覆盖率、是否变化、与指定帧比较 |
| `get_log` | 读内存日志或文件日志，含最后错误 |

---

## 怎么工作

```text
OpenCode / MCP 客户端
        │ JSON-RPC over stdio
        ▼
vscreenpilot（npm 启动器）
        │ 平台检查 + 透传 stdio
        ▼
VScreenPilot.exe
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
- 从源码构建需要 .NET 10 SDK；使用 npm 包只需 Node.js ≥ 18。

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

### 方式 A：直接用发布产物

```powershell
dotnet publish src\VScreenPilot.Server\VScreenPilot.Server.csproj `
  -c Release -r win-x64 -o .\artifacts\server
```

得到 `artifacts\server\VScreenPilot.exe`。

### 方式 B：构建 npm 单包

```powershell
.\scripts\build-npm.ps1
```

得到 `artifacts\npm\vscreenpilot-0.1.0.tgz`。

当前包尚未发布到 npm registry；本地验证用：

```powershell
npm install .\artifacts\npm\vscreenpilot-0.1.0.tgz
```

发布后可用：

```powershell
npx -y vscreenpilot@0.1.0
```

> npm 包声明了 `os:["win32"]`、`cpu:["x64"]`，非 Windows 平台会在 `npm install` 阶段直接 `EBADPLATFORM`。

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
- `--allow-script` 或 `VSP_ALLOW_SCRIPT=1`：启用 `run_script`。默认关闭，避免任意 PowerShell。
- `VSP_APP_ALLOWLIST`：可选，用分号分隔目录，限制 `launch_or_place_app` 通过 `path` 能启动哪些 exe；按 `window` 标题接管不受此限制。
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

`keep_foreground=true` 会暂停焦点护栏且不归还前台，适合连续菜单操作；操作完成后调用 `type_text`/`press_keys` 或 `release_environment` 即可恢复焦点护栏。

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

`target_path` 必须位于当前工作目录或系统临时目录下；模板必须来自 1:1 截图，同 DPI、无缩放/旋转。

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
- **不覆盖用户文件**：截图/模板落盘只允许工作目录或系统临时目录下的 `.png`。

---

## 运行期行为

- 启动时后台预热挂屏，第一次黑屏发生在 OpenCode 启动阶段。
- 虚拟屏固定 1280×720@60、DPI 96、主屏右侧。
- 鼠标默认不动；鼠标只有 `mode=input` 才用 `SendInput`，键盘输入（`type_text`/`press_keys`）也使用 `SendInput`，但不移动光标。
- 焦点护栏每 200ms 检查：本会话应用意外占前台时归还用户窗口；菜单操作可暂停。
- 光标护栏每 150ms 检查：光标误入虚拟屏时弹回最近物理屏。
- EOF、Ctrl+C、控制台关闭、SIGTERM、父进程退出都会进入统一清理。
- 强杀后驱动心跳停止约 1 秒自动摘屏；如仍有残留，下次启动会按 `state.json` 接管孤儿屏并还原窗口。
- 日志：内存 500 条 + 文件按天/8MB 轮转保留 3 份，14 天清理。

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
- 纯单元测试尚未覆盖 PNG/KeyMap/region/ref 注册/协议分帧，当前以集成脚本验收为主。

---

## 项目结构

```text
src/VScreenPilot.Core/          VDD IOCTL、显示拓扑、DPI
src/VScreenPilot.Server/        MCP Server、生命周期、工具、UIA、输入、视觉
npm/                            npm 单包（启动器 + exe + 驱动脚本 + LICENSE）
scripts/                        驱动安装、npm 构建
tools/                          VddProbe、AotSmokeTest、验收脚本
artifacts/server/               发布 exe
artifacts/npm/                  npm tgz
技术文档.md                     代码级实现说明
需求文件.md                     需求契约
docs/mcp-config.example.jsonc  OpenCode 接入示例
```

## 验收脚本

```powershell
node tools\mcp-smoke.mjs .\artifacts\server\VScreenPilot.exe full
node tools\m1-errors.mjs .\artifacts\server\VScreenPilot.exe
node tools\m1-cancel.mjs .\artifacts\server\VScreenPilot.exe
node tools\m1-keep.mjs .\artifacts\server\VScreenPilot.exe
node tools\m1-heal.mjs .\artifacts\server\VScreenPilot.exe
node tools\m1-lifecycle.mjs .\artifacts\server\VScreenPilot.exe eof
node tools\m1-lifecycle.mjs .\artifacts\server\VScreenPilot.exe kill
node tools\m1-lifecycle.mjs .\artifacts\server\VScreenPilot.exe mutex
node tools\m2-semantic.mjs .\artifacts\server\VScreenPilot.exe
node tools\m2-visual.mjs .\artifacts\server\VScreenPilot.exe
node tools\m2-1-review.mjs .\artifacts\server\VScreenPilot.exe
node tools\m3-verify.mjs .\artifacts\server\VScreenPilot.exe
node tools\m3-fix-verify.mjs .\artifacts\server\VScreenPilot.exe
node tools\m4-package-verify.mjs
node tools\m4-stress.mjs .\artifacts\server\VScreenPilot.exe 50
node tools\m4-selfheal.mjs .\artifacts\server\VScreenPilot.exe 3
node tools\m4-log-verify.mjs .\artifacts\server\VScreenPilot.exe
node tools\m4-parent-watch.mjs .\npm\bin\vscreenpilot.js
```

需要 `run_script` 的验收脚本（`mcp-smoke`、`m1-*`、`m2-*`、`m3-*`、`m4-selfheal`）会在启动服务端时附加 `--allow-script`；`m4-package-verify` 故意不加，用于验证产品默认关闭；`m4-stress`、`m4-log-verify`、`m4-parent-watch` 不需要脚本。

---

## 许可证

MIT，见 [LICENSE](LICENSE)。
