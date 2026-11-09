# vscreenpilot

Windows 虚拟屏 MCP Server 的 npm 启动包。启动器只负责平台路由和透传 stdio，原生二进制位于 `dist/win32-x64/VScreenPilot.exe`。

仅支持 **Windows 10/11 x64**。运行期不需要管理员权限；首次使用需要安装一次 Parsec VDD（会触发一次 UAC）。

> 当前包用于本地 `npm pack` + 临时安装验证；以下 `npx` 示例在包发布到 registry 后生效。

## 安装前

```powershell
# 在包目录内执行（npx 安装后位于 node_modules/vscreenpilot）
npm run install-driver

# 或直接调用
powershell -NoProfile -ExecutionPolicy Bypass -File .\node_modules\vscreenpilot\scripts\install-driver.ps1
```

## 接入 OpenCode V2

```jsonc
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "servers": {
      "vscreenpilot": {
        "type": "local",
        "command": ["npx", "-y", "vscreenpilot@0.1.0"],
        "codemode": false,
        "timeout": {
          "startup": 120000,
          "catalog": 60000,
          "execution": 600000
        }
      }
    }
  }
}
```

`codemode: false` 是为了让 `capture_screen` 的 image content 直接进入模型上下文；如果不需要看图，可以去掉。

本地开发也可以直接指向启动器：

```jsonc
"command": ["node", "E:\\path\\to\\VScreenPilot\\npm\\bin\\vscreenpilot.js"]
```

## 安全默认值

- `run_script` 默认关闭。需要时用 `--allow-script` 或 `VSP_ALLOW_SCRIPT=1` 显式开启：

```jsonc
"command": ["npx", "-y", "vscreenpilot@0.1.0", "--allow-script"]
```

- `VSP_APP_ALLOWLIST` 可选，用分号分隔目录，限制 `launch_or_place_app` 通过 `path` 只能启动这些目录下的 exe；按 `window` 标题接管不受此限制。启用后请传完整路径：

```jsonc
"environment": {
  "VSP_APP_ALLOWLIST": "C:\\Program Files\\MyApp;D:\\Tools"
}
```

## 平台与诊断

- 仅支持 `win32-x64`；其他平台会在 stderr 返回 `E_UNSUPPORTED_PLATFORM`（exit 78）。
- `vscreenpilot --version` 输出包版本。
- `vscreenpilot --help` 输出用法。
- `vscreenpilot --doctor` 以 JSON 输出驱动、虚拟屏、显示器数量、`run_script_enabled`、`app_allowlist`、日志路径等诊断信息；驱动未就绪时 exit 1。
