# Third-Party Notices

VScreenPilot 以 MIT 许可发布（见 `LICENSE`）。发布产物包含以下第三方组件：

| 组件 | 用途 | 许可证 |
| --- | --- | --- |
| .NET 10 Runtime（self-contained） | 运行时 | MIT，Copyright (c) .NET Foundation and Contributors |
| Microsoft.Extensions.Hosting | 宿主与依赖注入 | MIT，Copyright (c) .NET Foundation and Contributors |
| ModelContextProtocol（C# SDK） | MCP 协议实现 | MIT，Copyright (c) Model Context Protocol a Series of LF Projects, LLC |
| Interop.UIAutomationClient | UI Automation COM 互操作 | MIT，Copyright (c) Microsoft Corporation |

以下组件**不随包分发**，由用户自行安装：

- **Parsec Virtual Display Driver (Parsec VDD)**：通过 `winget install --id Parsec.ParsecVDD`
  安装，受其自身许可约束。VScreenPilot 只在运行期调用其用户态 IOCTL，不再分发驱动文件。

## MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
