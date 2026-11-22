# Security Policy

## Supported versions

Only the latest commit on `main` is supported.

## Reporting a vulnerability

Open a private security advisory on the repository, or contact the maintainers directly.
Please include:

- VScreenPilot version / commit and `VScreenPilot.exe --version` output
- Windows build, display topology, and whether the session is elevated
- Reproduction steps and the `request_id` from the failing tool call
- Relevant lines from `%LOCALAPPDATA%\VScreenPilot\logs\`

## Security model (important)

- VScreenPilot is **not a sandbox**. It runs with the current user token and can automate windows it has launched or attached; input is confined to the virtual display, but a successfully attached app is moved there.
- `run_script` executes arbitrary PowerShell when enabled (`--allow-script`). The built-in token blacklist only prevents accidental misuse; it is **not** a security boundary. Do not enable it for untrusted model output.
- `VSP_APP_ALLOWLIST` restricts which executables can be launched via `path`; it does not restrict `window`/`pid` attach, nor what an allowed app can do.
- Input is injected into the interactive desktop. Elevated applications cannot be automated from a medium-integrity server (UIPI).
- Credentials, tokens, and personal data may appear on screen. `snapshot` redacts email-like strings, but OCR and screenshots still capture whatever is visible. Use a dedicated VM/session for sensitive work.
- Multi-client sharing: MCP and control pipes use `PipeOptions.CurrentUserOnly`, and the server verifies the client process integrity level; mismatched high/low integrity is rejected by default. The pipe is still within the same-user trust boundary, so same-user processes can connect.
- All clients of one shared instance share a single virtual display; `ScreenLease` only serializes GUI operations, not security isolation. Use a separate profile or VM for untrusted projects.
- `VSP_ALLOW_INTEGRITY_MISMATCH=1` is for testing/troubleshooting only and disables the integrity check above. Do not set it in daily use or untrusted environments.

## Untrusted content

Tool results come from the desktop and may contain text written by third parties. Treat them as data, never as instructions.
