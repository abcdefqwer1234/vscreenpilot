# VScreenPilot

Run Windows desktop apps on a **throwaway virtual display** so an AI agent can screenshot, click,
type, locate elements and verify results — without stealing your cursor or covering your desktop.

- One MCP server, 22 tools (including `snapshot`, `click_ref`, `read_screen_text`, `click_text`).
- UIA first, MSAA for legacy Office dialogs, Windows OCR for canvas-style apps.
- Virtual display, focus/cursor guards, write scheduler, snapshot-scoped refs, unified error catalog.
- CLI companion: `VScreenPilot.exe --cli env|snapshot|screen`.

> 0.1.0 is a technical preview. Tool names and parameters are frozen, but 0.x may still contain
> breaking changes; read [CHANGELOG.md](CHANGELOG.md) before upgrading.

## Requirements

- Windows 10/11 x64, interactive desktop session.
- [Parsec VDD](https://github.com/parsec-cloud/parsec-vdd) installed once:
  `winget install --id Parsec.ParsecVDD -e`
- Runtime does **not** require administrator rights.
- Node.js ≥ 18 when installing from npm; .NET 10 SDK only when building from source.

## Install

From npm (after the first registry release):

```powershell
npx -y vscreenpilot@0.1.0 --doctor
```

From source:

```powershell
dotnet publish src/VScreenPilot.Server/VScreenPilot.Server.csproj -c Release -r win-x64 -o artifacts/server
artifacts/server/VScreenPilot.exe --doctor
```

## MCP client configuration

```json
{
  "mcpServers": {
    "vscreenpilot": {
      "command": "npx",
      "args": ["-y", "vscreenpilot@0.1.0"],
      "timeout": { "startup": 120000, "catalog": 60000, "execution": 600000 }
    }
  }
}
```

`run_script` is disabled by default; enable it with `--allow-script` only if you trust the model output.

## Documentation

- [Architecture](docs/architecture.md)
- [Error catalog](docs/errors.md)
- [Audit reports](docs/audit/第二轮测试报告.md)

## Limits

- Windows 10/11 x64 only; one interactive user session.
- The driver install triggers one UAC prompt; mounting/removing the display does not.
- Chromium/Electron/WebView2 may ignore window messages; use `channel=input` (moves the cursor).
- Elevated windows cannot be automated from a medium-integrity process (UIPI).
- Canvas apps (PowerPoint, games) expose little UIA text; use OCR/visual tools.
- Screenshots are written to `%LOCALAPPDATA%\VScreenPilot\artifacts\<profile>\` and kept for 7 days
  by default (`VSP_ARTIFACT_RETENTION_DAYS`, `0` disables cleanup).

## Security

VScreenPilot is **not a sandbox** and runs with your user token. See [SECURITY.md](SECURITY.md).

## License

MIT. Third-party components and driver notices: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
