using VScreenPilot.Views;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Verification;
using ObserveSignature = VScreenPilot.Verification.ObserveSampler.Signature;

namespace VScreenPilot.Session;

/// <summary>
/// 动作后置验证的会话侧接线：真实采样逻辑在 <see cref="ObserveSampler"/>。
/// 保留原有私有方法名，调用点无需改动。
/// </summary>
/// <summary>ObserveOps：从 SessionManager 拆出的操作组，只持有宿主引用。</summary>
internal sealed class SessionObserveOps
{
    internal readonly SessionManager _host;

    public SessionObserveOps(SessionManager host) => _host = host;

    internal ObserveSampler Sampler =>
        _host.ObserveSampler ??= new ObserveSampler(_host.CaptureOps.CaptureRegion);

    internal ObserveSignature CaptureSignature(
        DisplayInfo display, int x, int y, int width, int height) =>
        _host.ObserveOps.Sampler.Capture(display, x, y, width, height);

    internal ObservedView? BuildObserved(
        ObserveSignature? before, ObserveSignature? after, double milliseconds) =>
        ObserveSampler.Build(before, after, milliseconds);
}
