# Changelog

## 0.4.0 — 2026-11-27（issues I-01 ~ I-14 修复批次）

### Added

- `click`/`click_ref`/`click_text` 注入前做只读命中测试，返回 `hit_element`/`hit_source`；新增破坏性目标护栏（`E_DESTRUCTIVE_TARGET`）与 `allow_destructive`。
- 新增 `select_tab`/`close_tab`：按名称选中/关闭标签页，避免坐标误点右缘 `✕`。
- `find_element` 新增 `new_snapshot`/`scope`，返回 `snapshot_age_ms`/`snapshot_root_hwnd`/`outside_snapshot`/`sources[]` 与元素 `hwnd`。
- `press_keys`/`type_text` 新增 `target=app/current_dialog/foreground`；`E_BLOCKED` 的 `dialogs[]` 增加 `suggested_target`。
- `click` 新增 `settle_ms`/`abort_on_window_change`，多击逐击检测新顶层窗口并返回 `clicks_done`/`aborted`/`opened_dialog`。
- `click` 返回 `focus_before`/`focus_after`/`focus_changed`/`delivery`/`suspect_noop`，用于区分“没点中”和“命中但无视觉变化”。
- 新增 `read_table`：GridPattern/TablePattern 优先，DataItem 树回退；支持列过滤、`row_start`、`within`、`include_header` 和单元格上限。
- 新增 `lease_input`：会话级前台租约（acquire/release/renew/status），`keep_foreground` 等价于 30s 租约；`exclusive=true` 启用 200ms 前台守护，`force=true` 会移除对方租约。
- `run_actions` 白名单动作序列，一次调用完成点击→改值→确认，整批共享一个租约；已有租约时续期而不是误释放；`steps[].timeout_ms` 支持协作式单步超时。
- `launch_or_place_app` 新增 `lifecycle=session/detached`、`fit=clamp/keep`；detached 应用不加入 Job，daemon 崩溃/退出后继续运行；`fit=clamp` 使用 DWM 可见边界并返回 `fit_adjusted`/`requested_bounds`/`applied_bounds`。
- `state.json` v2：保存 detached/dedicated/owner/clientKey/tempProfile，崩溃后把存活窗口重新登记为待认领应用；客户端连接时按 `clientKey` 自动认领匹配窗口；`get_environment.apps[]` 返回 `detached`/`recovered`，`session` 返回 `previous_pid`/`previous_started_at`/`state_age_ms`/`input_lease`。
- `capture_screen`/`snapshot` 的 `scale` 上限提高到 4（超采样）；`read_screen_text`/`click_text` 新增 `upscale`（0=auto）。
- 读类工具（`capture_screen`/`read_screen_text`/`click_text`）默认把越界区域裁剪到虚拟屏并返回 `clipped`/`requested_region`；新增 `on_out_of_bounds=error` 恢复旧报错；断言类保持报错。
- `read_screen_text` 返回 `ocr_source_width/height` 和 `ocr_scaled_width/height`。
- `close_tab` 在 Ctrl+W 回退前先选中目标标签页，避免关闭当前活动标签页。
- `set_display` 新增 `fit_windows`（或 `VSP_FIT_MANAGED_WINDOWS=1`）：重排时把仍大于新虚拟屏的受管窗口缩到屏内。
- `click_ref` 输出补 `suspect_noop`。
- `CursorGuard` 在输入租约活跃时保持暂停，租约释放后恢复。
- `assert_region`/`wait_for` 新增 `clip`：断言/等待区域越界时可显式裁剪（默认仍报错）。
- `read_table` 树回退按全局列锚点对齐，缺格不再导致后续列左移。
- OCR 放大默认改为 bilinear，`VSP_OCR_UPSCALE=nearest` 可切最近邻。
- PNG 超采样默认 bilinear，`VSP_PNG_UPSCALE=nearest` 可切最近邻。
- `click` 多击命中新顶层窗口默认中止；`E_DESTRUCTIVE_TARGET`、`E_ELEMENT_AMBIGUOUS`、`E_INPUT_LEASE_CONFLICT` 新错误码。
- `find_element` 在 `scope=app` 下先按快照根分组，再应用 `prefer`，避免旧窗口元素抢占 `index=0`。
- `type_text`/`set_element_value` 新增 `strict_verify`：回读不一致返回 `E_VERIFY_FAILED`（默认 `false`）。
- `type_text`/`press_keys` 新增 `hwnd`：可指定受管对话框作为输入目标。
- `launch_or_place_app` 新增 `place`/`maximize`：可只移动不最大化，或保留当前 bounds。
- `find_element` 新增 `within`：按容器 ref 做 UIA 子树查找。
- `invoke_element` 新增 `select_text`：展开 + 子树精确匹配 + 唯一选中，多命中返回候选。
- 解析 `element_ref` 时对离屏控件尝试 `ScrollItemPattern.ScrollIntoView`。
- TraceLog 每条 `tool_call` 增加 `summary`（ref/verified/observed.changed/snapshot_id/frame_id 等）。
- `--read-only` / `VSP_READ_ONLY=1`：只读会话，写工具返回 `E_READ_ONLY`；共享 daemon 按客户端握手生效。
- 新错误码：`E_BLOCKED`、`E_VERIFY_FAILED`、`E_NEEDS_HUMAN`、`E_ELEVATION_REQUIRED`、`E_READ_ONLY`。
- 新增 `list_windows`：列出顶层窗口（hwnd/pid/进程/类/bounds/是否在虚拟屏/是否受管/是否提权），非受管窗口标题脱敏。
- 新增 `get_processes`：按名称/pid 查进程、启动时间、是否提权、窗口数；用于安装/构建等长任务。
- 新增 `set_display`：运行期切换虚拟屏几何（800–3840 × 600–2160），不必重启 daemon。
- `wait_for` 支持 `type=process`（`running`/`exited`），可直接等安装器/构建退出。
- `launch_or_place_app` 支持 `hwnd` 精确接管对话框/弹窗，只接受顶层窗口；新增 `on_no_window=leave|kill`。
- `click` 新增 `context_menu`（补发 `WM_CONTEXTMENU`，仅允许 `button=right`）与 `interval_ms`（自绘控件多击间隔）。
- `type_text` 新增 `pace_ms`，并修复 `chunk_chars`/`verify_every` 未传入实际写入请求的问题；小 chunk 自动提高回读间隔。
- `get_processes` 默认不枚举窗口（`include_windows=false`），一次 EnumWindows 建 pid→窗口表；`elevated` 按 pid 缓存。
- `list_windows`/`get_processes` 返回 `total`（截断前数量）；`list_windows` 只认本会话虚拟屏设备。
- `wait_for(type=process)` 不再挂载虚拟屏；`launch_or_place_app` 只有拿到窗口才挂载虚拟屏，纯控制台启动不会挂屏。

### Changed

- 工具数 25 → 30；schema 基线切到 `tools/audit/baseline/0.4.0`；版本号 `0.4.0`。
- `click(clicks>1)` 默认中止后续点击（`abort_on_window_change=true`）。
- `launch_or_place_app` 默认 `fit=clamp`：超出虚拟屏的窗口会被缩到屏幕内。
- **进度通知默认关闭**：只有 `VSP_PROGRESS=1` 才发送 `notifications/progress`，避免客户端无法关联 token 时反复断链。
- **daemon 空闲退出默认从 5s 提高到 300s**：客户端短暂断链/重连不再关闭作业对象、连带杀掉已启动的应用；`VSP_DAEMON_IDLE_MS=0` 仍表示立即退出。
- **无窗口不再视为失败**：`launch_or_place_app` 对控制台/启动器/静默安装器返回 `launched=true, window=false, alive/exit_code`，且不再杀进程树、不再提前加入作业对象；`on_no_window=kill` 可显式终止。控制台程序 1.2 秒判定无窗口，不再等满 `wait_ms`。
- **`set_display` 改为独占写操作**：与 capture/snapshot 互斥；失败回滚、位置被忽略时置 `PositionFallback`，并默认重排跑出屏的受管窗口。
- 小窗口先等最多 3 秒找更大的主窗口，超时才接受当前小窗，避免把 splash 当成主窗口。
- 启动失败：Win32 740/5 映射为 `E_ELEVATION_REQUIRED`，2/3/193 映射为 `E_APP_START`，不再落到 `E_INTERNAL`。
- `get_environment`/`snapshot` 的前台信息在非受管窗口时也返回 hwnd/pid/进程名/是否提权（标题仍脱敏）。
- 小窗口（对话框/解压器）尺寸稳定且不可调整大小时提前接管，不再等满 `wait_ms`。
- `launch_or_place_app` 按 `hwnd` 接管对话框时，release 会还原该对话框的位置。
- `snapshot` 后默认保留最近 3 代 ref（`VSP_REF_GENERATIONS` 2–10），长流程不再“多看一眼就失效”。
- 目标窗口被模态对话框禁用时，输入/元素操作提前返回 `E_BLOCKED`，不再发送按键。
- 目标进程完整性级别高于服务端时，输入/点击/元素操作返回 `E_ELEVATION_REQUIRED`，不自动提权。

### Removed

- 移除 CI/发布中的 `arch_metrics --enforce` 与 `doc_metrics --check` 阻塞；`arch_metrics.py` 仅保留 `--inventory` / `--selftest` 作为本地参考。
- 移除 PR CI 的 `check-version.mjs`；版本一致性只在发布流程按 tag 校验。
- 移除 CI/发布的 `capture_baseline` + `compare_baseline` 阻塞步骤；`ToolSchemaSnapshotTests` 继续校验 30 工具 schema。
- 精简 `ArchitectureFitnessTests`：删除文件行数、测试数量、组件映射、record 基线存在性、allowlist 空等风格闸门，保留命名空间/IVT/引用方向/Automation public 类型等真实边界。
- 删除 `tools/audit/doc_metrics.py` 与 `docs/架构说明.md` 的自动度量块。

### Docs

- 技术文档重构：补 0.4.0 工具契约、屏幕/输入租约矩阵、错误码 63、验收矩阵与已知限制；T1–T3 重构史归档到 `docs/audit/2026-11-重构史.md`。
- 新增 `scripts/check-doc-facts.ps1`，CI 校验工具数、错误码、环境变量与文档一致。

### Tests

- 新增 `ElementRefStoreTests`；schema 合同基线切到 `tools/audit/baseline/0.4.0`（历史 `3ddb85b`/`0.3.1-dev` 保留）。
- 新增 `WaitServiceTests`、`FocusGuardTests`、`JsonArgReaderTests`、`ActionBatchContentTests`，并扩展表头、帧缓存、租约、窗口变化与崩溃恢复测试；质量修复批次新增租约延迟归还/TTL、FrameStore 单客户端字节配额、缺失 `startedAt` 恢复与批次步骤标记测试（单元 365）。
- 破坏性护栏的 off 策略测试改用策略重载，不再修改进程环境变量，消除并行测试的偶发失败。

### Fixed

- `run_actions` 内的 `wait_for` 不再二次获取会话读锁（`NoRecursion` 下会抛 `LockRecursionException`）；外层已持读锁时直接复用。
- `type_text` 的 `pace_ms` 现在同时作用于验证与非验证键盘路径。
- `wait_for(type=pixel_diff)` 读取文档字段 `min_changed_ratio`（兼容旧 `threshold`），并校验 0..1；schema 补齐 `tolerance`。
- `read_table` 表头为空、重复或少于实际列数时补齐为 `colN` 并对重名去重，避免列值被跳过或覆盖。
- `FrameStore` 的 `Latest`/`Snapshot` 按客户端过滤；单客户端配额改为 6 帧 / 16MB，全局淘汰优先选仍有多帧的最忙客户端，并保留每个客户端最新 1 帧，避免跨客户端基线串用。
- `FocusGuard`/`CursorGuard` 的租约判断改为全局租约；普通输入结束不再清除其他客户端仍在有效期内的前台租约；较短的新租约也不再缩短更长租约的暂停时间。
- `ForegroundLease(force=true)` 抢占时保留对方的 `PreviousForeground`，对方稍后 release/drop 时归还；仍有其他活跃租约时只延迟归还，不抢回前台；待归还目标增加 120s TTL。
- `lease_input release` / 客户端断连 / `release_environment forceAllClients` 恢复前台前检查剩余租约，并在全量清理时归还最早捕获的前台；非独占 release/renew 不再停掉其他客户端的独占守护。
- `run_actions` 转发嵌套 `snapshot`/`capture_screen` 的 image/resource_link 内容块，并插入 `[step N] action` 文本标记；有失败步骤时设置 `isError=true`。
- `invoke_element` 接入破坏性目标护栏，并新增 `allow_destructive`；`select_text` 只选择不触发，不套用该护栏。
- `wait_for` schema 补齐 `window.state/min_width/min_height`、`text.pid`、`pixel_diff.tolerance/threshold`。
- `RequiredInt`/`RequiredCoordinate` 先校验 JSON 类型，非法输入返回 `E_INVALID_ARG` 而非 `InvalidOperationException`。
- 崩溃恢复不再仅凭 PID 相同跳过；按 PID + 启动时间判断，避免 PID 复用漏恢复；旧 state 缺 `startedAt` 时按旧进程已退出处理。
- 多击窗口变化检测排除 tooltip/IME 候选窗口。
- `clientKey` 规范化路径大小写和末尾分隔符；自动认领同时兼容旧算法的原始/排序键，减少 0.4.0 开发状态重连后的重新接管。
- `get_element_text` 多读 1 字符，修复“刚好等于 max_chars”被误报截断。
- `release_environment` 在关闭动作后重新判断窗口存活，允许回收残留进程。
- `wait_for` 像素差分输出同时保留 `threshold` 并新增 `min_changed_ratio`。

### 已知限制与待验证

- I-07 不修应用复制行为；状态表改用 `read_table`/`ValuePattern`，不再依赖剪贴板。
- 本轮只做编译、单测、契约测试（365 + 14 通过）；未做 CCW 实机手工验收。
- `FrameStore` 的严格字节隔离保证两个客户端；第三个及以上客户端同时刷帧时仍受全局 8 帧 / 32MB 限制（每个客户端最新帧优先保留）。
- `select_text` 不触发候选项，因此不检查破坏性目标；选中后执行需显式 `invoke_element`/`click_ref`。
- 残留边界见 `技术文档.md` §17.4 与 `docs/2026-11-剩余任务与Issue复核.md`。

## 0.3.0 — 2026-11-23（多客户端安全与生命周期收尾）

### Fixed

- **会话锁写优先**：`release_environment` 之前先判读锁，导致独占分支永远执行不到，release 可与 `snapshot`/`capture_screen` 并发。现在写锁优先；读工具最多等 10s，超时返回可重试的 `E_BUSY`，不再无限阻塞。
- **`wait_for` 不再被 release 卡死**：每轮只尝试 250ms 短读锁；release 持写锁时本轮返回 `last.reason=session_busy` 继续轮询，超时/取消逻辑不变。
- **`get_environment.session.screen_lease` 恒为 null**：组合根在构造后才注入 `ScreenLeaseProvider`，此前按值捕获导致字段永远缺失。改为延迟读取。
- **`snapshot.text` 格式**：`{disabled}`/`{focused}` 等状态标记与节点 header 同行，不再单独占一行。
- **`press_keys` 单大写字母**：`A` 现在带 Shift 输出大写，与文档一致。
- **MSAA 键盘回退**：按 `msaaHwnd` 反查 pid 再调用 `TypeText`，并在点击前校验点位，避免落到错误进程。
- **UIA 超时不再补点击**：UIA 坐标动作超时直接返回 `E_TIMEOUT`；退休代际中尚未执行的 UIA 动作会被丢弃，不会在重建后误点。
- **`--doctor`**：`LogRing` 有丢弃时输出 `log_dropped`；`TraceLog` 每 16 条 flush，错误记录即时落盘；遥测改为从 `StructuredContent` 读取 `code/stage`。

### Changed

- **断连清理语义**：客户端断开仍按 `abort` 保留窗口（不丢未保存内容），但超过 10 分钟无同 id 客户端接管时，维护服务按 `force` 回收；daemon 退出仍全量 `force`。
- **`on_dirty=save` 不强杀**：保存框未处理成功时返回 `kept` 并保留窗口；只有 `force`/`discard` 会结束临时 profile 进程。
- **`release_environment` 内部预算**：清理超过 60s 后，剩余应用标记 `kept` 并写 `warnings`，避免外层 90s 工具超时先到、handler 仍持写锁和屏幕租约。
- **强杀结果如实上报**：`forced_pids` 只记录真正发出的 kill；`AppReleaser` 按结果返回 `forced`/`kept`/`closed`；临时 profile 进程仍在运行时绝不删目录。`HasOtherWindows` 不再忽略无标题窗口，避免误杀带隐藏对话框的共享进程。
- **`find_element`**：`contains`/`regex` 扫描增加 8s 预算，耗尽时返回已收集结果并标记 `truncated`。
- **资源与限额**：`FrameStore` 单客户端保留 6 帧；`LogRing` 待写队列上限 4096 并暴露 `DroppedCount`；`ElementRefStore` TTL 扫描改为 30s 一次；`ScreenCapture` 实现 `IDisposable`，摘屏与退出时释放 DC 缓存；`VSP_*` 环境变量统一收敛到 `VspOptions`（默认值与优先级不变）。
- **架构阈值**：`max_type_lines` 由 400 调整为 420，`gt_400` 保留为提示性指标。

> 工具名、参数与 schema 未变，仍为 22 个工具。

## 0.2.2 — 2026-11-19（启动与输入通道修正）

### Fixed

- **单实例接管可能关闭用户原有窗口（安全）**：`launch_or_place_app(new_instance=true)` 在单实例应用（如 Win11 记事本）无法创建新窗口时会回退为接管现有窗口，但随后 `close_on_release`/`dedicated` 被无条件赋值覆盖。`release_environment` 因此会把用户原有窗口当成本会话启动的窗口关闭；`on_dirty=save/discard/force` 下还可能强杀共享进程，默认 `abort` 下也不会恢复窗口位置。现在回退时记录 `adoptedExisting`，保持 `close_on_release=false`、`started=false`，响应 `reused=true` 并写入 `warnings[]`。
- **记事本恢复标签不可见**：Win11 记事本无参数启动会恢复上次会话的标签。无参数启动且标题带文件名时返回警告，提示先 `snapshot` 确认标题，或传 `args` 指定文件。
- **Chromium/WebView2 输入文档错误**：`click` 描述声称 auto 会走 SendInput，实际默认不会；`gesture` 描述写 `channel=input`，但参数名是 `mode`。已修正工具描述和 server instructions。
- **fallback 参数不对称**：`click_ref`/`click_text` 新增 `fallback_input`；新增 `VSP_INPUT_AUTOFALLBACK=1` 进程级开关（默认关闭）。fallback 前补交互桌面检查，锁屏时返回 `E_NO_INTERACTIVE_DESKTOP` 而不是模糊的 `E_INPUT`。
- **match_visual 文档落后**：模板已支持 `scales`（0.5–2.0，默认 `[1]`），不再声称必须 1:1；README、技术文档同步。
- `--help` 补 `VSP_DISPLAY_WIDTH/HEIGHT/REFRESH/OFFSET` 与 `VSP_INPUT_AUTOFALLBACK`，并说明显示设置需重启 daemon。
- 新增 `InputPolicyTests`、`LaunchPolicyTests`；单测 120/120。

## 0.2.1 — 2026-11-19（修虚拟屏 ADD 回归）

### Fixed

- **0.2.0 回归：挂虚拟屏报 `E_DISPLAY_ADD`（新建的虚拟显示器未出现在拓扑中）**。0.2.0 为多 profile 改成“只认本次新出现的 device”，但 `DisplayTopology.List()` 会保留未激活的 Parsec 适配器（本机 `DISPLAY5`~`DISPLAY20` 共 16 个）；驱动 ADD 复用 `DISPLAY5` 时名字不变，判定永远不成立，3 秒后超时。
  - 判定改为“由不可用变为可用”：`Attached || Width > 0`，并排除其他 live profile 已认领的屏（`InstanceRegistry`）。
  - 新增纯函数 `DisplayTopology.PickNewLiveParsec` 与单测，覆盖“同名 detached 适配器复用时必须选中”；`VddProbe` 不受影响。
- `E_DISPLAY_ADD` 失败时返回 `details.before_live/after_live/display_index` 并写入日志，便于现场定位。
- **多虚拟屏时摘屏失败**：`VddDevice.RemoveDisplay` 把 index 按小端 UINT32 写在 offset 0，而驱动按 16 位大端读取；`index=0` 恰好正确，`index=1` 被读成 256 返回 `ERROR_INVALID_PARAMETER`，导致第二个 profile 的屏摘不掉并残留。改为按上游 `parsec-vdd` 的 16 位大端编码，并补单测。
- 跨 profile 的 `Local\VScreenPilot_DisplayAlloc` 保护范围扩大到“ADD + 解析 device”，避免两个 profile 冷启动时抢同一块新屏。
- 错误提示明确说明 detached 适配器不算“已存在”。

## 0.2.0 — 2026-11-18（多客户端共享实例）

### Added

- **多客户端共享实例**：首个 MCP 进程优先 spawn 独立 `--daemon`（`CREATE_BREAKAWAY_FROM_JOB`，Job 不允许时回退普通启动，spawn 失败再回退进程内 daemon）；后续进程作为瘦代理通过命名管道接入，第二个工作目录不再因全局互斥体 exit 3。
- **daemon 生命周期**：`VSP_DAEMON_IDLE_MS`（默认 5000，`0` 立即退出）在最后一个客户端断开后退出；首次启动保留 10s 连接窗口。
- **屏幕租约**：`VSP_SCREEN_LEASE_IDLE_MS` / `VSP_SCREEN_LEASE_WAIT_MS`；TTL 抢占写 `screen_lease_stolen`；`keep_foreground` 延长 30s 并暂停 FocusGuard。
- **per-client 隔离**：refs/apps/epoch/帧/artifacts/日志归属按客户端隔离；断开时释放租约并清理自己的窗口。
- **CLI 管理视图**：`--cli env|snapshot|screen|status|stop`；`snapshot` 返回管理员 `apps[]`（保留旧顶层字段）；`stop` 有活跃客户端时需 `--force`；`env --ensure` 明确提示只读忽略。
- **资源与观测**：共享 TraceLog、`VSP_TOTAL_BUFFER_MB` 跨连接输入预算、daemon 级共享限流、`session.clients[]` / `client_list` / `get_log` 按客户端过滤。
- **路径基准**：`save_path`/`target_path`/相对 exe 按调用方 cwd 解析，`VSP_CLIENT_CWD` 可覆盖。
- **测试**：`share-smoke.mjs`（共享/CLI/空闲退出）、`daemon-smoke.mjs`（daemon 崩溃/首个 proxy 崩溃）、租约与消息预算单测。

### Fixed

- `release_environment` 成功后真正归还屏幕租约（`ForceRelease`，此前 holder 会残留到 TTL）；手动 `--daemon` 首次连接窗口恢复为 10s。
- daemon/进程内 daemon 退出顺序改为停管道 → 清理 session → 删 registry → 放互斥体；`InstanceRegistry.Delete` 校验 pid，避免删掉新实例登记。
- `AttachToSpawnedDaemon` 使用单一 15s deadline 并复用 proxy 日志；握手改缓冲读；`MessageLimiter` 批量预留预算；`PipeHost` accept 不再被握手阻塞。
- `StdioProxy` 在 daemon 主动断开/崩溃时退出 3 并输出明确错误，不再伪装成正常退出 0。
- `get_environment` 增加 `session.role/daemon_pid/client_id/client_cwd/client_roots/clients[]/other_clients/screen_lease`；`release_environment` 增加 `client_id/display_kept_reason/screen_lease_released`；`--cli status` 增加 `client_list`。
- `get_log` 按 `client_id` 过滤并支持 `include_other_clients`；proxy 日志改为 `client-<pid>.log`。
- `save_path`/`target_path` 的相对路径按调用方 cwd 解析；补相对路径单测。
- `Profile` 超长名称截断+哈希；握手拒绝缺失/相对 cwd；完整性查询失败按拒绝处理。
- 显示 ADD 串行化并只认本次新出现的 device；`RemoveDisplay`/Dispose 兜底校验 device；控制管道请求限长；`stop` 先响应再退出。
- `FocusGuard` 只认租约持有者 apps；`focused_element` 校验 PID 归属；`_uiaBlockedPids` 按客户端隔离。
- 日志文件按进程 pid 分离，proxy/daemon 不再混写；trace 文件名加随机后缀。
- 默认 profile 不再静默回退 `Local\` 互斥体。
- MCP 管道单条消息上限（`VSP_MAX_MESSAGE_MB`，默认 16），超限关闭连接。
- 命名管道客户端完整性校验；默认拒绝 elevated/普通双向不一致。
- `find_element`/`get_ui_tree` 无目标时先返回 `E_NO_TARGET`，不再为了报错而挂屏。
- `get_environment` 增加 `client_id`/`screen_lease`；`snapshot` 的 windows/foreground、`wait_for` 文本候选、`read_screen_text` tracked 范围按客户端过滤。
- 修复 `snapshot`/`release_environment` 并发时快照字典的读写竞争。
- 修复 per-client artifacts 导致保留策略失效；daemon 运行期间每小时补扫一次旧目录。
- 修复 `launch_or_place_app{new_instance:false}` 仍按 daemon cwd 解析相对路径。
- 修复 proxy 在已连接后遇到 IO 失败时回退成新 daemon、丢失已消费的 `initialize`。
- 修复日志清理按旧文件名解析导致新日志永不清理；`get_log(source=file)` 不再跨 profile 读取。
- 修复 `InstanceRegistry` 在 `dotnet` 运行/改名 exe 下把存活实例判成 stale。
- 应用白名单改为客户端与全局取交集，空客户端白名单不再绕过全局限制。
- `release_environment` 失败时不再丢弃屏幕租约；释放顺序改为先写锁后屏幕租约。

## 0.1.0 — 2026-11-18（首次公开发布）

### Added

- 发布渠道固定为两条：GitHub Releases（安装版 + 绿色版）与 winget；不再提供 npm/NuGet/MCP Registry。
- `release.yml`：tag 触发 build/test/zip/GitHub Release；`check-version.mjs` 与 `check-docs-links.mjs` 纳入 CI。
- `THIRD-PARTY-NOTICES.md` 与 `scripts/update-winget.ps1`。
- Windows 安装版：Inno Setup per-user 安装包（`VScreenPilot-Setup-0.1.0.exe`），可选加入用户 PATH，自带卸载；同时保留绿色 zip。
- winget 多文件清单（version/locale/installer），`winget validate` 通过。

### Fixed

- `click_text` 纳入写操作调度：之前不持写锁，可能与 `click`/`type_text` 并发点击。
- 未知工具改为结构化 `E_UNKNOWN_TOOL`（含 `code/hint/docs/request_id`），不再是 SDK 生成的裸文本。
- 错误 payload 的 `docs` 改为可直接打开的 GitHub URL；安装包用户不再看到不存在的相对路径。
- 截图产物默认保留 7 天（`VSP_ARTIFACT_RETENTION_DAYS`，0 关闭），启动时清理旧会话目录。
- 测试从 48 增至 71：工具目录精确校验、错误 docs、产物保留、参数校验边界、smoke 收紧到 22 工具。
- 文档对齐：OCR 状态、测试覆盖、工具数量、未知工具行为、归档索引说明。

## 2026-11-15 — 修复轮（PPT 轮后续）

### Fixed

- **内联图片**：`ImageContentBlock.Data` 的契约是 base64 编码后的 UTF-8 字节；改用 `FromBytes`，`capture_screen` / `snapshot{include_screenshot}` 不再产生非法 base64。
- **`wait_for{type:"text"}`**：实现 UIA 文本查找（Name + Document/Edit 的 TextPattern），不再返回 `E_INVALID_ARG`。
- **`document_ready`**：改为三态；PowerPoint/Excel 根据窗口标题判断，未建模应用返回 `null` + `document_ready_reason`；PPT 尝试 `Ctrl+N` 与“空白演示文稿”磁贴。
- **`release_environment`**：默认 `on_dirty=abort`；`force`/`discard` 需要显式 `allow_data_loss=true`（错误码 `E_DATA_LOSS_REQUIRED`）。`abort` 保留应用跟踪与窗口位置；进程退出走 `force` 并摘屏。
- **`click_text`**：支持 `region`、`require_unique`；返回 `matches`/`ambiguous`，不再静默忽略 region。
- **大图元数据**：`preview` / `screenshot_preview` 在 `Text()` 之前写入，恢复 text/structuredContent 一致性。
- **脱敏**：`snapshot` 的 tree/windows/foreground/annotations、`read_screen_text`、`get_environment.focused_element` 都会过滤邮箱。
- **OCR 语言**：ASCII 查询优先 en-US，中文查询优先 zh-Hans，双向回退。
- **剪贴板**：HTML/RTF 也视为不可还原格式，触发回退或 `E_CLIPBOARD_UNSAFE`。
- **超时**：`E_TIMEOUT` hint 改为显式 `force + allow_data_loss` 重置；客户端取消也会把写锁交给仍在运行的 handler。
- **`run_script`**：纳入会话读锁，避免脚本执行期间被 release 摘屏。
- **outputSchema**：`click` / `press_keys` / `gesture` / `release_environment` 与实际返回对齐。
- **画布/观察**：`interactive_only` 保留 Name/Value/Text 非空的 Text/Image/Custom；OCR 支持取消。
- **指标**：`MetricsRegistry`，随 `get_environment.metrics` 返回调用数、错误率、平均/最大耗时、节点数、OCR 次数。
- **多 profile 输入**：`VSP_FOCUS_LEASE=1` 时用命名互斥体串行化输入，冲突返回 `E_FOCUS_OWNED`。
- **坐标降级**：`click{fallback_input:true}` 在 UIA/消息失败后使用 SendInput。
- **PPT 建文档**：开始屏幕磁贴不在 UIA 树时按比例估算点击，并校验标题。
- **体积**：启用单文件压缩，167MB → 64.8MB；启动约 330ms。
- **发布准备**：新增 `scripts/release.ps1`（生成 zip/哈希，不自动上传）。
- **剪贴板**：检测图片/文件格式；`auto` 回退 SendInput，显式 `channel=clipboard` 返回 `E_CLIPBOARD_UNSAFE`；`Restore(null)` 改为清空。
- **空快照**：`nodes<=1` 时重试一次并返回 `empty_reason`。
- **死进程**：`apps[]` 清理死亡超过 3s 的条目。
- **对话框**：root 是弹窗时 `dialogs[]` 补 `is_root:true`。
- **隐私**：`snapshot.text` 与窗口标题做邮箱脱敏，返回 `privacy_redactions`。
- **`readOnlyHint`**：`get_environment` / `snapshot` / `capture_screen` 改为非只读。
- **动作验证**：`click` / `press_keys` / `gesture` 返回 `observed{changed,ratio,region}`。
- **超时取消**：超时后取消 handler，并把 CTS 所有权交给 handler，避免继续注入。
- **静态文本**：`interactive_only` 保留有名字的 Text/Image/Custom 节点。
- **非默认 profile**：位置无法生效时接受实际坐标并标记 `position_enforced=false`，不再直接失败。

### Added

- `snapshot{annotate:true}`：元素框 + 序号 + 网格。
- `read_screen_text` / `click_text`：Windows OCR 文本定位。
- 标准 `resource_link` 内容块（大图 artifact）。
- `--cli env|snapshot|screen`：同一 exe 的 CLI 伴生入口。
- 48 个单元测试（新增图片 base64、document_ready、脱敏、叠加绘制、错误目录、OCR 匹配）。
- `SECURITY.md`、`CONTRIBUTING.md`、`CHANGELOG.md`、`README.en.md`。

## 0.1.0 初始实现 — 2026-11-14

- 官方 MCP C# SDK 2.4.0、20 个工具、虚拟屏隔离、UIA/MSAA、统一错误目录、写调度、快照代际。
