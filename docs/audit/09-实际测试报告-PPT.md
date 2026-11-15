# VScreenPilot 实际测试报告（PPT 轮）

> 测试对象：`artifacts/server7/VScreenPilot.exe`
> SHA256：`A90D33A19F74B0A61F6E5924EBBF20CD45CA0A9FE865EA41C7CA07267F7F3EB0`
> 基线提交：`2ef5334`（测试文档提交 `b2a5acc`）
> 测试日期：2026-11-15
> 方式：OpenCode 内亲手调用（4 页 PPT）+ 8 个 Python 脚本 + 解包校验 + 与 run6 对比
> 相关：[07-静态审查](07-静态审查报告-PPT轮.md)、[08-测试计划](08-实际测试计划-PPT.md)、[10-改进建议](10-改进建议与路线图.md)

---

## 0. 结论

**产品结论**：
- **“成熟 MCP 项目”仍不成立**：实测 60/100（静态 64 → 实测 60）。协议与可靠性扎实，但**图片通道在 OpenCode 中不可用**、感知层对画布型应用几乎为空、分发与 CI 仍是原型级。
- **“OpenCode 调试 Windows 应用”有条件成立**：Word/Win32/UWP 的中等任务可以；PowerPoint 这类画布型应用**能做出成品，但路径昂贵且脆弱**——本轮 4 页 PPT 通过全部 11 项产物校验，代价是 **118 次工具调用、约 17 分钟、15 张截图人工判读**。

**最重要的三个实测发现**：
1. **P0：MCP 图片内容不是合法 base64**。`capture_screen`/`snapshot` 的内联图在 OpenCode 侧报 `Invalid Base64 string`；原始 stdout 里 `data` 字段是**原始二进制**（第 2 个字符即 U+FFFD，PNG 头），不是 base64。所有截图必须走 `save_path` + 读文件，核心的“一次调用完成观察”被破坏。
2. **P0（场景级）：PowerPoint 的 UIA 树只有外壳**。`snapshot` 只有 15–22 个节点，全是 `MsoDockTop/MsoDockRight/工作区` 容器；功能区无按钮、画布无文本。静态审查的 S4 被 15/15 次实测确认。
3. **P1：坐标点击在布局变化后失效**。脚本流程 12 步里有 2 步 `E_INPUT_UNSUPPORTED`；手工场景里也出现“表格插到标题上”“布局选成竖排”等需要截图才能发现的误操作。

**亮点**（实测确认）：
- 虚拟屏隔离稳定：整轮未干扰主屏；`get_environment` 挂载 465ms、release 干净。
- 截图性能优秀：全屏 37–66ms，区域 45ms，soak 40 轮无错误。
- 长文本输入快：1530 字 181ms，15000 字 875ms（剪贴板通道），文本剪贴板可还原。
- 对话框语义可用：字体面板 36 节点、插入图片 43 节点、插入表格 16 节点，全部可 `set_element_value`/`click_ref` 操作。
- 只读观察确实不排写锁：长输入期间 8 次 snapshot 全部成功（虽然被 UIA 锁拖慢）。

---

## 1. 环境与基线

| 项 | 值 |
| --- | --- |
| OS | Windows 10.0.26300 x64 |
| 主屏 | 1920×1440 @168dpi（175%） |
| 虚拟屏 | `\\.\DISPLAY9` 1280×720 @96dpi @(1920,0) |
| PowerPoint | 16.0.20527.20196（中文 UI，已登录校园账号） |
| 服务端 | 单文件 R2R，100,158,798 字节 |
| 构建 | 0 警告 / 0 错误 |
| 单测 | 31/31 |
| 协议协商 | 请求 2025-11-25 → 2025-11-25 |

> 测试期间发现：PowerPoint 首次运行会弹 **OfficePLUS / AI 助手（Chromium 浮层）** 覆盖幻灯片。它属于第三方加载项，不是 PowerPoint 主窗口，`snapshot.dialogs` 看不到它；只能用 `channel=input` 点掉。这条对“Office 自动化”是常见坑，已记入改进建议。

---

## 2. 回归结果（P1）

| 套件 | 结果 | 说明 |
| --- | --- | --- |
| 单元测试 | **31/31** | 与 run6 一致 |
| 协议/安全 | **30/30** | `--fuzz 100`；含崩溃自愈、EOF 清理 |
| 契约 | **10/10** | 20 工具 schema、ref 代际、save_path、资源分页 |
| 并发 | **5/5** | 只读不阻塞、E_BUSY、release 后恢复 |

四个 summary 都写入同一 `binary_sha256`，与 run6 的做法一致。

---

## 3. OpenCode 内 PPT 场景

### 3.1 硬数据（来自服务端 JSONL trace）

| 指标 | 值 |
| --- | --- |
| 工具调用 | **118** 次 |
| 其中 | `press_keys` 32、`capture_screen` 28、`click` 16、`snapshot` 13、`type_text` 9、`set_element_value` 9、`click_ref` 6 |
| 错误 | 3 次（`E_INPUT_UNSUPPORTED` / `E_DESTRUCTIVE_REQUIRED` / `E_PATTERN_UNSUPPORTED`） |
| 服务端总耗时 | **31.79s** |
| 返回文本 | 89.6KB |
| 返回图片 | 37.4KB（含 1 次被客户端判为非法 base64） |
| 端到端墙钟 | ≈ **17 分钟**（13:44 → 14:01） |

> 服务端只花了 32 秒；其余时间在模型推理、看截图、纠错。**MCP 的延迟不是瓶颈，迭代次数才是**。

### 3.2 逐步记录

| 步骤 | 做法 | 结果 | 证据 |
| --- | --- | --- | --- |
| S1 启动 | `get_environment` + `launch_or_place_app` | 挂载 465ms；启动 4.4s；`document_ready:true` 但停在开始屏幕（**S3 实证**）；`snapshot` 暴露账号邮箱（隐私） | `probe-start.png` |
| S1b 新建 | `Ctrl+N`（失败）→ 点“空白演示文稿”磁贴（成功） | 工具没有“新建文档”能力，只能靠坐标 | 同上 |
| S1c 浮层 | OfficePLUS AI 助手覆盖画布 | `click auto` → `E_INPUT_UNSUPPORTED`（Chromium）；改 `channel=input` 才关掉 | 调用记录 |
| S2 观察 | `snapshot{max_depth:4}` | **22 节点**，只有 Dock 容器；无功能区按钮、无“单击此处添加标题”（**S4 实证**） | `snapshot s1` |
| S3 标题页 | 坐标点击 + `type_text` | 文字进入成功；`verified:false`（画布无 TextPattern） | `s1-title.png` |
| S3b 字号 | 双击 + Ctrl+A + 字体面板 | 两次误操作（标题变 24pt、标题+副标题同时加粗）；最终用字体对话框精确设为 40/20 | `s1-done.png` |
| S4 要点页 | `Ctrl+M` + `Alt+H,L,...` | 布局画廊按键落点偏，先选成“竖排标题与文本”，后两张截图才纠正 | `s3-layout.png` |
| S5 图片 | `Alt+N,P,1,D` → 插入图片对话框 | 对话框 43 节点，`set_element_value` 一次成功；**首图 1280×720 插入后超出画布**，重新生成 640×360 才放得下 | `s3-image2.png` |
| S6 表格 | `Alt+N,T,I` → 插入表格对话框 | 列/行输入成功；表格默认落在标题上，需按 25 次方向键下移；第三行内容漏填，补 Tab 后补齐 | `s4-moved.png` |
| S7 保存 | F12 → 另存为 | 对话框 34 节点；文件名 `set_element_value` 一次成功；无“保留格式”弹窗 | `slides/04.png` |
| S8 验收 | oracle | **11/11** | `artifacts/ppt/oracle-deliverable.json` |
| S9 收尾 | `release_environment{keep_display:false}` | `closed_pids/forced_pids` 含 PPT；屏已摘 | trace |

### 3.3 使用体验（主观，0–5）

| 维度 | 分数 | 说明 |
| --- | --- | --- |
| schema 自解释 | 4 | 参数好写；`channel` 语义清楚 |
| 错误可自纠 | 3 | 三次错误都给了下一步（`allow_keyboard`/`channel=input`），但都要多一次往返 |
| 返回成本 | 2 | 图片必须落盘再读；每次观察 ≈ 1 次 capture + 1 次文件读 |
| 动作可验证 | 1 | 点击/按键一律 `verified:false`；只能截图看 |
| 降级路径 | 3 | `message→input` 明确；但 Chromium 浮层没有 CDP |
| 文档一致 | 2 | `document_ready` 说谎；`readOnlyHint` 与副作用不符 |

---

## 4. 产物验收

`artifacts/ppt/VScreenPilot-PPT-测评.pptx`（77,682 字节，4 页，16:9）

| 检查 | 结果 |
| --- | --- |
| 页数 = 4 | ✅ |
| 16:9 画布 | ✅ 12192000×6858000 |
| 至少 1 张图 | ✅ 1 张（640×360 源） |
| 表格 3×4 | ✅ |
| 12 个单元格均有内容 | ✅ |
| 关键文本齐全 | ✅ |
| 元素不越界 | ✅ |
| 无“单击此处”残留 | ✅ |
| 标题/正文不重叠 | ✅ |
| 最大字号 ≥ 32 | ✅ 40pt |

**视觉复核**（`artifacts/ppt/slides/01..04.png`）：
- P1 标题页：44pt 标题 + 20pt 副标题，居中，留白合理。
- P2 要点页：4 条要点，其中 1 条二级缩进；加粗未生效（选择区偏移）。
- P3 图文页：标题 + 居中截图，边距正常；图片偏小（约占画布 50%）。
- P4 表格页：标题 + 3×4 表格，表头深色底纹；表格宽度只占画布 60%。

**质量评价**：结构合格、可读、无越界；但排版是“能看”级别，谈不上设计感——这是坐标+键盘路径的必然结果。

---

## 5. 性能与稳定性（脚本）

### 5.1 快照（`ppt-snapshot`）

| 用例 | 节点 | 文本 | P50 / P95 | 画布文本命中 |
| --- | --- | --- | --- | --- |
| PPT interactive_only=true d4/n300 | 15 | 2.8KB | 37.2 / 48.0ms | **0/15** |
| PPT interactive_only=false d4/n300 | 22 | 4.0KB | 37.6 / 44.5ms | **0/15** |
| PPT d2/n100 | 8 | 1.8KB | 23.8 / 30.5ms | 0/10 |
| PPT d6/n1024 | 22 | 4.2KB | 44.4 / 50.5ms | 0/10 |
| 记事本 d4/n300（基线） | 31 | 5.7KB | 67.8 / 87.0ms | — |

结论：**PPT 的树不是“太大”，而是“几乎为空”**；`max_depth/max_nodes` 调大没有帮助。`interactive_only=false` 只多 7 个节点。

### 5.2 点击（`ppt-click`）

| 通道 | 成功 | 实际落点 | P95 |
| --- | --- | --- | --- |
| auto | 8/8 | uia 2 / message 6 | **412ms** |
| message | 8/8 | message 8 | 143ms |
| input | 8/8 | input 8 | 205ms |
| 双击（message） | 5/5 | message | 59–144ms |

auto 的 P95 超了 300ms 目标：它在 UIA 探测失败后才退消息，多花 200–300ms。

### 5.3 输入（`ppt-input`）

| 字数 | auto | clipboard | keys |
| --- | --- | --- | --- |
| 17 | 241ms | 108ms | 226ms |
| 186 | 517ms | **131ms** | 523ms |
| 1530 | 182ms（自动转剪贴板） | 181ms | 1551ms |
| 15000 | 876ms | 865ms | 未测（预计 60s+） |

文本剪贴板在长输入后**已还原**（`SENTINEL-VSP-CLIP`）。非文本内容未实测（代码路径确认只保存文本，见 10 报告）。

### 5.4 并发（`ppt-concurrency`）

- 18000 字输入期间，8 次 `snapshot` 全部成功，P50 210ms、**P95 2382ms**、最大 2382ms。
- 其中一次 snapshot 只返回 **1 个节点**（`nodes:1`），没有错误码——调用方无法区分“应用没控件”和“UIA 忙”。
- 并发 release 正常返回并摘屏；随后 `ensure` 重建成功。

### 5.5 Soak（`ppt-soak`，40 轮）

| 指标 | 结果 |
| --- | --- |
| 错误 | 0 |
| snapshot P50/P95 | 165 / 182ms |
| RSS | 74.1 → 94.9MB（+20.7） |
| 句柄 | 528 → 540（+12） |

### 5.6 恢复（`ppt-recovery`）

| 用例 | 结果 |
| --- | --- |
| 强杀后 `snapshot` | ✅ `E_NO_TARGET` |
| 强杀后 `type_text` | ✅ `E_NO_TARGET` |
| 重新启动并进入演示文稿 | ❌ 启动成功但停在开始屏幕（磁贴点击未生效） |
| 会话内应用列表 | ❌ 旧 pid 仍以 `alive:false` 留在 `apps[]` |

### 5.7 可复现流程（`ppt-flow`）

18.2 秒完成“启动→标题→新建页→版式→要点→另存为”，逐步耗时：
launch 4.2s、layout 0.9s、type 0.2–0.5s、save-dialog 0.3s、save-filename 0.8s。
**12 步里 2 步坐标点击 `E_INPUT_UNSUPPORTED`**（版式切换后落点失效）。

---

## 6. 实测确认的缺陷

> 编号沿用 07 的 S1–S22；新发现用 N1–N6。

### P0

| ID | 问题 | 证据 | 影响 |
| --- | --- | --- | --- |
| **N1** | **内联图片不是合法 base64** | OpenCode 报 `Invalid Base64 string`；原始 stdout 的 `data` 从第 2 字符起就是 U+FFFD + PNG 头 | 所有内联截图不可用；必须 `save_path` 落盘再读，观察成本翻倍 |
| **N2** | **PowerPoint 语义树为空壳**（S4 确认） | 15–22 节点，全是 Dock 容器；`interactive_only` 开关无效 | 只能坐标/键盘操作；无法验证动作；无法不靠截图定位 |

### P1

| ID | 问题 | 实测 |
| --- | --- | --- |
| S1 | 非默认 profile 挂载失败 | 5.70s 后 `E_DISPLAY_MODE`；`VSP_DISPLAY_OFFSET=0` 时 465ms 成功 |
| S3 | `document_ready` 恒 true | 开始屏幕也返回 true；重启后新建失败 |
| S5 | 超时不取消 handler | 未触发（本机无卡死 provider），代码路径仍成立 |
| S6 | release 默认 `force` 杀进程 | `forced_pids:[11108]`；已保存故无损失 |
| S7 | 剪贴板只还原文本 | 文本还原成功；非文本未实测 |
| S15 | 动作无后置验证 | 118 次调用里 28 次截图，几乎全部用于“看有没有生效” |
| **N3** | **坐标点击在布局变化后失效** | flow 12 步 2 次 `E_INPUT_UNSUPPORTED`；手工场景误选竖排布局 |
| **N4** | **snapshot 在 UIA 忙时静默返回 1 节点** | 并发用例；无 `truncated`/`reason` |

### P2

| ID | 问题 | 实测 |
| --- | --- | --- |
| N5 | `apps[]` 不清理已死进程 | 恢复用例返回 2 条（1 死 1 活） |
| N6 | `snapshot.dialogs` 与 root 不一致 | 字体/另存为对话框作为 root，但 `dialogs` 为空 |
| S2 | `readOnlyHint` 与副作用不符 | 未造成事故，但客户端自动批准语义错误 |
| S9 | 5 条死错误码 + 锚点缺失 | 未变 |
| S12 | 核心无单测 | 本轮新增 9 个脚本，仍未进 CI |
| S11 | 分发不实 | 未变 |
| S13 | 脚本黑名单可绕过 | 未变（本轮未启用） |
| S17 | 文档口径漂移 | 未变 |
| — | OfficePLUS 浮层不可见/不可点 | 只能 `channel=input`；`snapshot.dialogs` 无记录 |
| — | 账号邮箱出现在 snapshot | 开始屏幕的 Button 名称含 `8207221809@csu.edu.cn` |

---

## 7. 成熟度复评

| 维度 | 权重 | 静态 | 实测 | 变化原因 |
| --- | --- | --- | --- | --- |
| MCP 协议与契约 | 15 | 12.5 | **11.5** | N1 图片不符合 MCP 的 base64 要求 |
| 语义感知广度 | 15 | 7.0 | **5.5** | S4 确认；无 OCR/标注/画布语义 |
| 输入保真与兼容 | 15 | 9.0 | **8.5** | 吞吐好，但坐标脆弱、无验证 |
| 隔离与安全 | 10 | 9.0 | **8.5** | 隔离稳定；release 强杀、邮箱泄露 |
| 可靠性与生命周期 | 10 | 7.5 | **7.0** | 重启建文档失败、死进程残留 |
| 可观测性 | 8 | 6.0 | **6.0** | trace/request_id 实测有效 |
| 测试工程 | 10 | 5.0 | **5.5** | 新增 PPT 套件，但未进 CI |
| 分发与生态 | 8 | 2.0 | **2.0** | 未变 |
| 文档与治理 | 5 | 2.5 | **2.5** | 未变 |
| 性能 | 4 | 3.0 | **3.0** | 截图/输入/快照均达标 |
| **合计** | 100 | 63.5 | **60.0** | |

---

## 8. 与 run6（Word 轮）对比

| 指标 | run6（Word） | 本轮（PPT） | 变化 |
| --- | --- | --- | --- |
| 协议 | 30/30 | 30/30 | 持平 |
| 契约 | 10/10 | 10/10 | 持平 |
| 并发 | 5/5 | 5/5 | 持平 |
| 单测 | 31/31 | 31/31 | 持平 |
| 截图 P95 | 25/58ms | 45/66ms | 持平（PPT 窗口更复杂） |
| 新场景产物 | Word 文档 | PPT 4 页，11/11 | **新增覆盖** |
| 动作验证 | 有 `observed`（click_ref） | 仍需截图 | 未改善 |
| 内联图片 | 未覆盖 | **不可用** | **新暴露 P0** |
| 画布类应用 | 未覆盖 | **语义为空** | **新暴露 P0** |
| 多 profile | 未实测 | **挂载失败** | 新暴露 P1 |
| 重启建文档 | Word 有 `Ctrl+N` 处理 | PPT 无 | 新暴露 P1 |

**进步**：协议/并发/截图/生命周期在更强压力下仍然稳定；`request_id`、错误目录、`E_BUSY` 行为都按设计工作；PPT 产物质量达标。
**仍缺陷**：感知广度、动作验证、分发、核心单测；本轮又暴露了图片通道和画布语义两个硬缺口。

---

## 9. 判定

### 9.1 是否成熟 MCP 项目

**否**。判据（08 §1.2）：加权 ≥ 80，且分发/测试/文档 ≥ 70%。当前 60 分，分发 20%、测试 55%、文档 50%。

### 9.2 是否足以让 OpenCode 调试 Windows 应用

**有条件可以**：

| 场景 | 判定 |
| --- | --- |
| Win32/WinForms/WPF/记事本 | ✅ 可用 |
| Word（含对话框） | ✅ 可用（run6 + 本轮回归） |
| UWP | ✅ 可用（run6） |
| PowerPoint / 画布型 | ⚠️ **可做出成品，但不适合无人值守**：118 次调用、28 次截图、3 次错误、2 次布局误操作 |
| Chromium/Electron/WebView2 | ❌ 仍阻断（无 CDP；浮层只能 input） |

**关键瓶颈不是速度，而是“看不见”**：服务端 32 秒完成全部动作，17 分钟里大部分是模型在截图里找坐标、确认结果。要把 PPT 变成“可靠可自动化”，必须先补**画布语义（OCR 或标注截图）**和**动作后置验证**。

---

## 10. 证据文件

| 文件 | 内容 |
| --- | --- |
| `tools/audit/results/ppt-run/{protocol,contract,concurrency}/summary.json` | 回归（30/10/5） |
| `tools/audit/results/ppt-run/ppt-snapshot/summary.json` | 快照矩阵 + 树文本样本 |
| `tools/audit/results/ppt-run/ppt-click/summary.json` | 通道矩阵 |
| `tools/audit/results/ppt-run/ppt-input/summary.json` | 输入表 + 剪贴板还原 |
| `tools/audit/results/ppt-run/ppt-concurrency/summary.json` | 并发读延迟、1 节点快照 |
| `tools/audit/results/ppt-run/ppt-soak/summary.json` | 40 轮采样 |
| `tools/audit/results/ppt-run/ppt-recovery/summary.json` | 4/6 的两条失败证据 |
| `tools/audit/results/ppt-run/ppt-flow/summary.json` | 逐步耗时、2 次点击失败 |
| `artifacts/ppt/VScreenPilot-PPT-测评.pptx` | 被测产物 |
| `artifacts/ppt/oracle-deliverable.json` | 11/11 |
| `artifacts/ppt/slides/01..04.png` | 逐页截图 |
| `artifacts/ppt/probe-start.png`、`s1-*.png`、`s3-*.png`、`s4-*.png` | 过程证据 |
