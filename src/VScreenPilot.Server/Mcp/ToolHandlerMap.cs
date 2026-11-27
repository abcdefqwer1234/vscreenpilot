using System.Text.Json;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Services;
using VScreenPilot.Tools;

namespace VScreenPilot.Mcp;

/// <summary>30 个工具名 → handler 的唯一装配点；与连接生命周期解耦，便于契约测试。</summary>
internal static class ToolHandlerMap
{
    public static ToolRegistry Build(
        ToolServices? services, LogRing? log, ClientInfo? client,
        IExecutionControl? control = null)
    {
        EnvironmentTools environment = new(services?.Environment!, client);
        SnapshotTools snapshot = new(services?.Snapshot!, services?.Artifacts!);
        ElementTools elements = new(services?.Elements!);
        SystemTools system = new(services?.ScreenText!, services?.Script!);
        InputTools input = new(services?.Input!);
        VisualTools visual = new(services?.Visual!, services?.Wait!);
        var handlers = new Dictionary<string, Func<JsonElement, ToolResponse>>(StringComparer.Ordinal)
        {
            ["get_environment"] = environment.GetEnvironment,
            ["list_windows"] = environment.ListWindows,
            ["get_processes"] = environment.GetProcesses,
            ["set_display"] = environment.SetDisplay,
            ["launch_or_place_app"] = new AppTools(services?.App!).LaunchOrPlace,
            ["snapshot"] = snapshot.Snapshot,
            ["capture_screen"] = snapshot.CaptureScreen,
            ["find_element"] = elements.FindElement,
            ["read_table"] = elements.ReadTable,
            ["get_ui_tree"] = elements.GetUiTree,
            ["invoke_element"] = elements.InvokeElement,
            ["set_element_value"] = elements.SetElementValue,
            ["get_element_text"] = elements.GetElementText,
            ["select_tab"] = elements.SelectTab,
            ["close_tab"] = elements.CloseTab,
            ["read_screen_text"] = system.ReadScreenText,
            ["click"] = input.Click,
            ["click_ref"] = input.ClickRef,
            ["gesture"] = input.Gesture,
            ["click_text"] = input.ClickText,
            ["type_text"] = input.TypeText,
            ["press_keys"] = input.PressKeys,
            ["lease_input"] = input.LeaseInput,
            ["wait_for"] = visual.WaitFor,
            ["assert_region"] = visual.AssertRegion,
            ["match_visual"] = visual.MatchVisual,
            ["release_environment"] = environment.ReleaseEnvironment,
            ["get_log"] = new LogTool(log!, () => client?.Id ?? "c0").GetLog,
            ["run_script"] = system.RunScript,
        };

        var actionTools = new ActionTools(handlers, services?.Input, services?.Elements, control);
        handlers["run_actions"] = actionTools.RunActions;

        ToolRegistry registry = ToolCatalog.Build(handlers);
        actionTools.BindRegistry(registry);
        return registry;
    }
}
