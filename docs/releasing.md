# 发布手册

> 发布渠道只有两条：**GitHub Releases** 与 **winget**。
> 不发布 npm、NuGet/dotnet tool、MCP Registry、Chocolatey、Scoop、MSIX。

## 1. 版本号位置

发版前把这些位置统一改成同一版本（当前 `0.1.0`）：

| 文件 | 字段 |
| --- | --- |
| `src/VScreenPilot.Server/VScreenPilot.Server.csproj` | `<Version>` |
| `src/VScreenPilot.Server/Infrastructure/BuildInfo.cs` | `Version` |
| `packaging/installer/VScreenPilot.iss` | `#define MyAppVersion` |
| `packaging/winget/VScreenPilot.VScreenPilot.yaml` | `PackageVersion` |
| `packaging/winget/VScreenPilot.VScreenPilot.locale.en-US.yaml` | `PackageVersion` |
| `packaging/winget/VScreenPilot.VScreenPilot.installer.yaml` | `PackageVersion`、`DisplayVersion` |
| `CHANGELOG.md` | `## <version>` 段落 |

`node scripts/check-version.mjs --tag v<version>` 会检查以上位置与 tag 是否一致。

## 2. 发布前检查

```powershell
dotnet build VScreenPilot.slnx -c Release
dotnet test tests\VScreenPilot.Tests\VScreenPilot.Tests.csproj -c Release
node scripts/check-docs-links.mjs
node scripts/check-version.mjs

cd packaging\winget
winget validate --manifest .
cd ..\..
```

本地安装包冒烟（可选）：

```powershell
.\scripts\release.ps1 -Version <version>
start artifacts\release\VScreenPilot-Setup-<version>.exe
```

## 3. 打 tag 与自动发布

确认工作区干净后：

```powershell
git tag -a v<version> -m "VScreenPilot <version>"
git push origin main
git push origin v<version>
```

tag 会触发 `.github/workflows/release.yml`：

1. 版本/文档一致性检查；
2. build + 单测；
3. 生成绿色 zip、symbols zip、安装包、`SHA256SUMS.txt`；
4. 创建 GitHub Release 并上传全部资产；
5. 用**刚构建出的安装包**回填 `packaging/winget` 的 SHA；
6. 把 winget 清单提交回 `main`（提交信息带 `[skip ci]`）。

> 第 5 步很关键：CI 构建的安装包与本地构建的 SHA 不同，winget 清单必须以 Release 资产为准。

## 4. 发布后核对

1. Release 页面有四个资产：
   - `VScreenPilot-win-x64-v<version>.zip`
   - `VScreenPilot-Setup-<version>.exe`
   - `VScreenPilot-symbols-v<version>.zip`
   - `SHA256SUMS.txt`
2. `main` 上出现 `chore(winget): 同步 <version> 安装包 SHA [skip ci]` 提交。
3. 本地核对 winget 清单：

```powershell
cd packaging\winget
winget validate --manifest .
Select-String -Path VScreenPilot.VScreenPilot.installer.yaml -Pattern 'InstallerSha256|InstallerUrl'
```

SHA 必须等于 `SHA256SUMS.txt` 里的安装包哈希。

## 5. 提交 winget

首次发布：

```powershell
wingetcreate submit --prtitle "New package: VScreenPilot <version>" packaging\winget
```

已有包的新版本：

```powershell
wingetcreate update VScreenPilot.VScreenPilot --version <version> `
  --urls https://github.com/abcdefqwer1234/vscreenpilot/releases/download/v<version>/VScreenPilot-Setup-<version>.exe `
  --submit
```

或直接提交已更新的本地清单：

```powershell
wingetcreate submit --prtitle "Update: VScreenPilot.VScreenPilot to <version>" packaging\winget
```

首次提交需要：

- GitHub 账号登录（wingetcreate 会走 device flow）；
- 在 PR 上签署 Microsoft CLA（回复 `@microsoft-github-policy-service agree`）；
- 等待 URL/SHA/静默安装/Defender 校验和人工审核。

## 6. 绝对不要做

- **不要重打已发布版本的 tag**，也不要手动重跑已发布版本的 release workflow。会覆盖 Release 资产、改变安装包 SHA，导致 winget 清单和已提交的 PR 失效。
- 不要在 CI 构建后又用本地构建的 setup 去更新 winget 清单。
- 不要把未签名的安装包描述成“已签名”。

## 7. 已知边界

- winget 包只安装 VScreenPilot；Parsec VDD 驱动仍需用户单独安装：
  `winget install --id Parsec.ParsecVDD -e`。
- 安装包当前未签名，SmartScreen 可能提示未知发布者；签名接入后在同一 workflow 增加签名步骤。
- 卸载保留 `%LOCALAPPDATA%\VScreenPilot` 下的日志与截图数据。
