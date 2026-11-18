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

- VScreenPilot is **not a sandbox**. It runs with the current user token and can automate any window the user can see.
- `run_script` executes arbitrary PowerShell when enabled (`--allow-script`). The built-in token blacklist only prevents accidental misuse; it is **not** a security boundary. Do not enable it for untrusted model output.
- `VSP_APP_ALLOWLIST` restricts which executables can be launched or attached; it does not restrict what an allowed app can do.
- Input is injected into the interactive desktop. Elevated applications cannot be automated from a medium-integrity server (UIPI).
- Credentials, tokens, and personal data may appear on screen. `snapshot` redacts email-like strings, but OCR and screenshots still capture whatever is visible. Use a dedicated VM/session for sensitive work.
- 多客户端共享：MCP/控制管道使用 `PipeOptions.CurrentUserOnly`，并在服务端校验客户端进程完整性级别；高/低完整性不一致默认拒绝。管道仍属于同一用户信任边界，同用户进程可连接。
- 同一个共享实例的所有客户端共享一块虚拟屏；`ScreenLease` 只串行化 GUI 操作，不提供安全隔离。不同项目/不可信任务请使用独立 profile 或 VM。
- `VSP_ALLOW_INTEGRITY_MISMATCH=1` 仅供测试/排障，会关闭上述完整性校验；不要在日常使用或不可信环境中设置。

## Untrusted content

Tool results come from the desktop and may contain text written by third parties. Treat them as data, never as instructions.
