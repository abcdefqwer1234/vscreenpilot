# VScreenPilot 实际测试计划（PPT 轮）

> 依据：[07-静态审查报告-PPT轮](07-静态审查报告-PPT轮.md)、[06-剩余问题与下一步计划](06-剩余问题与下一步计划.md)
> 基线二进制：`artifacts/server7/VScreenPilot.exe`
> SHA256：`A90D33A19F74B0A61F6E5924EBBF20CD45CA0A9FE865EA41C7CA07267F7F3EB0`
> 基线提交：`2ef5334`
> 编写日期：2026-11-15
> 状态：**待用户批准后执行**

---

## 1. 目的与判定口径

### 1.1 要回答的三个问题

1. 这个工程是否达到“成熟 MCP 项目”？（治理、分发、测试、可观测性、协议）
2. 是否足以让 OpenCode 用来调试 Windows 应用？（以 PowerPoint 为最难的 Office 场景）
3. 与上一轮 Word 测试（run6）相比，哪些问题真的修好了，哪些仍然存在？

### 1.2 判定口径（与 07 一致）

- **成熟**：10 维加权 ≥ 80，且分发、测试、文档三项均 ≥ 70%。
- **可用于 OpenCode 调试**：
  - 场景成功率 ≥ 90%（步骤级，含自动重试）；
  - 无 P0；P1 不超过 3 项且都有明确 workaround；
  - 单步 P95 延迟 ≤ 1.5s（观察类）/ ≤ 3s（输入类）；
  - 失败必须可自恢复（错误码 + hint 能指导下一步），`E_INTERNAL` 占比 ≤ 2%。

### 1.3 本轮已确认的决策

| 决策 | 选择 |
| --- | --- |
| OpenCode 接入 | 已把 `~/.config/opencode/opencode.jsonc` 的 `vscreenpilot.command` 指向 `artifacts/server7/VScreenPilot.exe`（备份 `opencode.jsonc.bak-20261115-ppt`） |
| 基线 | 先提交工作区（`2ef5334`），再发布 `server7`，全轮绑定该哈希 |
| PPT 规格 | 4 页中文 16:9（标题 / 要点 / 图文 / 表格），图片用仓库已有截图 |
| 同类对比 | 只做“资料 + 源码”对比，不安装第三方 Agent |
| 回归范围 | 单元 + 协议/契约/并发 + PPT 专项（不重跑 GUI 14 / REG 19 / DIA 4 / 截图 soak） |

---

## 2. 环境与基线（已完成）

| 项 | 值 |
| --- | --- |
| OS | Windows 10.0.26300（x64） |
| 主屏 | `\\.\DISPLAY1` 1920×1440 @168dpi（175% 缩放） |
| 虚拟屏 | `\\.\DISPLAY9` 1280×720 @96dpi，位于 (1920,0) |
| Parsec VDD | 驱动 0.41，`status: Ok`，`open: true` |
| Office | PowerPoint 16.0.20527.20196（`C:\Program Files\Microsoft Office\root\Office16\POWERPNT.EXE`） |
| Python | 3.13（`C:\Users\31746\Tools\Python313`），脚本只用标准库 |
| .NET | 10.0.404（`global.json` 固定） |
| 基线构建 | 0 警告 / 0 错误；单测 31/31 |
| 产物 | 100,158,798 字节，未签名 |
| 互斥体 | 默认 profile 被 OpenCode 常驻实例持有（pid 8776），第二实例退出码 3 |

### 2.1 环境检查已发现的问题（执行时必须规避）

- **S1**：非默认 profile 挂载失败（`E_DISPLAY_MODE`）。因此：
  - PPT 场景在 **OpenCode 内用默认 profile** 执行；
  - Python 脚本在 **OpenCode 释放虚拟屏后**，用 `--profile pptbench` + `VSP_DISPLAY_OFFSET=0` 执行；
  - 两者不并行。
- **S7**：`type_text` 长文本会清空非文本剪贴板。执行前不要把重要内容放在剪贴板；该现象本身会作为测试项记录。
- **S6**：`release_environment` 默认 `force` 会丢未保存内容。PPT 场景**必须先保存**，再 `release`；若需丢弃，显式传 `on_dirty:"discard"` 并记录。

### 2.2 环境检查产物

原始数据：`tools/audit/results/env-check/summary.json`（含 `binary_sha256`）

| 检查 | 结果 |
| --- | --- |
| 构建 | 0 警告 / 0 错误 |
| 单测 | 31/31 |
| `--doctor` | `ok:true`，驱动 0.41，主屏 1920×1440@168dpi |
| 无显示冒烟 | 10/10（`VSP_PROFILE=smoke`、`VSP_DISPLAY_OFFSET=0`） |
| profile + `VSP_DISPLAY_OFFSET=0` | 挂载成功，464.81ms，`DISPLAY9 1280×720@96dpi @ (1920,0)` |
| profile 默认偏移 | `E_DISPLAY_MODE`，5700.47ms，`set=0 apply=0 current=…@(1920,0)` |
| 协议协商 | 请求 2025-11-25 → 2025-11-25 |
| OpenCode MCP 进程 | pid 8776，运行 `artifacts/server7/VScreenPilot.exe` |

**脚本侧的连带要求**：现有审计脚本默认使用 `default` profile，会与 OpenCode 常驻实例争互斥体（表现为 `initialize` 超时）。执行时必须显式设置：

```powershell
$env:VSP_PROFILE='pptbench'
$env:VSP_DISPLAY_OFFSET='0'
```

这也是待改进项：脚本应暴露 `--profile` 参数，而不是依赖环境变量。

---

## 3. 范围

### 3.1 范围内

- 在 OpenCode 中**亲手调用** 20 个工具完成 4 页 PPT；
- PowerPoint 场景下的 `snapshot`/`find_element`/`click_ref`/`click`/`type_text`/`press_keys`/`gesture`/`capture_screen`/`wait_for`/`assert_region`；
- 8 个 Python 脚本的性能/并发/稳定性/恢复测试；
- 协议与契约回归（复用现有 `bench_protocol.py`/`bench_contract.py`/`bench_concurrency.py`）；
- 与同类项目的十维对比表（引用 07 的取数）。

### 3.2 范围外

- CDP/OCR/WGC 的实现验证（能力不存在，只记录“缺失”）；
- CCW/CefSharp（上一轮已证明阻断，本轮不重复）；
- 多机/远程桌面/锁屏；
- 代码签名与发布流程的真实执行（只评估）。

---

## 4. PPT 场景剧本（核心）

> 目标产物：`artifacts/ppt/VScreenPilot-PPT-测评.pptx`（4 页，16:9，中文）
> 每一步记录：工具名、参数来源（ref / 坐标 / 快捷键）、耗时、响应字节、错误码、是否降级、主观体验（1 句）。

### S0 素材与目录

- 建 `artifacts/ppt/`；从 `tools/audit/results/run6/` 选一张 PNG 复制为 `artifacts/ppt/shot.png`（本地文件操作，不计入工具评分）。
- 记录素材尺寸，供后续比例校验。

### S1 启动与就绪

1. `get_environment{ensure:true}` → 断言 `virtual_display.enabled=true`、`width=1280`。
2. `launch_or_place_app{path:POWERPNT.EXE,wait_ms:60000}`。
3. **重点验证 S3**：`document_ready` 是否为 true；若为 true 但实际停在开始屏幕/登录提示 → 记 P1 复现。
4. 若出现模态框：`snapshot` 看 `dialogs[]`；必要时 `press_keys{"sequence":["Esc"]}` 或 `click_ref` 关闭。
5. 若没有演示文稿：`press_keys{"keys":"Ctrl+N"}`（并记录工具未自动处理的缺口）。

**期望**：3 次调用内进入可编辑的空白演示文稿；虚拟屏上窗口最大化。

### S2 观察基线

1. `snapshot{max_depth:4,max_nodes:300,include_screenshot:true,scale:0.5}`。
2. 记录：`nodes`、`truncated`、`text` 字节、`png_bytes`、`elapsed_ms`。
3. **重点验证 S4**：`text` 里是否能看见“幻灯片/单击此处添加标题”等画布内容；还是只有功能区。
4. 若画布为空：改用 `snapshot{interactive_only:false}` 再测一次，比较节点数与内容。

**期望**：能定位标题占位符；若不能，记录为 S4 实证。

### S3 第 1 页：标题页

1. 选择“标题幻灯片”版式（默认第一页即是）。
2. 点击标题占位符：优先 `find_element{name:"标题",role:document_body}` 或 `snapshot` 里的 ref → `click_ref`；失败则 `click{x,y,channel:"message"}`。
3. `type_text{text:"VScreenPilot 能力测评",verify:true}`。
4. 副标题：点击 + 输入“基于虚拟显示器的 Windows 桌面自动化 · 2026-11”。
5. 格式：标题 `Ctrl+B`、`Ctrl+Shift+>` 两次（放大），副标题保持。
6. `capture_screen{region:幻灯片区域}` 存证。

**期望**：两段文字可见、字号有层级；`type_text.verified=true` 或明确 warning。

### S4 第 2 页：要点页（多级列表 + 局部加粗）

1. `press_keys{"keys":"Ctrl+M"}` 新建页。
2. 版式切“标题和内容”：`press_keys{"sequence":["Alt+H","L","Down","Enter"]}`（若菜单行为不符，记录并改用鼠标）。
3. 标题输入“核心能力”。
4. 正文输入 4 条要点（每条一行，`type_text` 一次或分次）：
   - 虚拟屏隔离，不打扰主桌面
   - UIA 语义定位 + MSAA 兜底
   - 快照代际 ref，可追溯失效
   - 统一错误目录与 JSONL 追踪
5. 第 2 条整行加粗：选中行（`Home`/`Shift+End`）→ `Ctrl+B`。
6. 第 3 条降级为二级：`Tab`。
7. 段落间距/对齐：`Ctrl+E`（居中）后再 `Ctrl+L`（恢复左对齐），验证快捷键通道。

**期望**：4 条要点、1 条二级、1 条加粗；文本回读（`get_element_text` 或再次 `snapshot`）能确认。

### S5 第 3 页：图文页

1. `Ctrl+M` 新建页，版式“仅标题”。
2. 标题“实测截图”。
3. 插入图片：`press_keys{"sequence":["Alt+N","P","D"]}` → 等待“插入图片”对话框。
4. `snapshot` 确认 `dialogs[]` 出现对话框（含 `#32770`）。
5. 在文件名框输入绝对路径：优先 `find_element{control_type:"Edit",name:"文件名"}` → `set_element_value`；否则 `type_text`（对话框内焦点）。
6. `press_keys{"keys":"Enter"}`；`wait_for{condition:{type:"element",name:"图片"}}` 或截图确认。
7. 调整位置/大小：`gesture{type:"drag",from,to,mode:"input"}`（消息拖拽会被拒绝，这正是预期行为，记录错误码）。
8. 右侧加说明文字（插入文本框：`Alt+N` → `X` → 拖拽 → 输入）。

**期望**：图片以原比例插入；若对话框不可达，记录 `E_DIALOG_UNREACHABLE`/`E_ELEMENT_NOT_FOUND` 的实际表现。

### S6 第 4 页：表格页

1. `Ctrl+M` 新建页，版式“仅标题”。
2. 标题“工具面覆盖”。
3. 插入表格：`press_keys{"sequence":["Alt+N","T"]}` → 方向键选 3×4 → `Enter`。
4. 逐格输入（用 `Tab` 在单元格间移动）：表头“类别/工具/状态/备注”，其余填 3 行。
5. 表头加粗：选中首行（`Shift+Space`? 失败则鼠标拖选）→ `Ctrl+B`。
6. 记录每格输入耗时与是否错位。

**期望**：3×4 表格、表头加粗；单元格内容不串行。

### S7 保存

1. `press_keys{"keys":"F12"}` → “另存为”。
2. `snapshot` 确认对话框；在“文件名”输入 `C:\Users\31746\Desktop\VScreenPilot\artifacts\ppt\VScreenPilot-PPT-测评.pptx`。
3. `Enter`；若出现“保留格式”提示 → `press_keys{"keys":"Enter"}` 或点击“使用 PowerPoint 演示文稿格式”。
4. `wait_for{condition:{type:"window",title:"VScreenPilot-PPT-测评"}}`（若标题策略允许）。

**期望**：文件落盘；无“修复”提示。

### S8 客观验收（脚本 + 人工）

用 `ppt_oracle.py` 解包校验：

| 检查 | 判据 |
| --- | --- |
| 页数 | `ppt/presentation.xml` 的 `sldIdLst` = 4 |
| 画布比例 | `p:sldSz` = 12192000×6858000 EMU（16:9） |
| 文本 | 4 页均含预期字符串；无“单击此处添加”残留 |
| 字号 | 标题 ≥ 32pt，正文 ≥ 18pt（`a:rPr@sz`） |
| 图片 | `ppt/media/*` 至少 1 个；`a:blip` 关系存在；显示比例与源图差异 ≤ 2% |
| 表格 | 至少 1 个 `a:tbl`，行=3、列=4 |
| 越界 | 所有 `a:off/a:ext` 落在页面内且留 ≥ 0.3in 边距 |
| 重叠 | 标题框与正文框不相交（容差 0.1in） |

另做视觉复核：4 页截图 + 逐页人工判读（对齐、留白、字号一致性）。

### S9 收尾与恢复

1. `release_environment{keep_display:true,on_dirty:"force"}`（文件已保存）。
2. 断言 `closed_pids` 含 POWERPNT 的 pid，无残留窗口。
3. `get_environment` 确认 `apps:[]`。
4. 再 `release_environment{keep_display:false,on_dirty:"force"}` 摘屏。

---

## 5. Python 脚本设计

> 目录：`tools/audit/ppt/`；输出：`tools/audit/results/ppt-run/<name>/summary.json`；每个 summary 写入 `binary_sha256`。
> 全部复用 `tools/audit/vsp_client.py`，只用标准库。
> 运行前提：OpenCode 已 `release_environment{keep_display:false}`；脚本用 `--profile pptbench` + `VSP_DISPLAY_OFFSET=0`。

| 脚本 | 目的 | 采样/规模 | 关键指标 |
| --- | --- | --- | --- |
| `bench_ppt_flow.py` | 复现 S1–S7 的 12 步流程 | 1 轮全量 + 2 轮“新建页→输入→截图”微循环 | 每步 P50/P95、总往返数、降级次数、错误码分布 |
| `bench_ppt_snapshot.py` | 快照规模与裁剪 | depth 2/4/6 × nodes 100/300/1024，各 15 次；PPT vs 记事本 | `nodes`、`truncated`、`elapsed_ms`、`text` 字节、是否含画布文本 |
| `bench_ppt_input.py` | 文本输入准确率与成本 | 20/200/2000/20000 字 × auto/clipboard/keys；中文+emoji | 成功率、`verified`、`inject_ms`、`duration_ms`、剪贴板副作用 |
| `bench_ppt_concurrency.py` | 并发与闸门 | 20k 字输入期间并发 8 个只读调用；再并发 release | `E_BUSY` 率、只读 P95 膨胀、release 结果、恢复时间 |
| `bench_ppt_click.py` | 点击通道与双击 | 功能区 12 个坐标 × auto/message/input；双击进入文本编辑 10 次 | 成功率、通道分布、`verified`、双击命中率 |
| `bench_ppt_soak.py` | 稳定性 | 40 轮“snapshot→click→type→capture” | RSS、句柄、GDI 对象、线程数、`E_TIMEOUT`/`E_INTERNAL` |
| `bench_ppt_recovery.py` | 故障恢复 | 杀 POWERPNT / 弹模态 / release 并发 | 错误码、重启恢复、孤儿窗口/显示 |
| `ppt_oracle.py` | PPT 产物校验 | 对上一步的 pptx | 第 4 节 S8 的 8 项检查 |

### 5.1 采样与安全

- 每个脚本开头 `get_environment{ensure:true}`，结束 `release_environment{keep_display:false,on_dirty:"force"}`。
- 失败中止时也执行 release（`try/finally`）。
- 不调用 `run_script`（默认关闭）。
- 每个用例记录 `request_id`，便于与 JSONL trace 对账。

### 5.2 阈值（SLO）

| 指标 | 阈值 | 依据 |
| --- | --- | --- |
| 截图 P95（区域/全屏） | ≤ 40ms / ≤ 80ms | run6：25/58ms |
| `snapshot` P95（PPT，300 节点） | ≤ 1500ms | 3s 预算内 |
| `click` P95 | ≤ 300ms | 消息/UIA 路径 |
| `type_text` 200 字 P95 | ≤ 1200ms | 剪贴板路径 |
| `release` P95 | ≤ 15s | 含 WM_CLOSE 等待 |
| `E_INTERNAL` 占比 | ≤ 2% | 归因规则 |
| soak RSS 增长 | ≤ 50MB（40 轮） | 防泄漏 |
| soak 句柄增长 | ≤ 100 | 防泄漏 |

---

## 6. 指标与评分

### 6.1 场景指标

- 步骤成功率 = 一次通过（含工具内自动重试）/ 总步骤。
- 降级率 = 需要换通道/换坐标/人工补一步的步骤数 / 总步骤。
- 端到端耗时、总工具调用数、总响应字节（token 估算 = 文本字节/3.5 + 图片按 1 张 ≈ 800 token 计）。
- 产物质量分（S8 的 8 项，每项 0/1）。

### 6.2 OpenCode 体验 rubric（每项 0–5）

| 项 | 判据 |
| --- | --- |
| schema 自解释 | 不看文档能否一次写对参数 |
| 错误可自纠 | 错误码 + hint 是否直接给出下一步 |
| 返回成本 | 完成一步所需的 token/截图数 |
| 动作可验证 | 是否能在同一调用内知道“生效了” |
| 降级路径 | 主通道失败时是否有明确替代 |
| 文档一致 | 工具描述与实际行为是否一致 |

### 6.3 成熟度复核

用 07 §13 的十维表，把“静态推断”替换为实测证据；分数变化 ≥ 3 分必须给出原因。

### 6.4 失败归因规则

| 归因 | 判据 |
| --- | --- |
| 环境 | 驱动/显示/Office 版本/权限导致，可复现且与代码无关 |
| 工具（VScreenPilot） | 错误码来自 ErrorCatalog，且是工具行为不符预期 |
| 模型/调用 | 参数拼错、坐标估算错、未按 hint 操作 |
| 脚本 | 审计脚本自身的断言/时序问题 |
| 应用（PowerPoint） | PPT 自身模态/动画/未激活导致 |

---

## 7. 风险与应对

| ID | 风险 | 应对 |
| --- | --- | --- |
| R1 | 非默认 profile 挂载失败（S1） | 场景用默认 profile；Python 用 `VSP_DISPLAY_OFFSET=0` + 先释放；不并行 |
| R2 | PowerPoint 首次运行/登录/激活弹窗 | S1 用 `snapshot.dialogs` 识别；记录为 S3 证据；必要时人工点掉一次并注明 |
| R3 | 1280×720 下功能区拥挤，按钮被折叠 | 记录为布局约束；补一组 1920×1080（Python profile）对照 |
| R4 | 画布无 UIA，退化为坐标点击 | 这正是 S4 的验证目标；坐标点击失败则记 P1 |
| R5 | `type_text` 清空非文本剪贴板（S7） | 执行前清空/备份剪贴板；把现象记为缺陷 |
| R6 | `release` 丢未保存内容（S6） | 先保存；只用 `on_dirty:"discard"` 做显式丢弃测试 |
| R7 | 用户鼠标/键盘干扰 | 测试期间不操作主屏；`keep_foreground` 用于菜单连续操作 |
| R8 | 长时间运行触发 UAC/锁屏 | 不锁屏；出问题按 `E_NO_INTERACTIVE_DESKTOP` 归因 |
| R9 | OpenCode MCP 实例占用互斥体 | 已确认；Python 必须换 profile |

---

## 8. 执行顺序与时间盒

| 阶段 | 内容 | 预计 |
| --- | --- | --- |
| P0 | 环境与构建（已完成） | — |
| P1 | 回归：单测 + 协议 30 + 契约 10 + 并发 5 | 25 min |
| P2 | OpenCode 内 PPT 场景 S1–S9（含记录） | 45–60 min |
| P3 | 8 个 Python 脚本 | 45–60 min |
| P4 | 产物校验 + 视觉复核 | 15 min |
| P5 | 报告 09 + 建议 10 + 文档同步 | 40 min |
| 合计 | | 约 3 小时 |

### 停止条件

- 出现 P0（会话无法建立/无法恢复）→ 停止，先修。
- 连续 3 步失败且无 workaround → 记录为阻断，跳到收尾。
- 磁盘/内存异常 → 停止。

---

## 9. 交付物

| 文件 | 内容 |
| --- | --- |
| `docs/audit/09-实际测试报告-PPT.md` | 场景记录、指标、截图、与 run6 对比、成熟度复核 |
| `docs/audit/10-改进建议与路线图.md` | P0/P1/P2 修复方案、同类项目做法、路线图 |
| `tools/audit/ppt/*.py` | 8 个脚本 + oracle |
| `tools/audit/results/ppt-run/**/summary.json` | 原始数据（含 hash） |
| `artifacts/ppt/VScreenPilot-PPT-测评.pptx` | 被测产物 |
| `artifacts/ppt/slides/*.png` | 4 页截图 |

---

## 10. 待验证清单（静态推断 → 实测）

| ID | 静态结论 | 实测方式 | 预期 |
| --- | --- | --- | --- |
| S1 | 非默认 profile 挂载失败 | 已在环境检查复现 | 确认；给出修复建议 |
| S3 | PPT `document_ready` 恒 true | S1 第 3 步 | 若误报 → 记录 |
| S4 | 画布文本被 `interactive_only` 裁掉 | S2 两次 snapshot 对比 | 若为空 → 记录 |
| S5 | 超时不取消 handler | `bench_ppt_concurrency` + 人为长输入 | 观察超时后是否还有输入 |
| S6 | release 默认丢数据 | 构造未保存文档 + `force` | 记录是否弹框/被杀 |
| S7 | 剪贴板非文本被清空 | 输入 300 字前放一张图片到剪贴板 | 记录是否丢失 |
| S15 | click 无验证 | `bench_ppt_click` | 记录 `verified` 恒 false 的影响 |
| S22 | 双击进文本编辑 | `bench_ppt_click` | 成功率 |
