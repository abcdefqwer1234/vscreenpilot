using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Input;
using VScreenPilot.Interop;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Ui;
using VScreenPilot.Uia;
using VScreenPilot.Views;
using VScreenPilot.Platform.Processes;
using VScreenPilot.Platform.Windows;
using VScreenPilot.Errors;

namespace VScreenPilot.Session;

internal sealed class LaunchState
{
    public string? Path { get; init; }
    public string? WindowTitle { get; init; }
    public string? Arguments { get; init; }
    public int WaitMs { get; init; }
    public bool NewInstance { get; set; }
    public bool EnsureDocument { get; init; }
    public bool Place { get; init; } = true;
    public bool Maximize { get; set; } = true;
    public string OnNoWindow { get; set; } = "leave";
    public bool Detached { get; init; }
    public string Fit { get; init; } = "clamp";
    public int? Pid { get; set; }
    public long? TargetHwnd { get; init; }
    public DisplayInfo Display { get; set; } = null!;
    public IntPtr UserForeground { get; init; }
    public WindowInfo? Window { get; set; }
    public bool Started { get; set; }
    public int LaunchedPid { get; set; }
    public bool Alive { get; set; }
    public int? ExitCode { get; set; }
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
    public bool FitAdjusted { get; set; }
    public OptionalBoundsView RequestedBounds { get; set; } =
        new(null, null, null, null);
    public OptionalBoundsView AppliedBounds { get; set; } =
        new(null, null, null, null);
    public WindowInfo? After { get; set; }
    public List<string> Warnings { get; } = new();
    public bool? DocumentReady { get; set; }
    public string DocumentReason { get; set; } = string.Empty;
    public IntPtr PreviousForeground { get; set; }
}
