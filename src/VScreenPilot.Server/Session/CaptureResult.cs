using VScreenPilot.Views;

namespace VScreenPilot.Session;

/// <summary>一次采集的主图与 preview 编码结果，供 capture/snapshot/CLI 共用。</summary>
internal sealed record CaptureResult(
    byte[] Png, CaptureView Meta, byte[]? PreviewPng, PreviewView? PreviewMeta);
