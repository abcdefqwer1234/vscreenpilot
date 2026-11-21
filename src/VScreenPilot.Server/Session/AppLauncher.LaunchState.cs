using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Input;
using VScreenPilot.Interop;
using VScreenPilot.Mcp;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Ui;
using VScreenPilot.Uia;
using VScreenPilot.Views;
using VScreenPilot.Platform.Processes;
using VScreenPilot.Platform.Windows;
using VScreenPilot.Errors;

namespace VScreenPilot.Session;

internal sealed partial class AppLauncher
{
    private sealed class LaunchState
    {
        public string? Path { get; init; }
        public string? WindowTitle { get; init; }
        public string? Arguments { get; init; }
        public int WaitMs { get; init; }
        public bool NewInstance { get; set; }
        public bool EnsureDocument { get; init; }
        public int? Pid { get; init; }
        public DisplayInfo Display { get; init; } = null!;
        public IntPtr UserForeground { get; init; }
        public WindowInfo? Window { get; set; }
        public bool Started { get; set; }
        public int ContentPid { get; set; }
        public string ContentPath { get; set; } = string.Empty;
        public bool CloseOnRelease { get; set; }
        public bool Dedicated { get; set; }
        public bool HadRunningInstance { get; set; }
        public bool AdoptedExisting { get; set; }
        public string? LaunchWarning { get; set; }
        public string? TempProfile { get; set; }
        public string ExeName { get; set; } = string.Empty;
        public string LaunchPath { get; set; } = string.Empty;
        public TrackedApp? Tracked { get; set; }
        public bool Maximized { get; set; }
        public WindowInfo? After { get; set; }
        public List<string> Warnings { get; } = new();
        public bool? DocumentReady { get; set; }
        public string DocumentReason { get; set; } = string.Empty;
        public IntPtr PreviousForeground { get; set; }
    }
}
