using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Infrastructure;
using VScreenPilot.Session;

namespace VScreenPilot.Mcp;

internal sealed class McpServer
{
    private static readonly JsonDocument EmptyDocument = JsonDocument.Parse("{}");

    private static readonly string[] SupportedProtocolVersions =
    {
        "2025-06-18",
        "2025-03-26",
        "2024-11-05",
    };

    private readonly SessionManager _session;
    private readonly LogRing _log;
    private readonly Dictionary<string, ToolDefinition> _tools;
    private readonly JsonArray _toolList;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeCalls = new();
    private readonly ReaderWriterLockSlim _stateLock = new(LockRecursionPolicy.NoRecursion);
    private int _stalledTools;
    private readonly object _writeGate = new();
    private TextWriter _stdout = TextWriter.Null;
    private volatile bool _initialized;

    private sealed record ToolDefinition(
        string Name,
        string Description,
        JsonNode InputSchema,
        Func<JsonElement, ToolResponse> Handler);

    public McpServer(SessionManager session, LogRing log)
    {
        _session = session;
        _log = log;
        _tools = BuildTools();
        _toolList = new JsonArray();

        foreach (ToolDefinition tool in _tools.Values)
        {
            var entry = new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["inputSchema"] = tool.InputSchema.DeepClone(),
            };

            JsonObject? annotations = AnnotationsFor(tool.Name);
            if (annotations is not null)
            {
                entry["annotations"] = annotations;
            }

            _toolList.Add((JsonNode)entry);
        }
    }

    /// <summary>
    /// MCP tool annotations：让客户端在权限/审批层区分只读、破坏性和开放世界工具。
    /// 这只是提示，真正的安全边界仍由服务端校验。
    /// </summary>
    private static JsonObject? AnnotationsFor(string name) => name switch
    {
        "get_environment" or "find_element" or "get_ui_tree" or "get_element_text"
            or "match_visual" or "assert_region" or "wait_for" or "get_log" =>
            new JsonObject
            {
                ["readOnlyHint"] = true,
                ["destructiveHint"] = false,
                ["openWorldHint"] = false,
            },

        "capture_screen" => new JsonObject
        {
            ["readOnlyHint"] = false,
            ["destructiveHint"] = false,
            ["openWorldHint"] = false,
        },

        "launch_or_place_app" => new JsonObject
        {
            ["readOnlyHint"] = false,
            ["destructiveHint"] = false,
            ["openWorldHint"] = true,
        },

        "invoke_element" or "click" or "gesture" or "type_text" or "press_keys"
            or "set_element_value" or "release_environment" or "run_script" =>
            new JsonObject
            {
                ["readOnlyHint"] = false,
                ["destructiveHint"] = true,
                ["openWorldHint"] = true,
            },

        _ => null,
    };

    public int Run()
    {
        using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false))
        {
            AutoFlush = true,
        };

        _stdout = stdout;
        _log.Info($"MCP stdio 服务启动 (pid={Environment.ProcessId})");

        string? line;
        while ((line = stdin.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                HandleLine(line);
            }
            catch (JsonException exception)
            {
                _log.Warn($"收到非法 JSON: {exception.Message}");
                WriteError(null, -32700, "JSON 解析失败");
            }
            catch (Exception exception)
            {
                _log.Error("处理消息失败", exception);
            }
        }

        _log.Info("stdin EOF，开始清理");

        // 通知所有在途工具取消，等待最多 3 秒后释放环境。
        foreach (CancellationTokenSource source in _activeCalls.Values)
        {
            try
            {
                source.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 已结束。
            }
        }

        var drainDeadline = DateTime.UtcNow.AddSeconds(3);
        while (!_activeCalls.IsEmpty && DateTime.UtcNow < drainDeadline)
        {
            Thread.Sleep(50);
        }

        if (!_activeCalls.IsEmpty)
        {
            _log.Warn("stdin EOF 时仍有工具在运行，超过 3s 未结束，继续退出");
        }

        _session.Dispose();
        return 0;
    }

    private void HandleLine(string line)
    {
        using var document = JsonDocument.Parse(line);
        JsonElement root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            WriteError(null, -32600, "JSON-RPC 消息必须是对象");
            return;
        }

        bool hasId = root.TryGetProperty("id", out JsonElement idElement);
        JsonNode? id = hasId ? JsonNode.Parse(idElement.GetRawText()) : null;

        if (!root.TryGetProperty("jsonrpc", out JsonElement jsonrpc) ||
            jsonrpc.ValueKind != JsonValueKind.String ||
            !string.Equals(jsonrpc.GetString(), "2.0", StringComparison.Ordinal))
        {
            if (hasId)
            {
                WriteError(id, -32600, "缺少或无效的 jsonrpc 版本（必须为 \"2.0\"）");
            }

            return;
        }

        if (!root.TryGetProperty("method", out JsonElement methodElement) ||
            methodElement.ValueKind != JsonValueKind.String)
        {
            if (hasId)
            {
                WriteError(id, -32600, "缺少 method");
            }

            return;
        }

        string method = methodElement.GetString()!;
        JsonElement parameters = root.TryGetProperty("params", out JsonElement p)
            ? p
            : EmptyDocument.RootElement;

        if (!hasId)
        {
            HandleNotification(method, parameters);
            return;
        }

        // MCP 要求先 initialize。ping 是唯一例外，便于客户端探测存活。
        if (!_initialized && method is not ("initialize" or "ping"))
        {
            WriteError(id, -32002, "服务器尚未初始化，请先发送 initialize");
            return;
        }

        switch (method)
        {
            case "initialize":
                HandleInitialize(id, parameters);
                break;

            case "ping":
                WriteResult(id, new JsonObject());
                break;

            case "tools/list":
                WriteResult(id, new JsonObject { ["tools"] = _toolList.DeepClone() });
                break;

            case "tools/call":
                HandleToolCall(id, idElement.GetRawText(), parameters);
                break;

            case "resources/list":
                WriteResult(id, new JsonObject { ["resources"] = new JsonArray() });
                break;

            case "resources/templates/list":
                WriteResult(id, new JsonObject { ["resourceTemplates"] = new JsonArray() });
                break;

            case "prompts/list":
                WriteResult(id, new JsonObject { ["prompts"] = new JsonArray() });
                break;

            case "logging/setLevel":
                WriteResult(id, new JsonObject());
                break;

            default:
                WriteError(id, -32601, $"未实现的方法: {method}");
                break;
        }
    }

    private void HandleNotification(string method, JsonElement parameters)
    {
        // notifications/initialized、notifications/cancelled 等无需响应。
        switch (method)
        {
            case "notifications/initialized":
                _log.Info("客户端初始化完成");
                break;

            case "notifications/cancelled":
                if (parameters.ValueKind == JsonValueKind.Object &&
                    parameters.TryGetProperty("requestId", out JsonElement requestId))
                {
                    string key = requestId.GetRawText();
                    if (_activeCalls.TryGetValue(key, out CancellationTokenSource? source))
                    {
                        source.Cancel();
                        _log.Info($"已取消请求 {key}");
                    }
                }

                break;
        }
    }

    private void HandleInitialize(JsonNode? id, JsonElement parameters)
    {
        string protocolVersion = SupportedProtocolVersions[^1];

        if (parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty("protocolVersion", out JsonElement version) &&
            version.ValueKind == JsonValueKind.String)
        {
            string requested = version.GetString() ?? string.Empty;
            protocolVersion = SupportedProtocolVersions.Contains(requested)
                ? requested
                : SupportedProtocolVersions[^1];
        }

        string client = "unknown";
        if (parameters.ValueKind == JsonValueKind.Object &&
            parameters.TryGetProperty("clientInfo", out JsonElement clientInfo) &&
            clientInfo.ValueKind == JsonValueKind.Object &&
            clientInfo.TryGetProperty("name", out JsonElement clientName))
        {
            client = clientName.GetString() ?? client;
        }

        _log.Info($"initialize: client={client}, protocol={protocolVersion}");
        _initialized = true;

        WriteResult(id, new JsonObject
        {
            ["protocolVersion"] = protocolVersion,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false },
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "vscreenpilot",
                ["version"] = BuildInfo.Version,
            },
            ["instructions"] =
                "虚拟屏自动化调试。MCP Server 启动时会在后台挂载 1280x720 虚拟屏（OpenCode 会话级常驻）；" +
                "所有坐标/区域必须落在虚拟屏内，越界会被拒绝。优先用 find_element/get_ui_tree 拿 element_ref，" +
                "再用 invoke_element/set_element_value 做语义操作；像素定位用 match_visual。结束后调用 " +
                "release_environment 归还窗口，默认保留虚拟屏（keep_display=true），keep_display=false 才立即摘屏。" +
                "run_script 默认关闭，需服务端以 --allow-script 启动。",
        });
    }

    private void HandleToolCall(JsonNode? id, string idKey, JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty("name", out JsonElement nameElement) ||
            nameElement.ValueKind != JsonValueKind.String)
        {
            WriteError(id, -32602, "tools/call 缺少 name");
            return;
        }

        string name = nameElement.GetString()!;
        JsonElement arguments = parameters.TryGetProperty("arguments", out JsonElement args) &&
                                args.ValueKind == JsonValueKind.Object
            ? args.Clone()
            : EmptyDocument.RootElement.Clone();

        if (!_tools.TryGetValue(name, out ToolDefinition? tool))
        {
            WriteError(id, -32602, $"未知工具: {name}");
            return;
        }

        if (_activeCalls.ContainsKey(idKey))
        {
            WriteError(id, -32600, "重复的请求 id");
            return;
        }

        var source = new CancellationTokenSource();
        _activeCalls[idKey] = source;

        // 工具调用放到后台执行：读循环继续响应 ping/cancelled，长工具不会阻塞协议。
        _ = Task.Run(() => ExecuteToolAsync(id?.DeepClone(), idKey, tool, arguments, source));
    }

    private async Task ExecuteToolAsync(
        JsonNode? id,
        string idKey,
        ToolDefinition tool,
        JsonElement arguments,
        CancellationTokenSource source)
    {
        bool priority = PriorityTools.Contains(tool.Name);
        bool readOnly = ReadOnlyTools.Contains(tool.Name);
        int timeoutMs = TimeoutFor(tool.Name);

        try
        {
            if (!priority && Volatile.Read(ref _stalledTools) > 0)
            {
                WriteResult(id, new JsonObject
                {
                    ["content"] = new JsonArray
                    {
                        (JsonNode)new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = new JsonObject
                            {
                                ["code"] = "E_BUSY",
                                ["message"] = "上一个工具超时后仍在执行，拒绝新的工具调用",
                                ["retryable"] = false,
                                ["hint"] = "先调用 release_environment 或重启服务端",
                            }.ToJsonString(),
                        },
                    },
                    ["isError"] = true,
                });
                return;
            }

            Task<ToolResponse> handlerTask = Task.Run(() =>
            {
                bool lockTaken = false;

                try
                {
                    lockTaken = readOnly
                        ? _stateLock.TryEnterReadLock(timeoutMs)
                        : _stateLock.TryEnterWriteLock(timeoutMs);

                    if (!lockTaken)
                    {
                        return ToolResponse.Error(
                            "E_BUSY",
                            "等待工具执行锁超时",
                            retryable: true,
                            hint: "稍后重试；长任务可先取消或释放环境",
                            stage: "lock");
                    }

                    if (source.IsCancellationRequested)
                    {
                        return ToolResponse.Error("E_CANCELLED", "请求已取消");
                    }

                    if (!readOnly)
                    {
                        _session.SetCancellation(source);
                    }

                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();

                    try
                    {
                        ToolResponse response = tool.Handler(arguments);
                        _log.Info($"工具 {tool.Name} 完成: {stopwatch.Elapsed.TotalMilliseconds:F1}ms");
                        return response;
                    }
                    catch (ToolException exception)
                    {
                        _log.Warn(
                            $"工具 {tool.Name} 失败: {exception.Code} {exception.Message} " +
                            $"({stopwatch.Elapsed.TotalMilliseconds:F1}ms)");
                        return ToolResponse.FromException(exception);
                    }
                    catch (Exception exception)
                    {
                        _log.Error(
                            $"工具 {tool.Name} 异常 ({stopwatch.Elapsed.TotalMilliseconds:F1}ms)",
                            exception);
                        return ToolResponse.Error("E_INTERNAL", exception.Message);
                    }
                    finally
                    {
                        if (!readOnly)
                        {
                            _session.SetCancellation(null);
                        }
                    }
                }
                finally
                {
                    if (lockTaken)
                    {
                        if (readOnly)
                        {
                            _stateLock.ExitReadLock();
                        }
                        else
                        {
                            _stateLock.ExitWriteLock();
                        }
                    }
                }
            });

            ToolResponse response;

            try
            {
                response = await handlerTask
                    .WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), source.Token)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                TrackStalled(handlerTask, tool.Name);
                response = ToolResponse.Error(
                    "E_TIMEOUT",
                    $"工具 {tool.Name} 超过 {timeoutMs}ms 未完成",
                    retryable: false,
                    hint: "上一次调用可能仍在执行；先调用 release_environment，必要时重启服务端",
                    stage: "tool");
            }
            catch (OperationCanceledException)
            {
                TrackStalled(handlerTask, tool.Name);
                throw;
            }

            if (source.IsCancellationRequested)
            {
                _log.Info($"工具 {tool.Name} 已取消，丢弃结果");
                return;
            }

            WriteResult(id, new JsonObject
            {
                ["content"] = response.Content.DeepClone(),
                ["isError"] = response.IsError,
            });
        }
        catch (OperationCanceledException)
        {
            // 已取消：按 MCP 约定不返回响应。
        }
        finally
        {
            _activeCalls.TryRemove(idKey, out _);
            source.Dispose();
        }
    }

    private void TrackStalled(Task<ToolResponse> task, string toolName)
    {
        if (task.IsCompleted)
        {
            return;
        }

        Interlocked.Increment(ref _stalledTools);
        _ = task.ContinueWith(
            completed =>
            {
                Interlocked.Decrement(ref _stalledTools);
                if (completed.IsFaulted)
                {
                    _log.Error($"超时工具 {toolName} 最终异常", completed.Exception);
                }
                else
                {
                    _log.Warn($"超时工具 {toolName} 已结束，恢复可调用状态");
                }
            },
            TaskScheduler.Default);
    }

    private static readonly HashSet<string> PriorityTools = new(StringComparer.Ordinal)
    {
        "release_environment", "get_log", "get_environment",
    };

    private static readonly HashSet<string> ReadOnlyTools = new(StringComparer.Ordinal)
    {
        "get_environment", "capture_screen", "get_element_text", "get_log",
        "match_visual", "assert_region",
    };

    private static readonly Dictionary<string, int> ToolTimeouts = new(StringComparer.Ordinal)
    {
        ["get_environment"] = 20000,
        ["capture_screen"] = 30000,
        ["find_element"] = 30000,
        ["get_ui_tree"] = 30000,
        ["invoke_element"] = 30000,
        ["set_element_value"] = 30000,
        ["get_element_text"] = 20000,
        ["click"] = 20000,
        ["gesture"] = 30000,
        ["type_text"] = 120000,
        ["press_keys"] = 30000,
        ["wait_for"] = 130000,
        ["assert_region"] = 30000,
        ["match_visual"] = 60000,
        ["launch_or_place_app"] = 90000,
        ["release_environment"] = 90000,
        ["get_log"] = 15000,
        ["run_script"] = 130000,
    };

    private static int TimeoutFor(string tool) =>
        ToolTimeouts.TryGetValue(tool, out int timeout) ? timeout : 120000;

    private void WriteResult(JsonNode? id, JsonNode result)
    {
        var message = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["result"] = result,
        };

        WriteMessage(message);
    }

    private void WriteError(JsonNode? id, int code, string message)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        };

        WriteMessage(response);
    }

    private int _writeFailed;

    private void WriteMessage(JsonObject message)
    {
        lock (_writeGate)
        {
            try
            {
                _stdout.WriteLine(message.ToJsonString());
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                if (Interlocked.Exchange(ref _writeFailed, 1) == 0)
                {
                    _log.Warn($"stdout 已关闭，后续响应被丢弃: {exception.Message}");
                }
            }
        }
    }

    private Dictionary<string, ToolDefinition> BuildTools()
    {
        var tools = new Dictionary<string, ToolDefinition>(StringComparer.Ordinal);

        Add(tools, "get_environment", "查看虚拟屏、显示器、前台窗口与会话状态。foreground 只在本会话窗口位于前台时返回标题/PID，避免泄露用户主屏窗口信息。ensure=true 时先修复/挂载虚拟屏。", """
            {"type":"object","properties":{"ensure":{"type":"boolean","description":"默认 false；true 时先 EnsureEnvironment"}}}
            """, GetEnvironment);

        Add(tools, "launch_or_place_app", "启动应用或接管窗口，移动到虚拟屏并最大化。默认 new_instance=true：优先独立实例/新窗口（Edge 使用独立临时配置）。Word 默认 ensure_document=true：启动后用 Ctrl+N 确保有文档。", """
            {"type":"object","properties":{"path":{"type":"string","description":"应用可执行文件路径"},"window":{"type":"string","description":"按窗口标题接管（会忽略 new_instance）"},"args":{"type":"string"},"wait_ms":{"type":"integer","minimum":1000,"maximum":60000},"new_instance":{"type":"boolean","description":"默认 true：独立实例/新窗口；false：接管已运行窗口"},"ensure_document":{"type":"boolean","description":"仅 Word：默认 true，无文档时自动 Ctrl+N"}}}
            """, LaunchOrPlace);

        Add(tools, "capture_screen", "截取虚拟屏（或虚拟屏内的指定区域），返回 Base64 PNG。区域越界会被拒绝。save_path 只允许工作目录/临时目录下的 .png；meta.baseline 使用未缩放的源尺寸。", """
            {"type":"object","properties":{"region":{"type":"object","properties":{"x":{"type":"integer"},"y":{"type":"integer"},"width":{"type":"integer"},"height":{"type":"integer"}},"required":["x","y","width","height"]},"scale":{"type":"number","minimum":0.1,"maximum":1},"save_path":{"type":"string","description":"仅工作目录或系统临时目录内的 .png"}}}
            """, CaptureScreen);

        Add(tools, "find_element", "用 UI Automation 查找控件，返回 element_ref、边界与状态。reference 用于 invoke_element / set_element_value / get_ui_tree 的 root。默认只查最后一个存活应用，可用 pid 指定。role=document_body 可映射 Word 正文（automation_id=Body）。", """
            {"type":"object","properties":{"name":{"type":"string"},"name_match":{"type":"string","enum":["exact","contains","regex"],"description":"默认 exact"},"control_type":{"type":"string"},"automation_id":{"type":"string"},"role":{"type":"string","enum":["document","document_body"],"description":"语义别名：document_body 映射 Edit + Body"},"prefer":{"type":"string","enum":["auto","first","focused","largest","last"],"description":"默认 auto：焦点优先，其次面积最大"},"index":{"type":"integer","minimum":0,"description":"候选序号，默认 0"},"all":{"type":"boolean","description":"返回全部候选"},"max_results":{"type":"integer","minimum":1,"maximum":200},"include_offscreen":{"type":"boolean","description":"默认 false：只返回可见控件"},"pid":{"type":"integer","description":"可选：指定本会话内某个应用的进程号"}}}
            """, FindElement);

        Add(tools, "get_ui_tree", "读取当前会话窗口的裁剪语义树；默认只保留交互控件，超出节点/时间预算时返回 truncated。节点带 element_ref。interactive_only=false 时仍会剪掉滚动条/分隔条等噪声。", """
            {"type":"object","properties":{"max_depth":{"type":"integer","minimum":1,"maximum":10,"description":"默认 3"},"interactive_only":{"type":"boolean","description":"默认 true"},"max_nodes":{"type":"integer","minimum":1,"maximum":2000,"description":"默认 300"},"root":{"type":"string","description":"可选：以某个 element_ref 为子树根"},"pid":{"type":"integer","description":"可选：指定本会话内某个应用的进程号"}}}
            """, GetUiTree);

        Add(tools, "invoke_element", "按 element_ref 触发控件。默认 auto 依次尝试 Invoke/Toggle/SelectionItem/ExpandCollapse/Legacy；可用 pattern 显式指定。不移动光标；无可用模式时返回 E_PATTERN_UNSUPPORTED。", """
            {"type":"object","properties":{"ref":{"type":"string"},"pattern":{"type":"string","enum":["auto","invoke","toggle","select","expand","legacy","default"],"description":"默认 auto；default 表示聚焦后发送 Space+Enter/Enter（用于树节点打开）"},"allow_click":{"type":"boolean","description":"默认 false；true 时允许回退为控件中心的消息点击（仍不移动光标）"}},"required":["ref"]}
            """, InvokeElement);

        Add(tools, "set_element_value", "按 element_ref 写值。默认 ValuePattern；allow_keyboard=true 才切前台并分块键入。mode=auto 时文档类控件默认 append，普通控件 insert（insert 是整值写入，已有内容时必须 allow_destructive=true）；replace 必须显式 allow_destructive=true。", """
            {"type":"object","properties":{"ref":{"type":"string"},"value":{"type":"string"},"allow_keyboard":{"type":"boolean","description":"默认 false"},"mode":{"type":"string","enum":["auto","append","insert","replace"],"description":"默认 auto：文档 append，普通控件 insert"},"allow_destructive":{"type":"boolean","description":"默认 false；insert 覆盖非空内容或 replace 时必须为 true"}},"required":["ref","value"]}
            """, SetElementValue);

        Add(tools, "get_element_text", "读取文本控件的正文：优先 TextPattern（Word 正文），其次 ValuePattern。用于写值/输入后的可靠校验。", """
            {"type":"object","properties":{"ref":{"type":"string"},"max_chars":{"type":"integer","minimum":1,"maximum":65536,"description":"默认 4096"}},"required":["ref"]}
            """, GetElementText);

        Add(tools, "click", "在虚拟桌面坐标点击。默认不移动系统光标（UIA/窗口消息）；input 模式才使用 SendInput。坐标必须属于本会话应用，越界/其他窗口会被拒绝。keep_foreground=true 保留目标焦点，供菜单/弹出层操作。", """
            {"type":"object","properties":{"x":{"type":"integer"},"y":{"type":"integer"},"button":{"type":"string","enum":["left","right","middle"]},"clicks":{"type":"integer","minimum":1,"maximum":10},"mode":{"type":"string","enum":["auto","uia","message","input"],"description":"默认 auto：UIA → 窗口消息，绝不移动光标；input 显式接受光标跳变"},"keep_foreground":{"type":"boolean","description":"默认 false；true 时不归还用户前台，便于连续操作菜单"}},"required":["x","y"]}
            """, Click);

        Add(tools, "gesture", "复合鼠标动作：drag / scroll / move。默认不移动系统光标；input 模式才使用 SendInput。目标必须属于本会话应用。", """
            {"type":"object","properties":{"type":{"type":"string","enum":["drag","scroll","move"]},"from":{"type":"object","properties":{"x":{"type":"integer"},"y":{"type":"integer"}},"required":["x","y"]},"to":{"type":"object","properties":{"x":{"type":"integer"},"y":{"type":"integer"}},"required":["x","y"]},"dx":{"type":"integer"},"dy":{"type":"integer"},"button":{"type":"string","enum":["left","right","middle"]},"duration_ms":{"type":"integer","minimum":20,"maximum":5000},"mode":{"type":"string","enum":["auto","message","input"],"description":"默认 auto/message：窗口消息，不移动光标；input 显式接受光标跳变"}},"required":["type"]}
            """, Gesture);

        Add(tools, "type_text", "向当前焦点输入文本，支持非 ASCII。默认 16 字符分块并用 TextPattern/ValuePattern 回读确认；verify=false 改为一次写入的快速路径。多应用会话可用 pid 指定目标，默认最后一个存活应用。", """
            {"type":"object","properties":{"text":{"type":"string"},"pid":{"type":"integer","description":"可选：指定本会话内某个应用的进程号"},"verify":{"type":"boolean","description":"默认 true：分块回读校验；false 为一次写入的快速路径"},"chunk_chars":{"type":"integer","minimum":1,"maximum":256,"description":"分块字符数，默认 16"}},"required":["text"]}
            """, TypeText);

        Add(tools, "press_keys", "发送按键或组合键，例如 Ctrl+Shift+S。sequence 可在一次前台租约内发送多个按键（菜单导航）；keep_foreground=true 时不归还用户前台。多应用会话可用 pid 指定目标。", """
            {"type":"object","properties":{"keys":{"type":"string"},"sequence":{"type":"array","items":{"type":"string"},"description":"按顺序发送，例如 [\"Alt+D\",\"Down\",\"Enter\"]"},"keep_foreground":{"type":"boolean","description":"默认 false；true 时保留目标焦点"},"pid":{"type":"integer","description":"可选：指定本会话内某个应用的进程号"}}}
            """, PressKeys);

        Add(tools, "wait_for", "服务端等待条件满足，减少客户端轮询。condition.type 支持 element/color/window/pixel_diff/visual；visual 直接复用 match_visual 的 mode/target/region。window 默认只匹配本会话窗口（scope=tracked）。", """
            {"type":"object","properties":{"condition":{"type":"object","description":"type=element|color|window|pixel_diff|visual；visual 可带 mode/target/target_path/region/threshold/tolerance"},"timeout_ms":{"type":"integer","minimum":0,"maximum":120000}},"required":["condition"]}
            """, WaitFor);

        Add(tools, "assert_region", "区域断言：颜色覆盖率、与上一帧相比是否变化。changed 默认引用同区域最近一次 capture_screen；也可传 expectation.baseline_frame_id。baseline=last_same_region（默认）或 last 时断言未变化。", """
            {"type":"object","properties":{"region":{"type":"object","properties":{"x":{"type":"integer"},"y":{"type":"integer"},"width":{"type":"integer"},"height":{"type":"integer"}},"required":["x","y","width","height"]},"expectation":{"type":"object","description":"color/changed/baseline_frame_id/baseline=last_same_region|last"}},"required":["region","expectation"]}
            """, AssertRegion);

        Add(tools, "match_visual", "像素定位：color 找颜色连通区域，pixel 取单点颜色，template 用 Base64 PNG 或 target_path 模板匹配。只读取虚拟屏，越界会被拒绝。", """
            {"type":"object","properties":{"mode":{"type":"string","enum":["template","color","pixel"]},"target":{"description":"color: \"#RRGGBB\"；pixel: {x,y}；template: Base64 PNG"},"target_path":{"type":"string","description":"template 可选：工作目录/临时目录下的 .png 文件"},"region":{"type":"object","properties":{"x":{"type":"integer"},"y":{"type":"integer"},"width":{"type":"integer"},"height":{"type":"integer"}},"required":["x","y","width","height"]},"tolerance":{"type":"integer","minimum":0,"maximum":255,"description":"color 容差，默认 16"},"threshold":{"type":"number","minimum":0.3,"maximum":1,"description":"template 阈值，默认 0.88"},"max_results":{"type":"integer","minimum":1,"maximum":100},"min_pixels":{"type":"integer","minimum":1,"description":"color 最小连通像素数，默认 4"},"max_area_ratio":{"type":"number","minimum":0.01,"maximum":1,"description":"color 连通域最大面积占比，默认 1.0（不过滤）"},"exclude_border":{"type":"boolean","description":"color 是否丢弃接触区域边缘的连通域，默认 false"},"require_unique":{"type":"boolean","description":"template 命中多个位置时返回 E_MULTIPLE_MATCHES，默认 false"}},"required":["mode"]}
            """, MatchVisual);

        Add(tools, "release_environment", "结束调试：窗口复原、关闭本会话启动的应用。默认保留虚拟屏（会话级常驻）；on_dirty 控制未保存内容：force/discard/save/abort。", """
            {"type":"object","properties":{"keep_display":{"type":"boolean","description":"默认 true：保留虚拟屏到宿主退出；false：立即摘屏（会再次黑屏）"},"on_dirty":{"type":"string","enum":["force","discard","save","abort"],"description":"默认 force：超时强杀；discard：点“不保存”；save：点“保存”；abort：保留窗口"}}}
            """, ReleaseEnvironment);

        Add(tools, "get_log", "读取近期日志与最后错误。source=memory 读当前进程；file 读日志文件（重启后仍可取证）；both 同时返回。", """
            {"type":"object","properties":{"tail":{"type":"integer","minimum":1,"maximum":500},"source":{"type":"string","enum":["memory","file","both"],"description":"默认 memory"}}}
            """, GetLog);

        Add(tools, "run_script", "以当前用户权限执行临时 PowerShell 脚本（超时杀进程树）。默认关闭；需以 --allow-script 或 VSP_ALLOW_SCRIPT=1 启动。", """
            {"type":"object","properties":{"code":{"type":"string"},"timeout_ms":{"type":"integer","minimum":1000,"maximum":120000}},"required":["code"]}
            """, RunScript);

        return tools;
    }

    private static void Add(
        Dictionary<string, ToolDefinition> tools,
        string name,
        string description,
        string schemaJson,
        Func<JsonElement, ToolResponse> handler)
    {
        tools[name] = new ToolDefinition(name, description, JsonNode.Parse(schemaJson)!, handler);
    }

    private ToolResponse GetEnvironment(JsonElement arguments) =>
        ToolResponse.Text(_session.GetEnvironment(OptionalBool(arguments, "ensure", false)));

    private ToolResponse LaunchOrPlace(JsonElement arguments)
    {
        string? path = OptionalString(arguments, "path");
        string? window = OptionalString(arguments, "window");
        string? args = OptionalString(arguments, "args");
        int waitMs = OptionalInt(arguments, "wait_ms", 15000);
        bool newInstance = OptionalBool(arguments, "new_instance", true);

        return ToolResponse.Text(
            _session.LaunchOrPlaceApp(
                path, window, args, waitMs, newInstance,
                OptionalBool(arguments, "ensure_document", true)));
    }

    private ToolResponse CaptureScreen(JsonElement arguments)
    {
        int? x = null, y = null, width = null, height = null;

        if (arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("region", out JsonElement region))
        {
            if (region.ValueKind != JsonValueKind.Object)
            {
                throw new ToolException("E_INVALID_ARG", "region 必须是对象");
            }

            x = RequiredInt(region, "x");
            y = RequiredInt(region, "y");
            width = RequiredInt(region, "width");
            height = RequiredInt(region, "height");
        }

        double scale = OptionalDouble(arguments, "scale", 1.0);
        string? savePath = OptionalString(arguments, "save_path");

        (byte[] png, JsonObject meta) = _session.CaptureScreen(x, y, width, height, scale, savePath);

        return ToolResponse.Text(meta).AddImage(png);
    }

    private ToolResponse FindElement(JsonElement arguments)
    {
        string? name = OptionalString(arguments, "name");
        string? controlType = OptionalString(arguments, "control_type");
        string? automationId = OptionalString(arguments, "automation_id");
        string nameMatch = OptionalString(arguments, "name_match") ?? "exact";
        int index = OptionalInt(arguments, "index", 0);
        bool all = OptionalBool(arguments, "all", false);
        int maxResults = OptionalInt(arguments, "max_results", 20);
        bool includeOffscreen = OptionalBool(arguments, "include_offscreen", false);
        int? pid = OptionalNullableInt(arguments, "pid");
        string? role = OptionalString(arguments, "role");
        string prefer = OptionalString(arguments, "prefer") ?? "auto";

        return ToolResponse.Text(_session.FindElement(
            name, controlType, automationId, nameMatch, index, all, maxResults,
            includeOffscreen, pid, role, prefer));
    }

    private ToolResponse GetUiTree(JsonElement arguments)
    {
        int? maxDepth = OptionalNullableInt(arguments, "max_depth");
        bool? interactiveOnly = OptionalNullableBool(arguments, "interactive_only");
        int? maxNodes = OptionalNullableInt(arguments, "max_nodes");
        string? root = OptionalString(arguments, "root");
        int? pid = OptionalNullableInt(arguments, "pid");

        return ToolResponse.Text(_session.GetUiTree(maxDepth, interactiveOnly, maxNodes, root, pid));
    }

    private ToolResponse InvokeElement(JsonElement arguments)
    {
        string reference = OptionalString(arguments, "ref")
            ?? throw new ToolException("E_INVALID_ARG", "invoke_element 需要 ref");
        bool allowClick = OptionalBool(arguments, "allow_click", false);
        string? pattern = OptionalString(arguments, "pattern");

        return ToolResponse.Text(_session.InvokeElement(reference, allowClick, pattern));
    }

    private ToolResponse SetElementValue(JsonElement arguments)
    {
        string reference = OptionalString(arguments, "ref")
            ?? throw new ToolException("E_INVALID_ARG", "set_element_value 需要 ref");
        string value = OptionalString(arguments, "value")
            ?? throw new ToolException("E_INVALID_ARG", "set_element_value 需要 value");
        bool allowKeyboard = OptionalBool(arguments, "allow_keyboard", false);
        string? mode = OptionalString(arguments, "mode");
        bool allowDestructive = OptionalBool(arguments, "allow_destructive", false);

        return ToolResponse.Text(_session.SetElementValue(
            reference, value, allowKeyboard, mode, allowDestructive));
    }

    private ToolResponse GetElementText(JsonElement arguments)
    {
        string reference = OptionalString(arguments, "ref")
            ?? throw new ToolException("E_INVALID_ARG", "get_element_text 需要 ref");
        int maxChars = OptionalInt(arguments, "max_chars", 4096);

        return ToolResponse.Text(_session.GetElementText(reference, maxChars));
    }

    private ToolResponse Click(JsonElement arguments)
    {
        int x = RequiredCoordinate(arguments, "x");
        int y = RequiredCoordinate(arguments, "y");

        string button = OptionalString(arguments, "button") ?? "left";
        int clicks = OptionalInt(arguments, "clicks", 1);
        string mode = OptionalString(arguments, "mode") ?? "auto";
        bool keepForeground = OptionalBool(arguments, "keep_foreground", false);

        return ToolResponse.Text(
            _session.Click(x, y, button, clicks, mode, keepForeground));
    }

    private ToolResponse Gesture(JsonElement arguments) =>
        ToolResponse.Text(_session.Gesture(arguments));

    private ToolResponse TypeText(JsonElement arguments)
    {
        string text = OptionalString(arguments, "text")
            ?? throw new ToolException("E_INVALID_ARG", "type_text 需要 text");
        int? pid = OptionalNullableInt(arguments, "pid");
        bool verify = OptionalBool(arguments, "verify", true);
        int chunkChars = OptionalInt(arguments, "chunk_chars", 16);

        return ToolResponse.Text(_session.TypeText(text, pid, verify, chunkChars));
    }

    private ToolResponse PressKeys(JsonElement arguments)
    {
        bool keepForeground = OptionalBool(arguments, "keep_foreground", false);
        int? pid = OptionalNullableInt(arguments, "pid");

        if (arguments.TryGetProperty("sequence", out JsonElement sequence) &&
            sequence.ValueKind == JsonValueKind.Array)
        {
            var keys = new List<string>();

            foreach (JsonElement item in sequence.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    throw new ToolException("E_INVALID_ARG", "sequence 必须是字符串数组");
                }

                keys.Add(item.GetString() ?? string.Empty);
            }

            return ToolResponse.Text(_session.PressKeySequence(keys, keepForeground, pid));
        }

        string keysText = OptionalString(arguments, "keys")
            ?? throw new ToolException("E_INVALID_ARG", "press_keys 需要 keys 或 sequence");

        return ToolResponse.Text(_session.PressKeys(keysText, keepForeground, pid));
    }

    private ToolResponse WaitFor(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("condition", out JsonElement condition) ||
            condition.ValueKind != JsonValueKind.Object)
        {
            throw new ToolException("E_INVALID_ARG", "wait_for 需要 condition 对象");
        }

        int timeoutMs = OptionalInt(arguments, "timeout_ms", 5000);
        return ToolResponse.Text(_session.WaitFor(condition, timeoutMs));
    }

    private ToolResponse AssertRegion(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("region", out JsonElement region) ||
            region.ValueKind != JsonValueKind.Object)
        {
            throw new ToolException("E_INVALID_ARG", "assert_region 需要 region");
        }

        if (!arguments.TryGetProperty("expectation", out JsonElement expectation) ||
            expectation.ValueKind != JsonValueKind.Object)
        {
            throw new ToolException("E_INVALID_ARG", "assert_region 需要 expectation");
        }

        return ToolResponse.Text(_session.AssertRegion(region, expectation));
    }

    private ToolResponse MatchVisual(JsonElement arguments) =>
        ToolResponse.Text(_session.MatchVisual(arguments));

    private ToolResponse ReleaseEnvironment(JsonElement arguments)
    {
        bool keepDisplay = OptionalBool(arguments, "keep_display", true);
        string onDirty = OptionalString(arguments, "on_dirty") ?? "force";
        return ToolResponse.Text(_session.ReleaseEnvironment(keepDisplay, onDirty));
    }

    private ToolResponse GetLog(JsonElement arguments)
    {
        int tail = Math.Clamp(OptionalInt(arguments, "tail", 100), 1, 500);
        string source = (OptionalString(arguments, "source") ?? "memory").Trim().ToLowerInvariant();

        if (source is not ("memory" or "file" or "both"))
        {
            throw new ToolException(
                "E_INVALID_ARG",
                $"未知 source: {source}（可选 memory/file/both）");
        }

        JsonArray memoryEntries = _log.TailAsJson(tail);
        IReadOnlyList<string> fileLines = source is "file" or "both"
            ? _log.ReadFileTail(tail)
            : Array.Empty<string>();

        var fileEntries = new JsonArray();
        LogEntry? lastFileError = null;

        foreach (string line in fileLines)
        {
            LogEntry? parsed = LogRing.Parse(line);

            if (parsed is null)
            {
                fileEntries.Add((JsonNode)new JsonObject
                {
                    ["time"] = null,
                    ["level"] = "RAW",
                    ["message"] = line,
                });
                continue;
            }

            fileEntries.Add((JsonNode)LogRing.ToJson(parsed));

            if (string.Equals(parsed.Level, "ERROR", StringComparison.Ordinal))
            {
                lastFileError = parsed;
            }
        }

        LogEntry? lastError = _log.LastErrorEntry() ?? lastFileError;

        var result = new JsonObject
        {
            ["source"] = source,
            ["entries"] = source == "file"
                ? (JsonNode)fileEntries.DeepClone()
                : (JsonNode)memoryEntries,
            ["file"] = _log.FilePath,
            ["last_error"] = lastError is null ? null : (JsonNode)LogRing.ToJson(lastError),
        };

        if (source is "file" or "both")
        {
            result["file_entries"] = fileEntries;
        }

        return ToolResponse.Text(result);
    }

    private ToolResponse RunScript(JsonElement arguments)
    {
        string code = OptionalString(arguments, "code")
            ?? throw new ToolException("E_INVALID_ARG", "run_script 需要 code");

        int timeoutMs = OptionalInt(arguments, "timeout_ms", 30000);
        return ToolResponse.Text(_session.RunScript(code, timeoutMs));
    }

    private static string? OptionalString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int RequiredCoordinate(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out JsonElement value))
        {
            throw new ToolException("E_INVALID_ARG", $"click 需要 {property}");
        }

        if (!value.TryGetInt32(out int result))
        {
            throw new ToolException(
                "E_INVALID_ARG",
                $"click 的 {property} 必须是整数（收到 {value.ValueKind}）");
        }

        return result;
    }

    private static int OptionalInt(JsonElement element, string property, int fallback) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out JsonElement value) &&
        value.TryGetInt32(out int result)
            ? result
            : fallback;

    private static int RequiredInt(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out JsonElement value) &&
        value.TryGetInt32(out int result)
            ? result
            : throw new ToolException("E_INVALID_ARG", $"缺少整数参数 {property}");

    private static double OptionalDouble(JsonElement element, string property, double fallback) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out JsonElement value) &&
        value.TryGetDouble(out double result)
            ? result
            : fallback;

    private static bool OptionalBool(JsonElement element, string property, bool fallback)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out JsonElement value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    private static int? OptionalNullableInt(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out JsonElement value) &&
        value.TryGetInt32(out int result)
            ? result
            : null;

    private static bool? OptionalNullableBool(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }
}
