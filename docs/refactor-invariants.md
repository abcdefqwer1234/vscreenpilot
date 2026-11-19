# 重构不变量（评审用清单）

> 本文件是 `docs/全局重构方案.md` 第 5 章的**可执行子集**：评审每个 PR 时逐条对照。
> 任何一条被破坏，都视为重构失败，与“代码变漂亮”无关。
> 基线数据：`tools/audit/baseline/3ddb85b/`（`3ddb85b`，0.2.2）。

## 0. 怎么验证

| 手段 | 覆盖 |
|---|---|
| `tools/audit/capture_baseline.py` + `compare_baseline.py` | 工具目录、schema、错误包、CLI 输出 |
| `tools/audit/run_all.ps1 -SkipDisplay` | 无显示审计（smoke / contract / protocol） |
| `tools/audit/run_all.ps1` | 全量审计（性能、并发、对话框、GUI、PPT） |
| `dotnet test tests/VScreenPilot.Tests.Contract` | schema 快照、golden、架构约束 |
| 人工对照本文件 | 无法自动化的语义项 |

判定口径：

- **零 diff**：与基线逐字段一致（易变字段按 `compare_baseline.py` 的掩码规则排除）。
- **允许的差异**：绝对路径、PID、时间戳、耗时、日志文件名、临时目录名。
- **不允许的差异**：字段名、字段类型、可空性、字段缺失/新增、错误码、退出码、内容块结构。

## 1. MCP 协议面

- [ ] 22 个工具名与基线完全一致，顺序仍按 `Ordinal` 排序。
- [ ] 每个工具的 `title`、`description`、`inputSchema`、`outputSchema`、`annotations` 与基线一致。
- [ ] `serverInfo.name=vscreenpilot`、`serverInfo.version`、`capabilities`、`instructions` 一致。
- [ ] 内容块顺序与结构一致：`text` → `structuredContent` → `image` → `resource_link`。
- [ ] `text` 与 `structuredContent` 语义一致，`request_id` 同时出现在两者中。
- [ ] 进度通知字段为 `progressToken/progress/total/message`，起点 `(0,1,"started")`、终点 `(1,1,"completed")`。
- [ ] 错误包字段齐全：`code/message/details/retryable/hint/stage/docs/request_id`，`docs` 锚点小写。
- [ ] 稳定错误码不变：`E_UNKNOWN_TOOL`、`E_INVALID_ARG`、`E_DISABLED`、`E_BUSY`、`E_SCREEN_LEASED`、`E_TIMEOUT`、`E_CANCELLED`、`E_STALE_SNAPSHOT`、`E_ELEMENT_STALE`、`E_NO_INSTANCE`、`E_CLIENTS_ACTIVE`、`E_MESSAGE_TOO_LARGE`、`E_VERSION_MISMATCH`、`E_INTEGRITY_MISMATCH`、`E_CLIENT_LIMIT`、`E_HANDSHAKE`。
- [ ] 参数边界不变：`type_text` ≤100000 字符、`chunk_chars` 1–256（默认 32）；`run_script` `timeout_ms` 1000–120000、输出 64KB；`match_visual` `max_results` 1–100、`threshold` 0.3–1.0、`scales` 0.5–2.0 且 ≤5；`read_screen_text` `max_lines` 1–200；`snapshot` `max_nodes` ≤1024。
- [ ] 截图落盘规则不变：仅 `.png`；相对路径按调用方 cwd 解析；允许根为 cwd / 客户端 roots / 系统临时目录；解析符号链接后再校验；越界 `E_INVALID_ARG` + `details.allowed_roots`。
- [ ] artifact 资源规则不变：`vsp://artifacts/`、按修改时间倒序、50 条/页、排除 `.preview.png`、`?preview=1`/`?full=1`、>512KB 自动 preview、非法名拒绝。
- [ ] 内联阈值 128KB、preview 缩放 0.25 不变。
- [ ] snapshot 代际语义不变：refs 仅当前代有效、跨代 `E_STALE_SNAPSHOT`、容量 2048、TTL 5 分钟。
- [ ] `release_environment` 的 `display_kept_reason`、`screen_lease_released`、`kept/closed/forced/saved/discarded/restored_pids` 语义不变。

## 2. CLI 面

- [ ] 参数集合不变：`--help`、`--version`、`--doctor`、`--cli env|snapshot|screen|status|stop`、`--force`、`--ensure`、`--all`、`--daemon`、`--profile`、`--parent-pid`、`--allow-script`。
- [ ] 退出码不变：0 成功、1 失败、2 参数错误、3 实例/连接错误。
- [ ] `--cli env` 无实例时走只读 helper（不挂屏、不清理 state），保留 `ensure_ignored`。
- [ ] `--cli snapshot/screen` 无实例且互斥体空闲时用临时本地会话；`status/stop` 无实例返回 `E_NO_INSTANCE` + 退出码 3。
- [ ] `--cli stop` 有活跃客户端且未 `--force` 时返回 `E_CLIENTS_ACTIVE`。
- [ ] `--doctor` 字段、`checks[]`、`hint`、成功判定（`driver.status` + `driver.open` + `interactive`）不变；`ok=true` 退出 0，否则 1。
- [ ] `--help` 内容不缺失（允许标签宽度变化）。

## 3. IPC 面

- [ ] 管道名：`VScreenPilot-mcp-<profile>-<sidhash>`、`VScreenPilot-ctl-<profile>-<sidhash>`。
- [ ] 握手字段不变：`vsp:1`、`client`、`proxy_pid`、`cwd`、`roots`、`version`、`capabilities`；响应 `ok/client_id/protocol/daemon_version`。
- [ ] ctl 五个操作的请求/响应形状不变，含 `admin_view` 与 legacy 顶层字段。
- [ ] 请求上限 1MB、响应上限 4MB。
- [ ] 消息预算：`VSP_TOTAL_BUFFER_MB` 默认 64（8–512）、`VSP_MAX_MESSAGE_MB` 默认 16（1–64）。
- [ ] 超时不变：连接 300ms；ctl `env/status/stop` 5s、`snapshot/screen` 30s；doctor 探测 800ms + 150ms。

## 4. 进程与生命周期

- [ ] 单实例互斥体：`Global\VScreenPilot_Session_0`（default）/`Global\VScreenPilot_Session_<profile>`；非 default 同时查 `Local\`；显示分配为 `Local\VScreenPilot_DisplayAlloc`。
- [ ] daemon spawn、Job breakaway、失败回退进程内 daemon、代理转发行为不变。
- [ ] 预热（`VSP_NO_WARMUP=1` 可关）、空闲退出（`VSP_DAEMON_IDLE_MS`，首连 10s 宽限）、父进程监听、关闭顺序不变。
- [ ] DPI awareness 仍为 Per-Monitor V2。

## 5. 文件系统

- [ ] `%LOCALAPPDATA%\VScreenPilot\{logs, artifacts, sessions, state*.json, instances}` 布局不变。
- [ ] artifact 命名 `<prefix>-<HHmmss-fff>-<guid8>.png`，preview 为 `.preview.png`。
- [ ] 日志轮转 8MB / 3 备份 / 14 天；trace JSONL 字段不变。
- [ ] 保留策略：`VSP_ARTIFACT_RETENTION_DAYS` 默认 7、0 关闭；当前会话目录永不删除；非标准目录名忽略。

## 6. 环境变量

- [ ] 名称、默认值、钳制范围逐项不变（表驱动单测锁定）：
  `VSP_DISPLAY_WIDTH/HEIGHT/REFRESH/OFFSET`、`VSP_INPUT_AUTOFALLBACK`、`VSP_ALLOW_SCRIPT`、`VSP_ALLOW_UNSAFE_SCRIPT`、`VSP_APP_ALLOWLIST`、`VSP_CLIENT_CWD`、`VSP_SCREEN_LEASE_IDLE_MS`、`VSP_SCREEN_LEASE_WAIT_MS`、`VSP_DAEMON_IDLE_MS`、`VSP_CLIENT_LIMIT`、`VSP_TOTAL_BUFFER_MB`、`VSP_MAX_MESSAGE_MB`、`VSP_NO_WARMUP`、`VSP_PROFILE`、`VSP_ARTIFACT_RETENTION_DAYS`。
- [ ] 两层权限仍生效：进程级 + 连接级（`AllowScript`/`AllowUnsafeScript`/`AppAllowlist`）。

## 7. Windows 行为

- [ ] 输入通道顺序：`auto` = UIA → 窗口消息（仅客户区）→ SendInput（需 `fallback_input` 或 `VSP_INPUT_AUTOFALLBACK=1`）。
- [ ] 非客户区在 message/auto 下返回 `E_INPUT_UNSUPPORTED`。
- [ ] 系统键拦截：`Win`、`Alt+Tab`、`Ctrl+Alt+Tab`、`Alt+Esc`。
- [ ] 剪贴板：≥200 字走剪贴板；非文本剪贴板保护；保存/还原。
- [ ] UIA 不可见时回退 MSAA（`bosa_sdm_msword` 等）。
- [ ] UWP `ApplicationFrameWindow` 归属与关闭规则（禁止杀 frame host）。
- [ ] 虚拟屏槽位规则：default 优先槽位 0，其他 profile `hash(profile) % 8` 起；位置未生效时接受实际坐标并置 `position_enforced=false`。
- [ ] 孤儿屏接管、驱动 3–5 秒冷却重试、VDD 心跳与重开不变。

## 8. 并发与调度

- [ ] 限流 300 突发 / 100 每秒，daemon 内所有连接共享。
- [ ] 写调度等待：普通写 30s；`release_environment` 6s；写锁已持 ≥5s 时 release 旁路。
- [ ] release 读锁等待 3s，超时 `E_BUSY` + `retry_after_ms=500`。
- [ ] 屏幕租约 TTL 60s；`keep_foreground` 延长 30s。
- [ ] 工具超时后写锁与租约仍由 handler 持有，直到 handler 结束。
- [ ] epoch：`release_environment` 校验通过后递增，在途结果返回 `E_CANCELLED`。

## 9. 明确不属于外特性（可以改）

类型名、命名空间、文件名、目录结构、私有实现、日志内部格式、注释、测试代码、`InternalsVisibleTo` 列表。
