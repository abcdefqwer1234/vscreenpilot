# VScreenPilot

Run Windows desktop apps on a **throwaway virtual display** so an AI agent can screenshot, click,
type, locate elements and verify results — without stealing your cursor or covering your desktop.

- One MCP server, 22 tools (including `snapshot`, `click_ref`, `read_screen_text`, `click_text`).
- UIA first, MSAA for legacy Office dialogs, Windows OCR for canvas-style apps.
- Virtual display, focus/cursor guards, write scheduler, snapshot-scoped refs, unified error catalog.
- CLI companion: `VScreenPilot.exe --cli env|snapshot|screen`.

## Quick start

```powershell
dotnet publish src/VScreenPilot.Server/VScreenPilot.Server.csproj -c Release -r win-x64 -o artifacts/server
artifacts/server/VScreenPilot.exe --doctor
```

Add it to your MCP client (stdio):

```json
{
  "mcpServers": {
    "vscreenpilot": {
      "command": "C:/path/to/VScreenPilot.exe"
    }
  }
}
```

## Documentation

- [Architecture](docs/architecture.md)
- [Error catalog](docs/errors.md)
- [Audit reports](docs/audit/09-实际测试报告-PPT.md)
- [Fix verification](docs/audit/12-修复复验报告.md)

## Security

VScreenPilot is **not a sandbox** and runs with your user token. See [SECURITY.md](SECURITY.md).

## License

MIT
