using VScreenPilot.Session;

namespace VScreenPilot.Services;

/// <summary>10 个应用服务的只读集合，handler 组按需注入。</summary>
internal sealed class ToolServices
{
    public IEnvironmentService Environment { get; init; } = null!;
    public IAppService App { get; init; } = null!;
    public ISnapshotService Snapshot { get; init; } = null!;
    public IArtifactService Artifacts { get; init; } = null!;
    public IElementService Elements { get; init; } = null!;
    public IInputService Input { get; init; } = null!;
    public IVisualService Visual { get; init; } = null!;
    public IWaitService Wait { get; init; } = null!;
    public IScriptService Script { get; init; } = null!;
    public IScreenTextService ScreenText { get; init; } = null!;

    /// <summary>组合根调用；每个会话共享一组适配器。</summary>
    public static ToolServices Create(SessionManager session) => new()
    {
        Environment = new EnvironmentToolService(session),
        App = new AppToolService(session),
        Snapshot = new SnapshotToolService(session),
        Artifacts = new ArtifactToolService(session),
        Elements = new ElementToolService(session),
        Input = new InputToolService(session),
        Visual = new VisualToolService(session),
        Wait = new WaitToolService(session),
        Script = new ScriptToolService(session),
        ScreenText = new ScreenTextToolService(session),
    };
}
