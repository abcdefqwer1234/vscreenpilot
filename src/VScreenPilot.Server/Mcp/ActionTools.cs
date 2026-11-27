using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Errors;
using VScreenPilot.Services;
using VScreenPilot.Tools;
using static VScreenPilot.Json.JsonArgReader;

namespace VScreenPilot.Mcp;

/// <summary>
/// `run_actions`：在单个工具调用内顺序执行白名单动作。
/// 不经过 MCP 管线，因此只复用 handler，不重复获取屏幕租约；整批共享一个前台租约。
/// </summary>
internal sealed class ActionTools
{
    private static readonly HashSet<string> AllowedActions = new(StringComparer.Ordinal)
    {
        "get_environment", "list_windows", "get_processes",
        "snapshot", "capture_screen", "find_element", "get_ui_tree", "get_element_text",
        "read_table", "invoke_element", "set_element_value", "read_screen_text",
        "click", "click_ref", "click_text", "gesture",
        "type_text", "press_keys",
        "wait_for", "assert_region", "match_visual",
        "select_tab", "close_tab",
    };

    private static readonly HashSet<string> InputActions = new(StringComparer.Ordinal)
    {
        "click", "click_ref", "click_text", "gesture",
        "type_text", "press_keys",
        "invoke_element", "set_element_value",
        "select_tab", "close_tab",
    };

    private readonly IReadOnlyDictionary<string, Func<JsonElement, ToolResponse>> _handlers;
    private readonly IInputService? _input;
    private readonly IElementService? _elements;
    private readonly IExecutionControl? _control;
    private ToolRegistry? _registry;

    public ActionTools(
        IReadOnlyDictionary<string, Func<JsonElement, ToolResponse>> handlers,
        IInputService? input,
        IElementService? elements = null,
        IExecutionControl? control = null)
    {
        _handlers = handlers;
        _input = input;
        _elements = elements;
        _control = control;
    }

    /// <summary>由组合根在 ToolCatalog.Build 之后注入，用于 dry_run 的参数校验。</summary>
    public void BindRegistry(ToolRegistry registry) => _registry = registry;

    internal static bool IsAllowed(string action) => AllowedActions.Contains(action);

    public ToolResponse RunActions(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("steps", out JsonElement stepsElement) ||
            stepsElement.ValueKind != JsonValueKind.Array)
        {
            throw new ToolException("E_INVALID_ARG", "run_actions 需要 steps 数组");
        }

        var steps = stepsElement.EnumerateArray().ToList();

        if (steps.Count == 0)
        {
            throw new ToolException("E_INVALID_ARG", "steps 不能为空");
        }

        if (steps.Count > 50)
        {
            throw new ToolException(
                "E_INVALID_ARG",
                "steps 最多 50 步",
                new JsonObject { ["count"] = steps.Count },
                retryable: false,
                hint: "拆成多次 run_actions，或减少步骤",
                stage: "validate");
        }

        bool stopOnError = OptionalBool(arguments, "stop_on_error", true);
        int leaseMs = Math.Clamp(OptionalInt(arguments, "lease_ms", 30_000), 1000, 600_000);
        bool dryRun = OptionalBool(arguments, "dry_run", false);
        var plan = new JsonArray();
        string? validationError = null;

        foreach (JsonElement step in steps)
        {
            string? action = step.TryGetProperty("action", out JsonElement actionElement) &&
                             actionElement.ValueKind == JsonValueKind.String
                ? actionElement.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(action) || !IsAllowed(action))
            {
                validationError = action is null
                    ? "每个 step 都需要 action"
                    : $"step action 不在白名单内: {action}";
                break;
            }

            var args = step.TryGetProperty("args", out JsonElement argsElement) &&
                       argsElement.ValueKind == JsonValueKind.Object
                ? JsonNode.Parse(argsElement.GetRawText())!.AsObject()
                : new JsonObject();
            bool continueOnError = step.TryGetProperty("continue_on_error", out JsonElement cont) &&
                                   cont.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                                   cont.GetBoolean();
            int stepTimeoutMs = OptionalInt(step, "timeout_ms", 15_000);

            try
            {
                ValidateStep(plan.Count, action, args);
            }
            catch (ToolException exception)
            {
                validationError = exception.Message;
                break;
            }

            plan.Add((JsonNode)new JsonObject
            {
                ["action"] = action,
                ["args"] = args.DeepClone(),
                ["continue_on_error"] = continueOnError,
                ["timeout_ms"] = stepTimeoutMs,
            });
        }

        if (validationError is not null)
        {
            throw new ToolException(
                "E_INVALID_ARG",
                validationError,
                new JsonObject { ["allowed"] = ToJsonArray(AllowedActions) },
                retryable: false,
                hint: "run_actions 只允许白名单动作；不要用它执行 run_script/launch/release/set_display",
                stage: "validate");
        }

        if (dryRun)
        {
            return ToolResponse.Text(new JsonObject
            {
                ["ok"] = true,
                ["dry_run"] = true,
                ["completed"] = 0,
                ["steps"] = plan,
            });
        }

        bool needsLease = steps.Any(step =>
            step.TryGetProperty("action", out JsonElement action) &&
            action.ValueKind == JsonValueKind.String &&
            InputActions.Contains(action.GetString() ?? string.Empty));

        var results = new JsonArray();
        var forwardedContent = new JsonArray();
        int completed = 0;
        int? abortedAt = null;
        bool leaseAcquired = false;
        bool leaseManaged = false;
        var watch = Stopwatch.StartNew();

        try
        {
            if (needsLease && _input is not null)
            {
                var current = _input.LeaseInput("status", null, 0, exclusive: false, force: false);

                if (current.Active)
                {
                    // 已有租约（keep_foreground/lease_input）时只续期，不在批次结束时误释放。
                    _input.LeaseInput("renew", null, leaseMs, exclusive: false, force: false);
                    leaseManaged = true;
                }
                else
                {
                    _input.LeaseInput("acquire", null, leaseMs, exclusive: false, force: false);
                    leaseAcquired = true;
                    leaseManaged = true;
                }
            }

            for (int index = 0; index < steps.Count; index++)
            {
                JsonElement step = steps[index];
                string action = step.GetProperty("action").GetString()!;
                var args = step.TryGetProperty("args", out JsonElement argsElement) &&
                           argsElement.ValueKind == JsonValueKind.Object
                    ? JsonNode.Parse(argsElement.GetRawText())!.AsObject()
                    : new JsonObject();
                bool continueOnError = step.TryGetProperty("continue_on_error", out JsonElement cont) &&
                                       cont.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                                       cont.GetBoolean();
                int stepTimeoutMs = OptionalInt(step, "timeout_ms", 15_000);

                var stepWatch = Stopwatch.StartNew();
                ToolResponse response;
                bool stepTimedOut = false;
                CancellationTokenSource? previousCts = _control?.CurrentCancellationSource;
                CancellationTokenSource? stepCts = null;

                if (leaseManaged && InputActions.Contains(action))
                {
                    _input?.LeaseInput("renew", null, leaseMs, exclusive: false, force: false);
                }

                if (_control is not null && stepTimeoutMs > 0)
                {
                    stepCts = CancellationTokenSource.CreateLinkedTokenSource(
                        previousCts?.Token ?? CancellationToken.None);
                    stepCts.CancelAfter(stepTimeoutMs);
                    _control.SetCancellation(stepCts);
                }

                try
                {
                    ValidateStep(index, action, args);
                    ValidateRefs(index, action, args);
                    JsonElement element = JsonSerializer.SerializeToElement(args);
                    response = _handlers[action](element);
                }
                catch (ToolException exception)
                {
                    response = ToolResponse.FromException(exception);
                }
                catch (Exception exception)
                {
                    response = ToolResponse.FromException(exception, "batch");
                }
                finally
                {
                    stepTimedOut = stepCts is { IsCancellationRequested: true } &&
                                   previousCts?.IsCancellationRequested != true;

                    if (_control is not null)
                    {
                        _control.SetCancellation(previousCts);
                    }

                    stepCts?.Dispose();
                }

                if (stepTimedOut)
                {
                    response = ToolResponse.Error(
                        "E_TIMEOUT",
                        $"run_actions 第 {index + 1} 步 {action} 超过 {stepTimeoutMs}ms",
                        new JsonObject
                        {
                            ["index"] = index,
                            ["action"] = action,
                            ["timeout_ms"] = stepTimeoutMs,
                        },
                        retryable: true,
                        hint: "缩小单步范围或提高 timeout_ms；卡住的输入可能已部分生效，重试前先 snapshot 核对",
                        stage: "tool");
                }

                // 批处理只回传结构化结果，但嵌套的快照/截图可能把小图放在 image 块里。
                // 转发非 text 内容块，避免 batch 内的截图静默丢失；同一 step 的多块只加一个步骤标记。
                bool stepContentStarted = false;

                foreach (JsonNode? block in response.Content)
                {
                    if (block is JsonObject item &&
                        item["type"]?.GetValue<string>() is "image" or "resource_link")
                    {
                        if (!stepContentStarted)
                        {
                            forwardedContent.Add(new JsonObject
                            {
                                ["type"] = "text",
                                ["text"] = $"[step {index}] {action}",
                            });
                            stepContentStarted = true;
                        }

                        forwardedContent.Add(block.DeepClone());
                    }
                }

                bool stepOk = !response.IsError;
                var result = new JsonObject
                {
                    ["index"] = index,
                    ["action"] = action,
                    ["ok"] = stepOk,
                    ["elapsed_ms"] = Math.Round(stepWatch.Elapsed.TotalMilliseconds, 1),
                    ["timeout_ms"] = stepTimeoutMs,
                };

                if (response.StructuredContent is not null)
                {
                    result[stepOk ? "result" : "error"] = response.StructuredContent.DeepClone();
                }

                results.Add(result);

                if (!stepOk)
                {
                    abortedAt = index;

                    if (stopOnError && !continueOnError)
                    {
                        break;
                    }
                }

                completed++;
            }
        }
        finally
        {
            if (leaseAcquired && _input is not null)
            {
                _input.LeaseInput("release", null, 0, exclusive: false, force: false);
            }
        }

        bool allOk = results.All(item => item!["ok"]!.GetValue<bool>());
        ToolResponse batch = ToolResponse.Text(new JsonObject
        {
            ["ok"] = allOk,
            ["completed"] = completed,
            ["aborted_at"] = abortedAt,
            ["elapsed_ms"] = Math.Round(watch.Elapsed.TotalMilliseconds, 1),
            ["steps"] = results,
        });

        foreach (JsonNode? block in forwardedContent)
        {
            batch.Content.Add(block?.DeepClone());
        }

        // MCP 客户端通常先看 isError；批处理有失败步骤时不能只靠 ok 字段。
        batch.IsError = !allOk;
        return batch;
    }

    private void ValidateStep(int index, string action, JsonObject args)
    {
        if (_registry is null || !_registry.TryGet(action, out ToolDescriptor? descriptor))
        {
            return;
        }

        JsonElement element = JsonSerializer.SerializeToElement(args);
        ArgumentValidator.Validate(descriptor.InputSchema, element, $"steps[{index}].args");
    }

    private void ValidateRefs(int index, string action, JsonObject args)
    {
        if (_elements is null)
        {
            return;
        }

        foreach (string key in new[] { "ref", "root_ref", "within" })
        {
            if (args[key] is not JsonValue value ||
                !value.TryGetValue(out string? reference) ||
                string.IsNullOrWhiteSpace(reference) ||
                _elements.OwnsRef(reference))
            {
                continue;
            }

            throw new ToolException(
                "E_ELEMENT_STALE",
                $"step {index} ({action}) 的 {key}={reference} 不属于当前客户端或已过期",
                new JsonObject
                {
                    ["index"] = index,
                    ["action"] = action,
                    ["field"] = key,
                    ["ref"] = reference,
                },
                retryable: true,
                hint: "重新 snapshot/find_element 获取当前客户端的 ref，或把创建 ref 的步骤放在前面",
                stage: "validate");
        }
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();

        foreach (string value in values.OrderBy(item => item, StringComparer.Ordinal))
        {
            array.Add(value);
        }

        return array;
    }
}
