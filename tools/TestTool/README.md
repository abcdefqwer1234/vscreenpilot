# VScreenPilot 自动化回归测试

长期保留的测试脚本。每个任务用独立 oracle 判定，不调用被测工具。

## 目录

```text
tools/TestTool/
  run-all.mjs          # 入口：A/B 臂、稳定性、报告
  lib/
    client.mjs         # MCP stdio 客户端，记录耗时/错误码/字节
    util.mjs           # 路径、清理、fixture、采样、COM oracle
  flows/
    tasks.mjs          # 8 个应用任务
    faults.mjs         # 9 个故障注入
  baseline/
    baseline.ps1       # PowerShell + 内置 UIAutomation 对照
    baselines.mjs
  targets/
    UiTarget/          # 确定性 WinForms 靶标
  results/             # 运行产物，gitignore，测试后清理
  fixtures/            # 运行期生成，gitignore，测试后清理
  .tmp/                # 运行期生成，gitignore，测试后清理
```

## 使用

```powershell
dotnet build VScreenPilot.slnx -c Release
node tools/TestTool/run-all.mjs --arm A --task all --runs 3
node tools/TestTool/run-all.mjs --arm B --task t1,t4 --runs 3
node tools/TestTool/run-all.mjs --stability --sessions 50
node tools/TestTool/run-all.mjs --report
```

首次运行会自动编译 `tools/TestTool/targets/UiTarget`。运行前应确保没有用户正在编辑的 Word/Excel/记事本；清理只针对本测试启动的进程（通过 `--parent-pid` 与显式 PID）。

## 安全开关

- `--arm B`（真实桌面脚本基线）默认禁用，必须显式设置 `VSP_ALLOW_REAL_DESKTOP=1`。
- `VSP_NO_INPUT=1` 禁止所有 `mode=input` 点击，键盘任务可能因此返回 `E_FOCUS`。
- `focusWindow` 在点击前校验坐标严格位于 `virtual_display` 内；虚拟屏未挂载时跳过。
- 不要在有人使用该桌面时运行；B 臂和 `mode=input` 都会移动真实鼠标或前台窗口。

## 任务

| ID | 任务 | oracle |
| --- | --- | --- |
| t1 | Notepad 输入并保存 | 文件内容 |
| t2 | 截图缩放 + 区域变化断言 | 截图 meta / assert |
| t3 | Notepad 与 WinForms 靶标双应用 | 文件 + 靶标 JSON |
| t4 | WinForms 表单 | 靶标写出的 JSON |
| t5 | 模板匹配 | 命中坐标 |
| t6 | Word 替换保存 | Word COM 读回 |
| t7 | Excel 单元格写入 | Excel COM 读回 |
| t8 | 故障恢复 | 错误码 + 自愈状态 |

## 清理

测试结束后删除 `tools/TestTool/results/`、`tools/TestTool/fixtures/`、`tools/TestTool/.tmp/`，并执行：

```powershell
tools/VddProbe/bin/Release/net10.0-windows/VddProbe.exe removeall
```

## 已知限制

- 同一模型设计并执行，存在自评偏差；
- token 只统计交互字节；
- B 臂不含模型写脚本的思考成本；
- `t6/t7` 依赖本机 Office；`t7` 需要按窗口标题二次接管 Excel 启动壳进程。
