# Changelog

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

## 0.1.0 — 2026-11-14

- 官方 MCP C# SDK 2.4.0、20 个工具、虚拟屏隔离、UIA/MSAA、统一错误目录、写调度、快照代际。
