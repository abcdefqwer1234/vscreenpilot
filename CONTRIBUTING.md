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

外特性冻结清单在 [`技术文档.md`](技术文档.md) §15.4；`ToolSchemaSnapshotTests` 会校验 30 工具 schema 与 `tools/audit/baseline/0.4.0/server.json` 一致。提交前至少跑：

```powershell
dotnet build VScreenPilot.slnx -c Release
dotnet test tests/VScreenPilot.Tests.Unit/VScreenPilot.Tests.Unit.csproj -c Release
dotnet test tests/VScreenPilot.Tests.Contract/VScreenPilot.Tests.Contract.csproj -c Release
node scripts/check-docs-links.mjs
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/check-doc-facts.ps1
```

需要 VDD/Office 的场景（GUI、并发、PPT/Word）优先在自托管 runner 上跑完整套件：

```powershell
pwsh tools/audit/run_all.ps1 -Exe artifacts/server/VScreenPilot.exe -Out tools/audit/results/current
```

`run_all.ps1` 默认使用独立 profile（`vspaudit`），不会打扰用户正在使用的 `default` 会话。基线抓取/对比脚本仍保留，但不再作为提交或 CI 的阻塞项。

## 3. 新增一个工具

工具定义已文件化到 `src/VScreenPilot.Server/Tools/Definitions/*.json`：

1. 新增 `<name>.json`：`name/title/description/inputSchema/outputSchema/readOnly/destructive/timeoutMs/policy`。
2. 在 `src/VScreenPilot.Server/Tools/ToolCatalog.cs` 的 `BuildTools` 注册一行 handler；加载时会校验“定义数 == handler 数”。
3. handler 放 `src/VScreenPilot.Server/Mcp/*Tools.cs` 或相应工具组，业务逻辑委托给会话操作组/服务。
4. 在 `tests/VScreenPilot.Tests.Unit/ReleaseReadinessTests.cs` 的 `Expected` 数组补名称；必要时补 `ToolPolicyTests` 断言。
5. 错误码如为新增，补 `docs/errors.md` 与 `ErrorCatalog`。
6. 运行第 2 节的构建/测试；如 schema 变化，同步更新 `tools/audit/baseline/0.4.0/server.json`（或当前版本基线目录）。

> 工具数量属于冻结契约：新增工具需要方案评审，不能只加 JSON。

## 4. 修改 schema 或输出字段

1. 先读 [`技术文档.md`](技术文档.md) §15.4，确认该字段是否属于冻结清单。
2. 属于冻结清单的字段：必须同步更新 `tools/audit/baseline/0.4.0/server.json`（或当前版本基线目录），并在 PR 说明里写“为什么这是有意的契约变更”。
3. 更新 `tests/VScreenPilot.Tests.Contract/ToolSchemaSnapshotTests.cs` 期望（基线文件本身）。
4. 输出视图（`Abstractions/Views/*`）字段顺序要与现有 JSON 拼装顺序一致，golden 才稳定。

## 5. 代码风格

- 异步方法一律带 `CancellationToken`，不要 `.Result`/`.Wait()`。
- 禁止新增无类型空捕获 `catch { }`：要么收窄异常类型并注释，要么记日志。
- 平台原语只放 `Platform`，领域编排放 `Automation`，协议/工具/CLI 放 `Server`。
- 日志文案是对外可观测行为，改动前确认审计脚本是否依赖。

## 6. 提交与回滚

- 每个提交必须能编译、单测通过、契约测试通过（schema 由 `ToolSchemaSnapshotTests` 守护）。
- 结构性与行为性改动分开提交，便于单独 revert。
- 阶段结束打 tag（如 `refactor-p4`），保留 `tools/audit/baseline/*` 作为后续对比基准。

## 7. 文档

改动架构或新增工具后同步更新：

- `docs/架构说明.md`：分层、并发模型、已知未完成项。
- [`技术文档.md`](技术文档.md)：机制、工具、共享实例、重构史与不变量清单。
- `docs/errors.md`：错误码与 `docs` 锚点。
