# VScreenPilot 实际测试报告（PPT 轮，v2）

> 测试对象：`artifacts/server7/VScreenPilot.exe`
> SHA256：`A90D33A19F74B0A61F6E5924EBBF20CD45CA0A9FE865EA41C7CA07267F7F3EB0`
> 基线提交：`2ef5334`（测试文档提交 `b2a5acc`；补测提交见文末）
> 测试日期：2026-11-15
> 方式：OpenCode 内亲手调用（4 页 PPT）+ 10 个 Python 脚本 + 解包校验 + 补测 + 与 run6 对比
> 相关：[07-静态审查](07-静态审查报告-PPT轮.md)、[08-测试计划](08-实际测试计划-PPT.md)、[10-改进建议](10-改进建议与路线图.md)

**v2 修订记录（相对 v1）**：
1. 错误数由“3 次”更正为“3 次工具错误 + 1 次客户端协议校验失败”。
2. 截图延迟由“P95 45/66ms”更正为“单次 37–66ms”。
3. 明确 oracle 的三个盲点，并把“11/11”从“质量合格”降级为“结构合格”。
4. 新增补测：图片通道、视觉兜底、手势、工具正反例、剪贴板、双击、重开、1080p、未保存 release。
5. 新增工具覆盖矩阵（19/20），新增 N7（`wait_for text` schema 与实现不一致）。
6. 结论分数维持 60，但构成变化（感知上调、协议/隔离下调）。

---

## 0. 结论

**产品结论**：
- **“成熟 MCP 项目”仍不成立**：实测 **60/100**。协议与生命周期扎实，但**图片通道在 OpenCode 中完全不可用**、画布型应用没有语义、分发与 CI 仍是原型级。
- **“OpenCode 调试 Windows 应用”有条件成立**：Word/Win32/UWP 的中等任务可以；PowerPoint 这类画布型应用**能做出成品，但路径昂贵**——4 页 PPT 通过 11 项产物校验，代价是 **118 次工具调用、约 17 分钟、28 次截图**。

**补测后新增的三个硬事实**：
1. **`snapshot{include_screenshot:true}` 在 OpenCode 里整条结果被丢弃**（不是“少一张图”，是 tree + text 全拿不到）。
2. **`wait_for{type:"text"}` 是 schema 里的合法条件，但实现直接抛 `E_INVALID_ARG`**——工具契约说谎。
3. **长文本输入会清空用户的图片剪贴板**（补测：`before=true, after=false`），从“代码推断”升级为“实测确认”。

**补测后确认的正面能力**：
- 视觉兜底可用：`match_visual` 的 pixel/color/template 全部命中，`assert_region` 能检测输入变化，`wait_for color` 立即满足。
- `gesture` 拖拽图片成功（区域变化 11.7%）。
- 双击（窗口消息）确实能进入文本编辑，写入内容能在 pptx XML 里找到。
- 对话框语义完整：`find_element` / `get_element_text` / `set_element_value` / `click_ref` 在字体对话框 7/7 通过。
- 关闭后重开无修复提示；1080p 下 8/8 点击成功。

---

## 1. 环境与基线

| 项 | 值 |
| --- | --- |
| OS | Windows 10.0.26300 x64 |
| 主屏 | 1920×1440 @168dpi（175%） |
| 虚拟屏（主测） | `\\.\DISPLAY9` 1280×720 @96dpi @(1920,0) |
| 虚拟屏（对照） | 1920×1080 @96dpi @(1920,0) |
| PowerPoint | 16.0.20527.20196（中文 UI，已登录） |
| 服务端 | 单文件 R2R，100,158,798 字节 |
| 构建 | 0 警告 / 0 错误 |
| 单测 | 31/31 |
| 协议协商 | 请求 2025-11-25 → 2025-11-25 |

> PowerPoint 首次运行会弹 **OfficePLUS / AI 助手（Chromium 浮层）** 覆盖画布。它不是 PowerPoint 主窗口，`snapshot.dialogs` 看不到；`click auto` 会报 `E_INPUT_UNSUPPORTED`，只能用 `channel=input` 点掉。

---

## 2. 回归结果

| 套件 | 结果 |
| --- | --- |
| 单元测试 | **31/31** |
| 协议/安全 | **30/30**（含 `--fuzz 100`） |
| 契约 | **10/10** |
| 并发 | **5/5** |

四组 summary 均写入同一 `binary_sha256`。

---

## 3. OpenCode 内 PPT 场景

### 3.1 硬数据（服务端 JSONL trace）

| 指标 | 值 |
| --- | --- |
| 工具调用 | **118** 次 |
| 分布 | `press_keys` 32、`capture_screen` 28、`click` 16、`snapshot` 13、`type_text` 9、`set_element_value` 9、`click_ref` 6、其余 5 |
| 工具错误 | 3 次（`E_INPUT_UNSUPPORTED` / `E_DESTRUCTIVE_REQUIRED` / `E_PATTERN_UNSUPPORTED`） |
| 客户端协议失败 | **1 次**（图片 base64 非法，整条结果被丢弃） |
| 服务端总耗时 | **31.79s** |
| 返回文本 / 图片 | 89.6KB / 37.4KB |
| 端到端墙钟 | ≈ **17 分钟** |

> 服务端 32 秒完成全部动作；其余时间是模型在截图里找坐标、确认结果。**瓶颈是迭代次数，不是延迟。**

### 3.2 逐步记录

| 步骤 | 做法 | 结果 |
| --- | --- | --- |
| S1 启动 | `get_environment` + `launch_or_place_app` | 465ms 挂屏；4.4s 启动；`document_ready:true` 但停在开始屏幕（**S3**）；快照文本含账号邮箱 |
| S1b 新建 | `Ctrl+N` 失败 → 点“空白演示文稿”磁贴 | 工具没有“新建文档”能力，只能坐标点击 |
| S1c 浮层 | OfficePLUS AI 助手 | `click auto` → `E_INPUT_UNSUPPORTED`；`channel=input` 才关掉 |
| S2 观察 | `snapshot{max_depth:4}` | **22 节点**，全是 Dock 容器；无功能区按钮、无画布文本（**S4**） |
| S3 标题页 | 坐标点击 + `type_text` | 文字进入；`verified:false` |
| S3b 字号 | 双击 + Ctrl+A + 字体面板 | 两次误操作；最终用字体对话框精确设 40/20 |
| S4 要点页 | `Ctrl+M` + 布局画廊 | 先选成竖排布局，两张截图后才纠正；二级缩进落错行 |
| S5 图片 | `Alt+N,P,1,D` → 对话框 | 43 节点，`set_element_value` 一次成功；首图 1280×720 超界，改 640×360 |
| S6 表格 | `Alt+N,T,I` → 对话框 | 3×4 成功；默认压住标题，按 25 次方向键下移；第三行漏填后补 Tab |
| S7 保存 | F12 → 另存为 | 34 节点，文件名一次成功；无“保留格式”弹窗 |
| S8 验收 | oracle | 11/11（但见 §4 的局限） |
| S9 收尾 | release | 干净；`forced_pids` 含 PPT |

### 3.3 使用体验（0–5）

| 维度 | 分数 | 说明 |
| --- | --- | --- |
| schema 自解释 | 4 | 参数好写；`channel` 清晰 |
| 错误可自纠 | 3 | 三次错误都给了下一步，但每次多一轮 |
| 返回成本 | 2 | 图片必须落盘再读；`snapshot` 带图直接失败 |
| 动作可验证 | 1 | 点击/按键一律 `verified:false`，只能截图 |
| 降级路径 | 3 | `message→input` 明确；Chromium 浮层无 CDP |
| 文档一致 | 2 | `document_ready` 说谎；`wait_for text` 说谎；`readOnlyHint` 不符 |

---

## 4. 产物验收

`artifacts/ppt/VScreenPilot-PPT-测评.pptx`（77,682 字节，4 页，16:9）

| 检查 | 结果 |
| --- | --- |
| 页数 4 / 16:9 | ✅ |
| 至少 1 张图 | ✅ |
| 表格 3×4、12 格有内容 | ✅ |
| 关键文本、无占位提示 | ✅ |
| 元素不越界、标题不重叠 | ✅ |
| 最大字号 ≥ 32 | ✅（显式 40pt） |

**oracle 的三个盲点（必须说明）**：
1. **字号只统计显式 `a:rPr@sz`**，继承自版式的字号看不到——所以只报出 40/20 两个值，而视觉上 P2 标题明显更小。
2. **重叠检查只比较 `p:sp`**，表格/图片（`graphicFrame`）压住标题检测不到（本轮确实发生过，是人工挪开的）。
3. **没有“关闭后重开”的验证**（补测已单独做，见 §5.8）。

**视觉复核**：P1 标题页 44/20pt、居中；P2 四条要点、一条二级；P3 标题+居中截图（约占画布 50%）；P4 标题+3×4 表格（约占 60% 宽）。
**结论**：结构合格、可读、无越界；**排版是“能看”级别**；“各种格式”只做到字号/缩进/表格样式，**没有颜色、形状、对齐变化**，这部分要求只能算部分满足。

---

## 5. 性能与稳定性（脚本）

### 5.1 快照

| 用例 | 节点 | 文本 | P50/P95 | 画布文本命中 |
| --- | --- | --- | --- | --- |
| PPT `interactive_only=true` d4/n300 | 15 | 2.8KB | 37.2/48.0ms | **0/15** |
| PPT `interactive_only=false` d4/n300 | 22 | 4.0KB | 37.6/44.5ms | **0/15** |
| PPT d2/n100 | 8 | 1.8KB | 23.8/30.5ms | 0/10 |
| PPT d6/n1024 | 22 | 4.2KB | 44.4/50.5ms | 0/10 |
| 记事本 d4/n300 | 31 | 5.7KB | 67.8/87.0ms | — |

### 5.2 点击

| 通道 | 成功 | 实际落点 | P95 |
| --- | --- | --- | --- |
| auto | 8/8 | uia 2 / message 6 | **412ms**（超 300ms 目标） |
| message | 8/8 | message | 143ms |
| input | 8/8 | input | 205ms |
| 双击 | 5/5 | message | 59–144ms |

### 5.3 输入

| 字数 | auto | clipboard | keys |
| --- | --- | --- | --- |
| 17 | 241ms | 108ms | 226ms |
| 186 | 517ms | **131ms** | 523ms |
| 1530 | 182ms | 181ms | 1551ms |
| 15000 | 876ms | 865ms | 未测 |

文本剪贴板可还原；**图片剪贴板会被清空**（见 §5.8）。

### 5.4 并发

18000 字输入期间 8 次 `snapshot` 全部成功，P50 210ms、**P95 2382ms**；其中一次只返回 **1 个节点**，无错误码。并发 release 正常摘屏，会话可重建。

### 5.5 Soak（40 轮）

0 错误；snapshot P50/P95 = 165/182ms；RSS 74.1→94.9MB（+20.7）；句柄 528→540（+12）。

### 5.6 恢复

强杀后错误码正确（`E_NO_TARGET`），但**重启后停在开始屏幕**（磁贴点击未生效），且 `apps[]` 残留死进程（1 死 1 活）。

### 5.7 可复现流程

18.2s 完成“启动→标题→新建页→版式→要点→另存为”；**12 步里 2 步坐标点击 `E_INPUT_UNSUPPORTED`**。

### 5.8 补充测试（v2 新增）

| 用例 | 结果 | 关键数据 |
| --- | --- | --- |
| SUP-01 `capture_screen` 内联 base64 | ❌ | 1085 字符，`b64decode(validate=True)` → `ValueError` |
| SUP-02 `snapshot{include_screenshot}` base64 | ❌ | 62,733 字符，同样失败 |
| SUP-03 模板匹配找回自身 | ✅ | 命中 |
| SUP-04 像素/颜色匹配 | ✅ | 颜色命中 |
| SUP-05 `assert_region` 检测输入变化 | ✅ | passed=true |
| SUP-06 `wait_for{type:"text"}` | ❌ | `E_INVALID_ARG 未知 wait_for 条件类型: text` |
| SUP-07 `wait_for{type:"color"}` | ✅ | 立即满足 |
| SUP-08 `wait_for{type:"element"}`（PPT） | ✅ | 超时后 `satisfied:false`，不崩 |
| SUP-09 `gesture` 拖拽图片 | ✅ | 区域变化 11.7% |
| SUP-10 `invoke_element`/`get_element_text`（Pane） | ✅ | `E_PATTERN_UNSUPPORTED`，错误清晰 |
| SUP-11 `get_log` | ✅ | 可读 |
| SUP-12 图片剪贴板 | ❌ | before=true, after=false（**内容被清空**） |
| SUP-13 双击→输入→另存→XML | ✅ | “双击验证”命中，40,340 字节 |
| SUP-14 关闭后重开 | ✅ | 标题正确；`dialogs` 只报“继续阅读”+4 个空窗口 |
| DLG 字体对话框正例 | ✅ 7/7 | `find_element` 命中“大小”，`get_element_text` 读到值，`set_element_value` 写入 36，`click_ref` 关闭 |
| RES 1920×1080 | ✅ 6/6 | 快照 P50 154/P95 224ms（比 720p 慢约 4 倍）；点击 8/8 |
| UNS 未保存 release | ✅ | 进程被强杀，无保存对话框 |

> 补充套件 13/17 通过；4 个失败全部是**已确认的产品缺陷**（2 个 base64、1 个 `wait_for text`、1 个剪贴板），不是脚本问题。

### 5.9 工具覆盖矩阵（20 个）

| 工具 | 覆盖 | 结论 |
| --- | --- | --- |
| `get_environment` | ✅ | 正常；`ensure` 副作用与 `readOnlyHint` 不符 |
| `launch_or_place_app` | ✅ | 启动可用；PPT 就绪误报 |
| `snapshot` | ✅ | 结构可用；带图整条失败；空快照无解释 |
| `capture_screen` | ✅ | 快、稳；内联图不可用 |
| `find_element` | ✅ | 字体对话框命中“大小/确定”；PPT 画布/功能区找不到（正负例） |
| `get_ui_tree` | ✅ | 同 snapshot |
| `invoke_element` | ✅ | Pane 上明确报错（负例） |
| `set_element_value` | ✅ | 对话框字段可用；覆盖需 `allow_destructive` |
| `get_element_text` | ✅ | 对话框读到字号（正例）；Pane 上明确报错（负例） |
| `click` | ✅ | 通道可用；坐标脆弱 |
| `click_ref` | ✅ | 对话框按钮可用 |
| `gesture` | ✅ | 拖拽成功 |
| `type_text` | ✅ | 中文/长文本可用 |
| `press_keys` | ✅ | 主力；无验证 |
| `wait_for` | ✅ | color/visual/element 可用；**text 不可用** |
| `assert_region` | ✅ | 可用 |
| `match_visual` | ✅ | pixel/color/template 可用 |
| `release_environment` | ✅ | 默认强杀 |
| `get_log` | ✅ | 可用 |
| `run_script` | ⛔ | 设计上默认关闭，未测 |

**未覆盖**：HTTP 传输（不存在）、多 profile 并行（被 S1 阻断）、CDP/OCR（不存在）、锁屏/UAC、4K、输入法候选框。

---

## 6. 问题清单（实测确认）

### P0

| ID | 问题 | 证据 | 影响 |
| --- | --- | --- | --- |
| **N1** | 内联图片不是合法 base64 | OpenCode：`Invalid Base64 string`；补测：1085/62733 字符均无法严格解码 | `snapshot{include_screenshot}` **整条结果被丢弃**；截图必须落盘再读 |
| **N2** | PowerPoint 语义树为空壳 | 15–22 节点，全是 Dock；画布 0/15 | 只能坐标/键盘；无法验证动作 |

### P1

| ID | 问题 | 证据 |
| --- | --- | --- |
| **N7** | `wait_for{type:"text"}` schema 声明但实现拒绝 | `E_INVALID_ARG 未知 wait_for 条件类型: text`；`EvaluateCondition` 无 `text` 分支 |
| S1 | 非默认 profile 挂载失败 | 5.70s → `E_DISPLAY_MODE`；`VSP_DISPLAY_OFFSET=0` 时 465ms |
| S3 | `document_ready` 恒 true | 开始屏幕也 true；重启后新建失败 |
| S6 | release 默认强杀 | 未保存内容 + `force`：进程被杀，无保存框 |
| S7 | 图片剪贴板被清空 | before=true → after=false |
| S15 | 动作无后置验证 | 118 次调用、28 次截图，多为“看有没有生效” |
| N3 | 布局变化后坐标点击失效 | flow 12 步 2 次 `E_INPUT_UNSUPPORTED` |
| N4 | UIA 忙时静默返回 1 节点 | 并发用例；无原因字段 |

### P2

| ID | 问题 |
| --- | --- |
| S2 | `readOnlyHint` 与副作用不符 |
| S5 | 超时不取消 handler（未触发，代码路径成立） |
| N5 | `apps[]` 不清理死进程 |
| N6 | `snapshot.dialogs` 与 root 不一致；“继续阅读”等空窗口误报 |
| S9 | 5 条死错误码 + 文档锚点缺失 |
| S12 | 核心类无单测；PPT 套件未进 CI |
| S11 | 分发声明不实（winget 占位、无 Release/Registry） |
| S13 | 脚本黑名单可绕过 |
| S17 | 文档口径漂移 |
| — | 账号邮箱出现在快照 |
| — | OfficePLUS 浮层不可见/不可点 |

---

## 7. 成熟度复评

| 维度 | 权重 | 静态 | v1 | v2 | 说明 |
| --- | --- | --- | --- | --- | --- |
| MCP 协议与契约 | 15 | 12.5 | 11.5 | **11.0** | N1 + N7 |
| 语义感知广度 | 15 | 7.0 | 5.5 | **6.0** | 视觉兜底实测可用，但画布语义仍缺 |
| 输入保真与兼容 | 15 | 9.0 | 8.5 | **8.5** | 拖拽可用；坐标仍脆弱 |
| 隔离与安全 | 10 | 9.0 | 8.5 | **8.0** | 剪贴板数据丢失确认 |
| 可靠性与生命周期 | 10 | 7.5 | 7.0 | **7.0** | 未变 |
| 可观测性 | 8 | 6.0 | 6.0 | **6.0** | 未变 |
| 测试工程 | 10 | 5.0 | 5.5 | **6.0** | 19/20 工具覆盖；仍无 CI 门槛 |
| 分发与生态 | 8 | 2.0 | 2.0 | **2.0** | 未变 |
| 文档与治理 | 5 | 2.5 | 2.5 | **2.5** | 未变 |
| 性能 | 4 | 3.0 | 3.0 | **3.0** | 1080p 慢 4 倍但可接受 |
| **合计** | 100 | 63.5 | 60.0 | **60.0** | 正负相抵 |

---

## 8. 与 run6（Word）对比

| 指标 | run6 | 本轮 v2 | 变化 |
| --- | --- | --- | --- |
| 协议/契约/并发 | 30/10/5 | 30/10/5 | 持平 |
| 单测 | 31/31 | 31/31 | 持平 |
| 新场景产物 | Word | PPT 4 页，11/11 | 新增 |
| 工具覆盖 | 未统计 | **19/20** | 新增证据 |
| 动作验证 | `click_ref` 有 observed | 仍缺 | 未改善 |
| 内联图片 | 未覆盖 | **不可用** | 新 P0 |
| 画布应用 | 未覆盖 | 语义为空 | 新 P0 |
| 视觉兜底 | 未覆盖 | **可用** | 新正面证据 |
| 多 profile | 未实测 | 挂载失败 | 新 P1 |
| 重启建文档 | Word 有处理 | PPT 无 | 新 P1 |

---

## 9. 判定

### 9.1 是否成熟 MCP 项目

**否**（60/100；分发 20%、测试 60%、文档 50%）。

### 9.2 是否足以让 OpenCode 调试 Windows 应用

**有条件可以**：

| 场景 | 判定 |
| --- | --- |
| Win32/WinForms/WPF/记事本 | ✅ |
| Word（含对话框） | ✅ |
| UWP | ✅ |
| PowerPoint / 画布型 | ⚠️ 能出成品，不适合无人值守；有 `match_visual`/`assert_region`/OCR 潜力 |
| Chromium/Electron/WebView2 | ❌ 仍阻断 |

**补测修正了一个判断**：画布型应用并非“完全无路可走”——模板/颜色/区域断言/拖拽都实测可用，缺的是**文本定位**（`wait_for text` 不可用、无 OCR）和**动作后置验证**。

---

## 10. 证据文件

| 文件 | 内容 |
| --- | --- |
| `tools/audit/results/ppt-run/{protocol,contract,concurrency}/summary.json` | 回归 |
| `.../ppt-snapshot`、`ppt-click`、`ppt-input`、`ppt-concurrency`、`ppt-soak`、`ppt-recovery`、`ppt-flow` 的 `summary.json` | 主测 |
| `.../ppt-extra/summary.json` | 补测 17 项（13 通过，4 个缺陷） |
| `.../ppt-resolution/summary.json` | 1080p 对照 |
| `.../ppt-unsaved/summary.json` | 未保存 release |
| `artifacts/ppt/VScreenPilot-PPT-测评.pptx`、`oracle-deliverable.json` | 产物与 11/11 |
| `artifacts/ppt/slides/01..04.png`、`probe-start.png`、`s1-*.png`、`s3-*.png`、`s4-*.png` | 视觉证据 |
