# 错误码目录

> 所有对外错误都包含 `code`、`message`、`retryable`、`stage`、`hint`、`docs`。
> `docs` 指向本文件的锚点。错误码的唯一事实来源是 `src/VScreenPilot.Server/Mcp/ErrorCatalog.cs`，
> 单元测试会检查每个码都有可操作的 hint/summary/stage。

## 参数与目标

| 错误码 | 含义 | 下一步 |
| --- | --- | --- |
| `E_INVALID_ARG` | 参数类型/范围/枚举不合法，或出现未声明字段 | 看 `details.field` 与 `details.allowed/supported`；不会静默使用默认值 |
| `E_OUT_OF_BOUNDS` | 坐标不在本会话虚拟屏内 | 先 `get_environment` 读取 `virtual_display` 的 x/y/width/height |
| `E_FOREIGN_WINDOW` | 目标窗口不属于本会话 | 先 `launch_or_place_app`（可用 `pid` 精确接管） |
| `E_NO_TARGET` | 没有存活的目标应用 | 启动或重新接管；`snapshot` 可查看 `windows[]` |
| `E_WINDOW_NOT_FOUND` | 按标题/pid 找不到窗口 | 改用 `path` 启动，或确认窗口已出现 |
| `E_WINDOW_TIMEOUT` | 等待主窗口超时 | 提高 `wait_ms`，检查是否有模态框挡住主窗口 |
| `E_WINDOW_AMBIGUOUS` | 多个窗口匹配 | 用 `pid` 精确指定；`details.candidates` 给出候选 |

## 语义树与输入

| 错误码 | 含义 | 下一步 |
| --- | --- | --- |
| `E_ELEMENT_NOT_FOUND` | 控件不存在 | 用 `wait_for(element)` 或 `name_match=contains` |
| `E_ELEMENT_STALE` | ref 已失效 | 重新 `snapshot`/`find_element`，只使用新 ref |
| `E_STALE_SNAPSHOT` | 引用了旧快照的 ref | 重新 `snapshot`；ref 只在对应 `snapshot_id` 内有效 |
| `E_PATTERN_UNSUPPORTED` | 控件不支持所选 UIA pattern | 试 `pattern=default`，或改用 `click`/键盘 |
| `E_DESTRUCTIVE_REQUIRED` | 写入会覆盖非空内容 | 确需覆盖时传 `allow_destructive=true` |
| `E_FOCUS` | 无法把目标切到前台 | 先 `click(keep_foreground=true)`；检查模态框 |
| `E_FOCUS_LOST` | 输入中前台被抢走 | 重试；确认用户没有在操作桌面 |
| `E_INPUT_UNSUPPORTED` | 该窗口/区域忽略所选输入通道 | 非客户区或 Chromium 改用 `channel=input`；菜单用 UIA |
| `E_UIPI` | 被 UIPI 拦截 | 以相同权限运行，或改用 UIA 语义操作 |
| `E_INPUT` | 输入未完整注入 | 检查前台、输入法与目标是否禁用输入 |
| `E_SYSTEM_KEY_BLOCKED` | 系统级快捷键会逃逸到用户桌面 | 确需发送时传 `allow_system_keys=true` |

## 会话、显示与生命周期

| 错误码 | 含义 | 下一步 |
| --- | --- | --- |
| `E_NO_INTERACTIVE_DESKTOP` | 锁屏/UAC 安全桌面 | 等待用户返回普通桌面后重试；服务端会探测 |
| `E_APP_GONE` | 目标进程已退出 | 重新 `launch_or_place_app` |
| `E_DRIVER` | VDD IOCTL 失败 | `--doctor` 查看；必要时重启服务端 |
| `E_DRIVER_OPEN` | VDD 设备打不开 | 查看 `driver.open_error`；驱动可能被其他进程占用 |
| `E_DISPLAY` | 显示拓扑操作失败 | 检查是否有其他显示管理软件占用 |
| `E_DISPLAY_IN_USE` | 虚拟屏仍被其他会话占用 | 先 `release_environment`，或用 `--profile` 隔离 |
| `E_TIMEOUT` | 工具超时 | 可重试；持续超时先 `release_environment` |
| `E_BUSY` | 另一个写操作正在执行，或 release 等不到只读观察结束 | 按 `details.retry_after_ms` 重试；release 请等 snapshot/capture 返回后再试 |
| `E_RATE_LIMITED` | 请求速率超过令牌桶（默认 300 突发 / 100 每秒） | 按 `details.retry_after_ms` 退避；合并批量调用 |
| `E_CANCELLED` | 请求被取消或会话被重置 | 不要自动重试；重新观察后再操作 |

## 感知、文件与脚本

| 错误码 | 含义 | 下一步 |
| --- | --- | --- |
| `E_IO` | 文件写入失败 | 检查路径权限/同名文件 |
| `E_ENCODING` | 文本编码不受支持 | 使用 UTF-8 |
| `E_UNSUPPORTED_IMAGE` | PNG 格式不支持 | 8-bit、非隔行、RGB/RGBA、≤16M 像素 |
| `E_NO_BASELINE` | 基线区域与请求区域不一致 | 对同一区域重新 `capture_screen` |
| `E_DIALOG_UNREACHABLE` | 对话框不暴露 UIA/MSAA | 用键盘路径或 app profile 的按键序列 |
| `E_SNAPSHOT_LIMIT` | 快照节点超过上限 | 降低 `max_depth` 或用 `root` 裁剪 |
| `E_DISABLED` | 能力默认关闭，或 run_script 命中危险命令黑名单 | 需要 `--allow-script`；黑名单可用 `VSP_ALLOW_UNSAFE_SCRIPT=1` 显式绕过（不是沙箱） |
| `E_NOT_SUPPORTED` | 当前平台/应用不支持 | 查看 `hint` 中的替代通道 |
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
| `E_RELEASED` | 会话已释放 | 重启服务端或新建 profile |
| `E_SCRIPT` | 脚本执行失败 | 检查 PowerShell 语法/退出码 |
| `E_TARGET_OFFSCREEN` | 目标窗口不在虚拟屏 | 先 `launch_or_place_app` 归位 |
| `E_UIA` | UIA provider 调用失败 | 重试或改用 `channel=input` |

> 设计原则：已知失败不返回 `E_INTERNAL`；`AggregateException` 会拆到内层；
> COM `0x80070003`/`0x80004005` 映射为 `E_APP_GONE`，`0x80070005` 映射为 `E_UIPI`。
