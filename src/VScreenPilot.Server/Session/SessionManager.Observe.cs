using VScreenPilot.Views;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Verification;
using ObserveSignature = VScreenPilot.Verification.ObserveSampler.Signature;

namespace VScreenPilot.Session;

/// <summary>
/// 动作后置验证的会话侧接线：真实采样逻辑在 <see cref="ObserveSampler"/>。
/// 保留原有私有方法名，调用点无需改动。
/// </summary>
internal sealed partial class SessionManager
{
    private ObserveSampler? _observeSampler;

    private ObserveSampler Sampler =>
        _observeSampler ??= new ObserveSampler(CaptureRegion);

    private ObserveSignature CaptureSignature(
        DisplayInfo display, int x, int y, int width, int height) =>
        Sampler.Capture(display, x, y, width, height);

    private static ObservedView? BuildObserved(
        ObserveSignature? before, ObserveSignature? after, double milliseconds) =>
        ObserveSampler.Build(before, after, milliseconds);
}
