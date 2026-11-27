# VScreenPilot 文档总索引

> 版本：`0.4.0`（30 个工具）
> 原则：**当前事实源**与**历史归档**分离；已完成的计划、实测与原始问题报告不再与主手册混放。
> 相关入口：[`../README.md`](../README.md)（使用说明）、[`../技术文档.md`](../技术文档.md)（as-built 主手册）、[`../CHANGELOG.md`](../CHANGELOG.md)（版本变更）。

---

## 1. 当前事实源

| 文档 | 内容 | 何时看 |
| --- | --- | --- |
| [`../技术文档.md`](../技术文档.md) | as-built 主手册：架构、协议、30 个工具、显示/输入/感知、测试与重构史 | 查机制、契约、默认值和已知限制 |
| [`../README.md`](../README.md) | 安装、接入、快速上手 | 第一次使用 |
| [`../README.en.md`](../README.en.md) | 英文简介 | 英文读者 |
| [`../CHANGELOG.md`](../CHANGELOG.md) | 版本变更与行为变化 | 升级前 |
| [`../需求文件.md`](../需求文件.md) | 需求与对外接口契约 | 确认“应该做什么” |
| [`架构说明.md`](架构说明.md) | 程序集、命名空间、数据流、质量检查 | 改代码前 |
| [`架构规则.md`](架构规则.md) | 参考指标、拆分建议、评审判断 | 重构/评审时 |
| [`errors.md`](errors.md) | 错误码、原因与修复建议 | 排错 |
| [`发布手册.md`](发布手册.md) | 版本号、构建、打包、winget 发布 | 发版 |
| [`mcp-config.example.jsonc`](mcp-config.example.jsonc) | OpenCode MCP 配置示例 | 接入 |
| [`2026-11-剩余任务与Issue复核.md`](2026-11-剩余任务与Issue复核.md) | 0.4.0 技术预览：未完成实机验收（R-01～R-06）、已知限制（L-01～L-08）、14 条 issue 静态结论、CCW 验收脚本 | 验收与发布前 |

## 2. 历史归档（`docs/audit/`）

这些文件保留原文供追溯，**不是当前事实源**；新结论应写入主手册或 CHANGELOG。

| 文档 | 内容 | 状态 |
| --- | --- | --- |
| [`audit/2026-11-issues.md`](audit/2026-11-issues.md) | 0.3.0 实测提交的 I-01～I-14 原始问题报告 | 已修复，结论见剩余任务文档 |
| [`audit/2026-11-issues-修复方案.md`](audit/2026-11-issues-修复方案.md) | I-01～I-14 的实施计划、设计决策、残留边界、验收脚本 | 已落地，等待 CCW 实机验收 |
| [`audit/2026-11-重构史.md`](audit/2026-11-重构史.md) | T1–T3 重构阶段、偏差、T3 结束时的度量 | T3 历史快照 |
| [`audit/2026-11-CCW实测报告.md`](audit/2026-11-CCW实测报告.md) | CCW v23 安装与 Micro850 工程实测、性能数据 | 0.3.0 历史 |
| [`audit/第一轮测试报告.md`](audit/第一轮测试报告.md) | 0.1.0 Word/CCW 审计与重构前基线 | 0.1.0 历史 |
| [`audit/第二轮测试报告.md`](audit/第二轮测试报告.md) | 0.1.0 PPT 场景、修复与复验 | 0.1.0 历史 |

## 3. 维护约定

1. **单一事实源**：工具名、参数、默认值、错误码以 `技术文档.md`、`docs/errors.md` 和 `tools/audit/baseline/0.4.0/server.json` 为准。
2. **变更留痕**：行为变化先写 `CHANGELOG.md`；不再新建“变更摘要”类平行文档。
3. **历史归档**：已完成任务的方案、实测报告和原始 issue 放入 `docs/audit/`，并在文件头注明归档日期与当前事实源。
4. **数字同步**：工具数、错误码、环境变量由 `scripts/check-doc-facts.ps1` 校验；测试数、版本号等文档数字在 `README.md`、`README.en.md`、`技术文档.md`、`docs/架构说明.md`、`需求文件.md` 之间手工保持一致。
5. **提交前检查**：

```powershell
dotnet build VScreenPilot.slnx -c Release
dotnet test tests\VScreenPilot.Tests.Unit\VScreenPilot.Tests.Unit.csproj -c Release
dotnet test tests\VScreenPilot.Tests.Contract\VScreenPilot.Tests.Contract.csproj -c Release
node scripts\check-docs-links.mjs
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\check-doc-facts.ps1
```

## 4. 已退役文档

| 原文档 | 去向 |
| --- | --- |
| `docs/2026-11变更.md` | 内容已并入 `CHANGELOG.md` 的 0.2.0～0.3.0 段与 `技术文档.md`；删除 |
| `docs/VSP工具缺陷和改进意见.md` | 问题清单并入 `docs/audit/2026-11-issues.md`；A5/E3 遗留记入 `技术文档.md` §17.5；删除 |
| `docs/测试执行报告.md` | 移入 `docs/audit/2026-11-CCW实测报告.md` |
| `docs/2026-11-修复方案.md` | 移入 `docs/audit/2026-11-issues-修复方案.md` |
| `issues.md`（仓库根） | 移入 `docs/audit/2026-11-issues.md` |
