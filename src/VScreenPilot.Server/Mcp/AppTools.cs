using System.Text.Json;
using VScreenPilot.Services;
using VScreenPilot.Tools;
using static VScreenPilot.Json.JsonArgReader;

namespace VScreenPilot.Mcp;

/// <summary>App 组：launch_or_place_app。</summary>
internal sealed class AppTools
{
    private readonly IAppService _app;

    public AppTools(IAppService app) => _app = app;

    public ToolResponse LaunchOrPlace(JsonElement arguments) =>
        ToolResponseWriter.FromView(_app.LaunchOrPlace(new AppLaunchRequest
        {
            Path = OptionalString(arguments, "path"),
            Window = OptionalString(arguments, "window"),
            Args = OptionalString(arguments, "args"),
            WaitMs = OptionalInt(arguments, "wait_ms", 15000),
            NewInstance = OptionalBool(arguments, "new_instance", true),
            EnsureDocument = OptionalBool(arguments, "ensure_document", true),
            Place = OptionalBool(arguments, "place", true),
            Maximize = OptionalBool(arguments, "maximize", true),
            OnNoWindow = OptionalString(arguments, "on_no_window") ?? "leave",
            Pid = OptionalNullableInt(arguments, "pid"),
            Hwnd = OptionalNullableLong(arguments, "hwnd"),
        }));
}
