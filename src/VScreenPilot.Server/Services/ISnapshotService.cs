using System.Text.Json.Nodes;
using VScreenPilot.Session;
using VScreenPilot.Views;

namespace VScreenPilot.Services;

/// <summary>快照输出；避免工具层引用 Session 内部类型。</summary>
internal sealed record SnapshotOutput(
    SnapshotView Data, byte[]? Png, byte[]? PreviewPng, JsonObject? PreviewMeta);

/// <summary>屏幕采集输出。</summary>
internal sealed record CaptureOutput(
    byte[] Png, CaptureView Meta, byte[]? PreviewPng, PreviewView? PreviewMeta);

/// <summary>语义快照与屏幕采集。</summary>
internal interface ISnapshotService
{
    SnapshotOutput Snapshot(SnapshotRequest request);
    CaptureOutput Capture(CaptureRequest request);
}

internal sealed class SnapshotToolService : ISnapshotService
{
    private readonly SessionManager _session;

    public SnapshotToolService(SessionManager session) => _session = session;

    public SnapshotOutput Snapshot(SnapshotRequest request)
    {
        SnapshotResult result = _session.SnapshotOps.Snapshot(
            request.Pid,
            request.MaxDepth,
            request.InteractiveOnly,
            request.MaxNodes,
            request.IncludeValues,
            request.IncludeScreenshot,
            request.Scale,
            request.Annotate);
        return new SnapshotOutput(result.Data, result.Png, result.PreviewPng, result.PreviewMeta);
    }

    public CaptureOutput Capture(CaptureRequest request)
    {
        CaptureResult result = _session.CaptureOps.CaptureScreenEx(request);
        return new CaptureOutput(result.Png, result.Meta, result.PreviewPng, result.PreviewMeta);
    }
}
