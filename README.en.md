# VScreenPilot

Run Windows desktop apps on a **throwaway virtual display** so an AI agent can screenshot, click,
type, locate elements and verify results — without stealing your cursor or covering your desktop.

- One MCP server, 22 tools (including `snapshot`, `click_ref`, `read_screen_text`, `click_text`).
- UIA first, MSAA for legacy Office dialogs, Windows OCR for canvas-style apps.
- Virtual display, focus/cursor guards, write scheduler, snapshot-scoped refs, unified error catalog.
- Multi-client shared instance: the first MCP process spawns an independent `--daemon` (Job breakaway,
  falls back to an in-process daemon); later processes join as thin proxies over a named pipe.
- CLI companion: `VScreenPilot.exe --cli env|snapshot|screen|status|stop`.

> 0.2.2 is a technical preview. Tool names and parameters are frozen, but 0.x may still contain
> breaking changes; read [CHANGELOG.md](CHANGELOG.md) before upgrading. For 0.2.2 as-built details see
> [技术文档.md](技术文档.md) and [docs/架构说明.md](docs/架构说明.md); 0.1.0 implementation
> details live in git history.

## Requirements

- Windows 10/11 x64, interactive desktop session.
- [Parsec VDD](https://github.com/parsec-cloud/parsec-vdd) installed once:
  `winget install --id Parsec.ParsecVDD -e`
- Runtime does **not** require administrator rights.
- .NET 10 SDK only when building from source.

## Install

**Installer (recommended).** Download `VScreenPilot-Setup-0.2.2.exe` from Releases and run it.
It installs per-user to `%LOCALAPPDATA%\Programs\VScreenPilot` (no administrator prompt) and can add
that folder to the current user `PATH`. Silent install:
`VScreenPilot-Setup-0.2.2.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP-`.
The installer is not code-signed yet; verify the SHA256 from the release before running.

**Portable.** Download `VScreenPilot-win-x64-v0.2.2.zip`, extract it to a stable folder and run
`VScreenPilot.exe`. Symbols ship separately as `VScreenPilot-symbols-v0.2.2.zip`.

**winget** (after the manifest is reviewed and merged):

```powershell
winget install --id VScreenPilot.VScreenPilot -e
```

From source:

```powershell
.\scripts\release.ps1 -Version 0.2.2   # portable zip + installer + symbols
```

## MCP client configuration

```json
{
  "mcpServers": {
    "vscreenpilot": {
      "command": "C:\\Users\\<you>\\AppData\\Local\\Programs\\VScreenPilot\\VScreenPilot.exe",
      "timeout": { "startup": 120000, "catalog": 60000, "execution": 600000 }
    }
  }
}
```

`run_script` is disabled by default; enable it with `--allow-script` only if you trust the model output.

## Documentation

- [Architecture](docs/架构说明.md)
- [Error catalog](docs/errors.md)
- [Audit reports](docs/audit/第二轮测试报告.md)

## Limits

- Windows 10/11 x64 only; one interactive user session.
- The driver install triggers one UAC prompt; mounting/removing the display does not.
- Chromium/Electron/WebView2 may ignore window messages; for click/click_ref/click_text use `channel=input` or `fallback_input=true`, and for gesture use `mode=input`.
- Elevated windows cannot be automated from a medium-integrity process (UIPI).
- Canvas apps (PowerPoint, games) expose little UIA text; use OCR/visual tools.
- Screenshots are written to `%LOCALAPPDATA%\VScreenPilot\artifacts\<profile>\` and kept for 7 days
  by default (`VSP_ARTIFACT_RETENTION_DAYS`, `0` disables cleanup).

## Security

VScreenPilot is **not a sandbox** and runs with your user token. See [SECURITY.md](SECURITY.md).

## License

MIT. Third-party components and driver notices: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
