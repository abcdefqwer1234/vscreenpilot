# 贡献指南

## 1. 本地构建

需要 Windows 10 2004+ / Windows 11 x64 与 .NET SDK 10.0.403（`global.json` 已固定）。

```powershell
dotnet build VScreenPilot.slnx -c Release
dotnet test tests/VScreenPilot.Tests.Unit/VScreenPilot.Tests.Unit.csproj -c Release
dotnet test tests/VScreenPilot.Tests.Contract/VScreenPilot.Tests.Contract.csproj -c Release
```

程序集边界由 `VScreenPilot.Tests.Contract/ArchitectureTests.cs` 断言，编译通过不代表边界正确，契约测试必须一起跑。

## 2. 改代码前的红线

外特性冻结清单在 `docs/refactor-invariants.md`，任何时候都必须零 diff。提交前至少跑：

```powershell
# 发布被测二进制
dotnet publish src/VScreenPilot.Server/VScreenPilot.Server.csproj -c Release -r win-x64 -o artifacts/server

# 无显示审计 + 与基线对比
pwsh tools/audit/run_all.ps1 -Exe artifacts/server/VScreenPilot.exe -Out tools/audit/results/current -SkipDisplay
python tools/audit/compare_baseline.py tools/audit/baseline/3ddb85b tools/audit/results/current
```

对比规则见 `docs/全局重构方案.md` 附录 D：根目录文件逐字段比较，子目录只比较“用例通过集”，且仅把“通过 → 失败”判定为回归。

需要 VDD/Office 的场景（GUI、并发、PPT/Word）优先在自托管 runner 上跑完整套件：

```powershell
pwsh tools/audit/run_all.ps1 -Exe artifacts/server/VScreenPilot.exe -Out tools/audit/results/current
```

`run_all.ps1` 默认使用独立 profile（`vspaudit`），不会打扰用户正在使用的 `default` 会话。

## 3. 新增一个工具

工具定义已集中到 `src/VScreenPilot.Server/Tools/ToolCatalog.cs` 的 `BuildTools`：

1. 在 `BuildTools` 里加 `Add(...)`：名称、标题、描述、`inputSchema`/`outputSchema`、`ToolPolicy`、超时、handler。
2. 用 `ToolPolicy` 声明调度行为：`Mutates`、`Lease`（`ScreenLeaseMode`）、`SessionRead`/`SessionWrite`、`BypassWriteLock`、`WriteTimeoutMs`；不要再维护 `MutatingTools`/`RequiresScreenLease` 清单。
3. handler 放 `src/VScreenPilot.Server/Tools/ToolHandlers.cs`，或委托给会话/领域组件。
4. 在 `tests/VScreenPilot.Tests.Unit/ReleaseReadinessTests.cs` 的 `Expected` 数组补名称；必要时补 `ToolPolicyTests` 断言。
5. 错误码如为新增，补 `docs/errors.md` 与 `ErrorCatalog`。
6. 运行第 2 节的审计命令，并用 `python tools/audit/capture_baseline.py` 重抓基线后提交。

> 目标形态（尚未实现，见执行报告）：`Tools/Definitions/<name>.json` + 单工具类 + `ToolRegistry` 注册一行。

## 4. 修改 schema 或输出字段

1. 先读 `docs/refactor-invariants.md` 第 1 节，确认该字段是否属于冻结清单。
2. 属于冻结清单的字段：必须同时更新 `tools/audit/baseline/<commit>/` 基线，并在 PR 说明里写“为什么这是有意的契约变更”。
3. 更新 `tests/VScreenPilot.Tests.Contract/ToolSchemaSnapshotTests.cs` 期望（基线文件本身）。
4. 输出视图（`Abstractions/Views/*`）字段顺序要与现有 JSON 拼装顺序一致，golden 才稳定。

## 5. 代码风格

- 异步方法一律带 `CancellationToken`，不要 `.Result`/`.Wait()`。
- 禁止新增无类型空捕获 `catch { }`：要么收窄异常类型并注释，要么记日志。
- 平台原语只放 `Platform`，领域编排放 `Automation`，协议/工具/CLI 放 `Server`。
- 日志文案是对外可观测行为，改动前确认审计脚本是否依赖。

## 6. 提交与回滚

- 每个提交必须能编译、单测通过、阶段契约零 diff。
- 结构性与行为性改动分开提交，便于单独 revert。
- 阶段结束打 tag（如 `refactor-p4`），保留 `tools/audit/baseline/*` 作为后续对比基准。

## 7. 文档

改动架构或新增工具后同步更新：

- `docs/architecture.md`：分层、并发模型、已知未完成项。
- `docs/refactor-invariants.md`：不变量清单。
- `docs/errors.md`：错误码与 `docs` 锚点。
