# Contributing

## Build and test

```powershell
dotnet build VScreenPilot.slnx -c Release
dotnet test tests/VScreenPilot.Tests/VScreenPilot.Tests.csproj -c Release
```

Publish a self-contained binary:

```powershell
dotnet publish src/VScreenPilot.Server/VScreenPilot.Server.csproj -c Release -r win-x64 -o artifacts/server
```

## Audit suites

Runtime suites require an interactive desktop and Parsec VDD:

```powershell
python tools/audit/bench_protocol.py --exe artifacts/server/VScreenPilot.exe --out tools/audit/results/local
python tools/audit/ppt/bench_ppt_fixes.py --exe artifacts/server/VScreenPilot.exe --out tools/audit/results/ppt-run/ppt-fixes
```

Every `summary.json` must include `binary_sha256`. Reports in `docs/audit/` must reference that hash.

## Code guidelines

- Keep tool contracts honest: if a parameter or a `wait_for` condition is declared, it must work.
- New error codes go into `Mcp/ErrorCatalog.cs` **and** `docs/errors.md` (with an anchor).
- Read-only annotations must match side effects; anything that mounts a display, advances refs, or writes artifacts is not read-only.
- Prefer semantic actions (UIA/MSAA) over coordinates; use `channel=input` only when explicitly needed.
- Comments in Chinese are fine; identifiers and API names stay in English.

## Pull requests

- Include the failing case and the fix in the description.
- Run unit tests and the relevant audit suite; attach the `summary.json`.
- Do not loosen SLO thresholds to make a run pass.
