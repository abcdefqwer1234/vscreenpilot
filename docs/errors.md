# 错误码目录

> 所有对外错误都包含 `code`、`message`、`details`、`retryable`、`stage`、`hint`、`docs`、`request_id`。
> `docs` 是本文件的完整 HTTPS 锚点（可用 `VSP_DOCS_BASE_URL` 覆盖）。错误码的唯一事实来源是 `src/VScreenPilot.Abstractions/Errors/ErrorCatalog.cs`，
> 单元测试会检查每个码都有可操作的 hint/summary/stage。

## 参数与目标

| 错误码 | 含义 | 下一步 |
| --- | --- | --- |
| `E_INVALID_ARG` | 参数类型/范围/枚举不合法，或出现未声明字段 | 看 `details.field` 与 `details.allowed/supported`；不会静默使用默认值 |
| `E_UNKNOWN_TOOL` | 工具名不在 `tools/list` 中 | 检查拼写和大小写；先调用 `tools/list` |
| `E_OUT_OF_BOUNDS` | 坐标不在本会话虚拟屏内 | 先 `get_environment` 读取 `virtual_display` 的 x/y/width/height |
| `E_FOREIGN_WINDOW` | 目标窗口不属于本会话 | 先 `launch_or_place_app`（可用 `pid` 精确接管） |
| `E_NO_TARGET` | 没有存活的目标应用 | 启动或重新接管；`snapshot` 可查看 `windows[]` |
| `E_WINDOW_NOT_FOUND` | 按标题/pid 找不到窗口 | 改用 `path` 启动，或确认窗口已出现 |
| `E_WINDOW_TIMEOUT` | 等待主窗口超时 | 提高 `wait_ms`，检查是否有模态框挡住主窗口 |
| `E_WINDOW_AMBIGUOUS` | 多个窗口匹配 | 用 `pid`/`hwnd` 精确指定；`details.candidates` 给出候选（也用于 `target=current_dialog`） |

## 语义树与输入

| 错误码 | 含义 | 下一步 |
| --- | --- | --- |
| `E_ELEMENT_NOT_FOUND` | 控件不存在 | 用 `wait_for(element)` 或 `name_match=contains` |
| `E_ELEMENT_AMBIGUOUS` | 多个同权控件匹配（如多个同名标签页） | 用 `index` 指定目标，或改用 `automation_id`/`within` 收窄 |
| `E_ELEMENT_STALE` | ref 已失效 | 重新 `snapshot`/`find_element`，只使用新 ref |
| `E_STALE_SNAPSHOT` | 引用了超过保留代际的 ref | 重新 `snapshot`；默认保留最近 3 代（`VSP_REF_GENERATIONS`） |
| `E_NEEDS_HUMAN` | 控件无法可靠自动化，继续重试也不会成功 | 按 `details.element` 定位并人工处理；批量流程按策略跳过或中止 |
| `E_VERIFY_FAILED` | 写入后回读与期望不一致（`strict_verify=true` 时） | 查看 `details.expected/observed`；确认焦点、控件状态与是否被弹窗拦截 |
| `E_BLOCKED` | 目标窗口被模态对话框禁用 | 先处理 `details.dialogs`；键盘输入传 `target=current_dialog`，或改用 `details.dialogs[].hwnd` |
| `E_PATTERN_UNSUPPORTED` | 控件不支持所选 UIA pattern | 试 `pattern=default`，或改用 `click`/键盘 |
| `E_DESTRUCTIVE_REQUIRED` | 写入会覆盖非空内容 | 确需覆盖时传 `allow_destructive=true` |
| `E_DESTRUCTIVE_TARGET` | 点击命中了关闭/删除/清空类控件（或标签页右缘关闭热区） | 确认后传 `allow_destructive=true`；标签页改用 `select_tab`/`close_tab` |
| `E_FOCUS` | 无法把目标切到前台 | 先 `click(keep_foreground=true)`；检查模态框 |
| `E_FOCUS_OWNED` | 另一个 profile 持有输入租约（`VSP_FOCUS_LEASE=1`） | 稍后重试，或串行执行两个 profile |
| `E_INPUT_LEASE_CONFLICT` | 另一个客户端持有不同目标的输入租约 | 稍后重试，或确认后传 `force=true` 抢占 |
| `E_FOCUS_LOST` | 输入中前台被抢走 | 重试；确认用户没有在操作桌面 |
| `E_INPUT_UNSUPPORTED` | 该窗口/区域忽略所选输入通道 | 非客户区或 Chromium 改用 `channel=input`/`fallback_input=true`；`gesture` 用 `mode=input` |
| `E_UIPI` | 被 UIPI 拦截 | 以相同权限运行，或改用 UIA 语义操作 |
| `E_ELEVATION_REQUIRED` | 目标进程完整性级别高于服务端，或启动目标要求管理员权限（Win32 740/5） | 以相同权限运行服务端；启动器交给宿主提权，不会自动提权 |
| `E_INPUT` | 输入未完整注入 | 检查前台、输入法与目标是否禁用输入 |
| `E_SYSTEM_KEY_BLOCKED` | 系统级快捷键会逃逸到用户桌面 | 确需发送时传 `allow_system_keys=true` |
| `E_CLIPBOARD_UNSAFE` | 剪贴板含图片/文件，写文本会永久清空且无法还原 | 改用 `channel=auto/keys`（SendInput），或先备份剪贴板 |

## 会话、显示与生命周期

| 错误码 | 含义 | 下一步 |
| --- | --- | --- |
| `E_NO_INTERACTIVE_DESKTOP` | 锁屏/UAC 安全桌面 | 等待用户返回普通桌面后重试；服务端会探测 |
| `E_APP_GONE` | 目标进程已退出 | 重新 `launch_or_place_app` |
| `E_APP_OWNED` | 窗口属于另一个 MCP 客户端 | 让该客户端 release，或为当前客户端启动独立实例 |
| `E_SCREEN_LEASED` | 另一个客户端持有屏幕租约 | 等待其 release_environment 或空闲超时后重试 |
| `E_DRIVER` | VDD IOCTL 失败 | `--doctor` 查看；必要时重启服务端 |
| `E_DRIVER_OPEN` | VDD 设备打不开 | 查看 `driver.open_error`；驱动可能被其他进程占用 |
| `E_DISPLAY` | 显示拓扑操作失败 | 检查是否有其他显示管理软件占用 |
| `E_TIMEOUT` | 工具超时 | 可重试；若持续超时，先保存再 `release_environment{on_dirty:"force",allow_data_loss:true}` 重置会话 |
| `E_DATA_LOSS_REQUIRED` | release 的 `force`/`discard` 会丢弃未保存内容 | 先保存；确认可丢弃后传 `allow_data_loss=true` |
| `E_BUSY` | 另一个写操作正在执行、release 等不到只读观察结束，或读工具等 release 结束超时 | 按 `details.retry_after_ms` 重试；release 请等 snapshot/capture 返回后再试 |
| `E_RATE_LIMITED` | 请求速率超过令牌桶（默认 300 突发 / 100 每秒） | 按 `details.retry_after_ms` 退避；合并批量调用 |
| `E_CANCELLED` | 请求被取消或会话被重置 | 不要自动重试；重新观察后再操作 |
| `E_READ_ONLY` | 服务端以只读模式运行，拒绝了写工具 | 观察/断言可继续；写操作需重启为可写会话 |

## 共享实例与 CLI（连接级）

这些错误不进入 MCP 工具错误目录，通常在 proxy 握手、控制管道或连接关闭时返回。

| 错误码 | 含义 | 下一步 |
| --- | --- | --- |
| `E_CLIENT_LIMIT` | 共享实例客户端达到 `VSP_CLIENT_LIMIT` | 关闭空闲 MCP 客户端，或提高上限 |
| `E_CLIENTS_ACTIVE` | `--cli stop` 时仍有活跃 MCP 客户端 | 先关闭客户端，或确认后加 `--force` |
| `E_INTEGRITY_MISMATCH` | 管道客户端与 daemon 完整性不一致，或完整性查询失败 | 用相同权限启动；仅测试/排障可设 `VSP_ALLOW_INTEGRITY_MISMATCH=1` |
| `E_HANDSHAKE` | 缺少/非法 VSP/1 握手 | 升级客户端与服务端到同一版本 |
| `E_VERSION_MISMATCH` | proxy 与 daemon 版本不一致 | 关闭旧实例后重试 |
| `E_NO_INSTANCE` | `--cli` 查询时没有活跃实例 | 先启动 MCP 客户端；`--cli snapshot/screen` 会自动走本地会话 |
| `E_INSTANCE_BUSY` | 实例存在但控制管道不可用（多为旧版本） | 关闭旧实例后重试，或 `--cli stop --force` |
| `E_RESPONSE_TOO_LARGE` | 控制响应超过 4MB | 减小 `max_nodes` 或 `--all` 范围 |
| `E_MESSAGE_TOO_LARGE` | 单条 MCP 消息或总输入预算超限 | 拆分请求；调整 `VSP_MAX_MESSAGE_MB` / `VSP_TOTAL_BUFFER_MB` |

## 感知、文件与脚本

| 错误码 | 含义 | 下一步 |
| --- | --- | --- |
| `E_IO` | 文件写入失败 | 检查路径权限/同名文件 |
| `E_UNSUPPORTED_IMAGE` | PNG 格式不支持 | 8-bit、非隔行、RGB/RGBA、≤16M 像素 |
| `E_NO_BASELINE` | 基线区域与请求区域不一致 | 对同一区域重新 `capture_screen` |
| `E_DISABLED` | 能力默认关闭，或 run_script 命中危险命令黑名单 | 需要 `--allow-script`；黑名单可用 `VSP_ALLOW_UNSAFE_SCRIPT=1` 显式绕过（不是沙箱） |
| `E_NOT_SUPPORTED` | 当前平台/应用不支持；OCR 时表示没有可用语言包 | 查看 `hint`；OCR 需在系统设置里添加识别语言 |
| `E_INTERNAL` | 未归类的内部错误 | `get_log` 读最近错误；附 `stage` 反馈 |

## 应用、显示与 UIA 细分

| 错误码 | 含义 | 下一步 |
| --- | --- | --- |
| `E_APP_EXITED` | 目标进程已退出 | 重新 `launch_or_place_app` |
| `E_APP_NOT_ALLOWED` | 路径不在 `VSP_APP_ALLOWLIST` | 调整白名单或换可执行文件 |
| `E_APP_START` | 可执行文件不存在/无法启动 | 检查 `path` 与权限 |
| `E_DISPLAY_ADD` | 添加虚拟屏失败 | 检查驱动与显示上限 |
| `E_DISPLAY_MODE` | 设置分辨率/位置失败 | 换分辨率或摘除其他虚拟屏 |
| `E_ELEMENT_DISABLED` | 控件被禁用 | 先完成前置步骤 |
| `E_ELEMENT_OFFSCREEN` | 控件不在可视区 | 先滚动到可见位置 |
| `E_ELEMENT_READONLY` | 控件只读 | 改用键盘或应用编辑入口 |
| `E_MULTIPLE_MATCHES` | 模板多重命中 | 加 `region` 或放宽唯一性 |
| `E_NEW_INSTANCE_UNSUPPORTED` | 应用不支持强制新实例 | 用 `pid` 接管或复用窗口 |
| `E_NO_MATCH` | 模板/颜色未命中 | 放宽阈值或换 `region` |
| `E_ORPHAN_DISPLAY` | 存在无归属虚拟屏 | 人工确认后摘除，或 `--profile` 隔离 |
| `E_RELEASED` | daemon 已退出/`Dispose` 后继续调用 | 重新启动 MCP 客户端；`release_environment` 本身不会进入该状态，之后仍可重新 `launch_or_place_app` |
| `E_SCRIPT` | 脚本执行失败 | 检查 PowerShell 语法/退出码 |
| `E_TARGET_OFFSCREEN` | 目标窗口不在虚拟屏 | 先 `launch_or_place_app` 归位 |
| `E_UIA` | UIA provider 调用失败 | 重试或改用 `channel=input` |

> 设计原则：已知失败不返回 `E_INTERNAL`；`AggregateException` 会拆到内层；
> COM `0x80070003`/`0x80004005` 映射为 `E_APP_GONE`，`0x80070005` 映射为 `E_UIPI`。

## 锚点索引

> 每个错误码都提供 `#e_xxx` 锚点；`docs` 字段直接指向这里。

<a id="e_invalid_arg"></a>

### E_INVALID_ARG

参数不合法（stage: `validate`）

<a id="e_out_of_bounds"></a>

### E_OUT_OF_BOUNDS

坐标越界（stage: `validate`）。读类工具（`capture_screen`/`read_screen_text`/`click_text`）默认会裁剪到虚拟屏并返回 `clipped=true`；传 `on_out_of_bounds=error` 才恢复该错误。`assert_region`/`wait_for` 等断言类默认仍报错。

<a id="e_foreign_window"></a>

### E_FOREIGN_WINDOW

外来窗口（stage: `validate`）

<a id="e_no_target"></a>

### E_NO_TARGET

无目标应用（stage: `resolve`）

<a id="e_window_not_found"></a>

### E_WINDOW_NOT_FOUND

窗口不存在（stage: `resolve`）

<a id="e_window_timeout"></a>

### E_WINDOW_TIMEOUT

等待窗口超时（stage: `launch`）

<a id="e_window_ambiguous"></a>

### E_WINDOW_AMBIGUOUS

窗口歧义（stage: `resolve`）

<a id="e_element_not_found"></a>

### E_ELEMENT_NOT_FOUND

控件不存在（stage: `uia`）

<a id="e_element_stale"></a>

### E_ELEMENT_STALE

元素已失效（stage: `uia`）

<a id="e_stale_snapshot"></a>

### E_STALE_SNAPSHOT

快照已过期（stage: `snapshot`）

<a id="e_element_ambiguous"></a>

### E_ELEMENT_AMBIGUOUS

多个同权控件匹配（stage: `uia`）。`details.candidates` 给出候选；用 `index` 指定目标，或改用 `automation_id`/`within` 收窄。

<a id="e_pattern_unsupported"></a>

### E_PATTERN_UNSUPPORTED

控件不支持该模式（stage: `uia`）

<a id="e_destructive_required"></a>

### E_DESTRUCTIVE_REQUIRED

需要破坏性写入确认（stage: `validate`）

<a id="e_destructive_target"></a>

### E_DESTRUCTIVE_TARGET

点击命中了破坏性控件（stage: `security`）。`details.hit_element` 给出实际命中的名称/类型/边界；确认后传 `allow_destructive=true`。标签页右缘的关闭热区也会触发此错误，优先改用 `select_tab`/`close_tab`。

<a id="e_focus"></a>

### E_FOCUS

无法获得前台（stage: `focus`）

<a id="e_focus_lost"></a>

### E_FOCUS_LOST

输入中丢失前台（stage: `focus`）

<a id="e_no_interactive_desktop"></a>

### E_NO_INTERACTIVE_DESKTOP

没有交互桌面（stage: `desktop`）

<a id="e_uipi"></a>

### E_UIPI

被 UIPI 拦截（stage: `input`）

<a id="e_input"></a>

### E_INPUT

输入失败（stage: `input`）

<a id="e_input_unsupported"></a>

### E_INPUT_UNSUPPORTED

输入通道不支持（stage: `input`）

- Chromium/Electron/WinUI 忽略窗口消息：`click`/`click_ref`/`click_text` 传 `channel=input` 或 `fallback_input=true`；`gesture` 传 `mode=input`。
- 也可用 `VSP_INPUT_AUTOFALLBACK=1` 全局开启自动降级（会移动真实光标）。

<a id="e_system_key_blocked"></a>

### E_SYSTEM_KEY_BLOCKED

系统键被拒绝（stage: `input`）

<a id="e_app_gone"></a>

### E_APP_GONE

目标进程已退出（stage: `app`）

<a id="e_app_owned"></a>

### E_APP_OWNED

窗口属于另一个 MCP 客户端（stage: `app`）。多客户端共享实例时，窗口按客户端归属；让持有者 `release_environment`，或为当前客户端启动独立实例。

<a id="e_screen_leased"></a>

### E_SCREEN_LEASED

另一个客户端持有屏幕租约（stage: `lease`）。同一时刻只允许一个客户端执行 GUI/视觉操作；等待对方 `release_environment` 或租约空闲超时（默认 60s）后重试。

<a id="e_input_lease_conflict"></a>

### E_INPUT_LEASE_CONFLICT

另一个客户端持有不同目标的输入租约（stage: `lease`）。`lease_input` 的 `details` 给出 holder；等待其释放，或确认后用 `force=true` 抢占。相同 pid 的租约可以并存，只有不同目标才冲突。

<a id="e_driver_open"></a>

### E_DRIVER_OPEN

驱动设备打开失败（stage: `display`）

<a id="e_driver"></a>

### E_DRIVER

驱动调用失败（stage: `display`）

<a id="e_display"></a>

### E_DISPLAY

显示操作失败（stage: `display`）

<a id="e_timeout"></a>

### E_TIMEOUT

操作超时（stage: `tool`）

<a id="e_busy"></a>

### E_BUSY

资源忙（stage: `gate`；release 会话锁为 `session`）

<a id="e_rate_limited"></a>

### E_RATE_LIMITED

请求过于频繁（stage: `rate`）

<a id="e_cancelled"></a>

### E_CANCELLED

请求已取消（stage: `tool`）

<a id="e_io"></a>

### E_IO

IO 失败（stage: `io`）

<a id="e_unsupported_image"></a>

### E_UNSUPPORTED_IMAGE

图片格式不支持（stage: `perception`）

<a id="e_no_baseline"></a>

### E_NO_BASELINE

没有可用基线（stage: `verify`）

<a id="e_disabled"></a>

### E_DISABLED

能力未启用（stage: `script`）

<a id="e_not_supported"></a>

### E_NOT_SUPPORTED

不支持（stage: `tool`）

<a id="e_clipboard_unsafe"></a>

### E_CLIPBOARD_UNSAFE

剪贴板不安全（stage: `input`）

<a id="e_app_exited"></a>

### E_APP_EXITED

应用已退出（stage: `app`）

<a id="e_unknown_tool"></a>

### E_UNKNOWN_TOOL

未知工具（stage: `validate`）

<a id="e_app_not_allowed"></a>

### E_APP_NOT_ALLOWED

应用不在白名单（stage: `launch`）

<a id="e_app_start"></a>

### E_APP_START

应用启动失败（stage: `launch`）

<a id="e_display_add"></a>

### E_DISPLAY_ADD

添加虚拟屏失败（stage: `display`）

- 0.2.1 起失败响应带 `details.before_live/after_live/display_index`；未激活（detached）的 Parsec 适配器不算“已存在”。
- 先看 `--doctor` 的 `driver` 状态；驱动 REMOVE 后需要 3-5 秒冷却。若某块屏被其他 live profile 占用，换 `--profile` 或先关闭该实例。

<a id="e_display_mode"></a>

### E_DISPLAY_MODE

设置显示模式失败（stage: `display`）

<a id="e_element_disabled"></a>

### E_ELEMENT_DISABLED

控件已禁用（stage: `uia`）

<a id="e_element_offscreen"></a>

### E_ELEMENT_OFFSCREEN

控件不在可视区（stage: `uia`）

<a id="e_element_readonly"></a>

### E_ELEMENT_READONLY

控件只读（stage: `uia`）

<a id="e_multiple_matches"></a>

### E_MULTIPLE_MATCHES

模板多重命中（stage: `perception`）

<a id="e_new_instance_unsupported"></a>

### E_NEW_INSTANCE_UNSUPPORTED

不支持新实例（stage: `launch`）

<a id="e_no_match"></a>

### E_NO_MATCH

未找到匹配（stage: `perception`）

<a id="e_orphan_display"></a>

### E_ORPHAN_DISPLAY

孤儿虚拟屏（stage: `display`）

<a id="e_released"></a>

### E_RELEASED

会话已释放（stage: `session`）

<a id="e_script"></a>

### E_SCRIPT

脚本执行失败（stage: `script`）

<a id="e_target_offscreen"></a>

### E_TARGET_OFFSCREEN

目标窗口不在虚拟屏（stage: `window`）

<a id="e_uia"></a>

### E_UIA

UIA 调用失败（stage: `uia`）

<a id="e_internal"></a>

### E_INTERNAL

内部错误（stage: `internal`）

<a id="e_data_loss_required"></a>

### E_DATA_LOSS_REQUIRED

需要数据丢失确认（stage: `release`）

<a id="e_focus_owned"></a>

### E_FOCUS_OWNED

输入租约被占用（stage: `focus`）

