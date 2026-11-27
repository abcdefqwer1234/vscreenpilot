using System.Text.Json;
using VScreenPilot.Mcp;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Services;
using VScreenPilot.Tools;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class ActionBatchTimeoutTests
{
    [Fact]
    public void StepTimeoutReturnsTimeoutError()
    {
        var control = new FakeControl();
        var handlers = new Dictionary<string, Func<JsonElement, ToolResponse>>(StringComparer.Ordinal)
        {
            ["wait_for"] = _ =>
            {
                Thread.Sleep(200);
                control.CurrentCancellationSource?.Token.ThrowIfCancellationRequested();
                return ToolResponse.Text(new System.Text.Json.Nodes.JsonObject());
            },
        };
        var tools = new ActionTools(handlers, null, null, control);
        using JsonDocument document = JsonDocument.Parse(
            """{"stop_on_error":true,"steps":[{"action":"wait_for","timeout_ms":20,"args":{}}]}""");

        ToolResponse response = tools.RunActions(document.RootElement);

        System.Text.Json.Nodes.JsonNode? step = response.StructuredContent!["steps"]![0];
        Assert.False(step!["ok"]!.GetValue<bool>());
        Assert.Equal("E_TIMEOUT", step["error"]!["code"]!.GetValue<string>());
    }

    private sealed class FakeControl : IExecutionControl
    {
        private CancellationTokenSource? _cts;

        public long Epoch => 1;
        public ScreenLease Lease { get; } = new();
        public ISessionGate Gate => null!;
        public bool HasLiveOwnedApps() => false;
        public IDisposable? UseClient(ClientInfo? client) => null;
        public void SetCancellation(CancellationTokenSource? cts) => _cts = cts;
        public CancellationTokenSource? CurrentCancellationSource => _cts;
        public void PauseFocusGuard(int leaseMs = 0) { }
        public void ExtendLease(string clientId, int durationMs) { }
    }
}
