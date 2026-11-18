# 重构后变化（2026-11）

> 详细方案与复验记录见 [第二轮测试报告](audit/第二轮测试报告.md)；
> 分层与并发模型见 [architecture.md](architecture.md)；错误码见 [errors.md](errors.md)。

## 新增能力

- **`snapshot` 工具**：一次返回语义树（`snapshot_id` + 作用域 ref）、本会话窗口、
  owned 对话框（含 Office `bosa_sdm_msword`、经典 `#32770`、`#32768` 菜单）、
  前台信息与可选截图。优先用它替代 `get_environment + get_ui_tree + find_element`。
- **多会话 profile**：`--profile <name>`（或 `VSP_PROFILE`）为每个会话使用独立互斥体、
  `state.<name>.json`、追踪与 artifact 目录。
- **剪贴板批量输入**：`type_text` 对 ≥200 字默认走 `Ctrl+V`，并保存/还原文本剪贴板；
  `channel=keys` 可强制走 SendInput。
- **`click_ref` 工具**：按 ref 点击，优先 UIA pattern，再回退消息/输入。
- **MCP 资源**：`vsp://artifacts/{file}` 可通过 `resources/list`/`resources/read` 读取。
- **快照代际**：ref 只在创建它的快照内有效；跨代返回 `E_STALE_SNAPSHOT`。
- **错误目录**：所有错误统一带 `code/message/retryable/stage/hint/docs`。
- **安装版**：Inno Setup per-user 安装包，默认装到 `%LOCALAPPDATA%\Programs\VScreenPilot`，
  可选加入用户 PATH；绿色 zip 与 npm 渠道继续保留。

## 行为变化（可能影响旧调用）

- `save_path` 必须是**绝对路径**；指定后就是唯一正式产物，不再额外写 artifact。
- 参数校验递归到嵌套对象：类型/范围/枚举/oneOf/unknown field 全部返回 `E_INVALID_ARG`，
  错误带 `path`。
- `click` 的 `mode` 仍兼容，但推荐 `channel=auto|uia|message|input`。
- 全屏 `capture_screen` 默认 `scale=0.5`；大图落 artifact 并内联 0.25× 同帧预览。
  `resources/read` 对 >512KB 的图默认返回 preview，`?full=1` 才取原图。
- `snapshot` 推进 ref 代际；`find_element`/`get_ui_tree` 复用当前代并返回 `snapshot_id`，
  不再让既有 ref 无故失效。`get_ui_tree(new_snapshot=true)` 可显式换代。
- `release_environment` 默认 `on_dirty=abort`（保留未保存窗口与应用跟踪）；`force`/`discard` 需要 `allow_data_loss=true`，否则返回 `E_DATA_LOSS_REQUIRED`；进程退出路径始终 `force` 并摘屏。
- `release_environment` 3s 等不到只读观察结束会返回 `E_BUSY`，不再边读边清理。
- `wait_for` 不持长读锁；每轮取一次短读锁。
- `baseline_frame_id` 必须与区域 x/y/width/height 完全一致，否则 `E_NO_BASELINE`。
- `press_keys` 按语义拦截 `Alt+Tab`/`Ctrl+Alt+Tab`/`Alt+Esc` 等变体。
- `release_environment` 会推进会话 epoch；在途工具的结果会被丢弃并返回 `E_CANCELLED`。
- `--doctor` 同时检查 `driver.status` 与 `driver.open`，并输出 `checks[]`。
- 协议层改用官方 MCP C# SDK 2.4.0：实测协商到 **2025-11-25**（SDK 当前最新）；2026-07-28 待 SDK 升级后验证。
  无状态调用、进度通知；未知工具返回结构化 `E_UNKNOWN_TOOL`（isError），不再是一句裸文本。
- 新增令牌桶限流（300 突发 / 100 每秒）；超限返回 `E_RATE_LIMITED`。
- trace 增加 `request_id` 与脱敏参数摘要；日志文件名带 profile。

## 工程

- `global.json` 固定 SDK；`Directory.Build.props` 开启分析器。
- 新增 `tests/VScreenPilot.Tests`（xUnit）：PNG 往返/缩放/过滤、KeyMap、快照格式、
  错误目录、写调度、profile、Job Object。
- 新增 `.github/workflows/ci.yml`（build + 单测 + 无显示协议冒烟）。
- 新增 `docs/architecture.md`、`docs/errors.md`、`docs/audit/第一轮测试报告.md`、`docs/audit/第二轮测试报告.md`。

## 已知限制（下一阶段）

- **内联图片**：已修复（`ImageContentBlock.FromBytes`）；`capture_screen` / `snapshot{include_screenshot}` 的 base64 通过严格解码与 OpenCode 渲染验证。
- **画布型应用**：PowerPoint 的 UIA 树仍只有 Dock 容器；已补 `snapshot{annotate}`、`read_screen_text`、`click_text` 作为 OCR/视觉兜底，但结构化语义仍需应用自身暴露。
- 全屏采集已无悬崖：服务端 P95 ≈53ms；大图落盘 + 0.25× 预览 + 标准 `resource_link`。
- UIA 仍是“锁串行 + 5s 超时 + 退休队列”，还不是专用 MTA worker。
- MSAA 已接入（Office 旧对话框）；OCR 已接入（Windows.Media.Ocr）；CDP 尚未接入。
- UWP `ApplicationFrameWindow` 已可解析；Word 自定义对话框可经 MSAA 操作。
- 非默认 profile 可挂载，但部分 VDD/Windows 组合会忽略 `dmPosition`（固定主屏右侧）；`get_environment.virtual_display.position_enforced=false` 会如实标记。
- NuGet `dnx` 受 `PackAsTool` 不支持 `net10.0-windows` 限制；winget/Registry 清单模板已就绪，发布时需填真实 URL/SHA。
