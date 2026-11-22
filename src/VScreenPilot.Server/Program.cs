using VScreenPilot.Composition;

namespace VScreenPilot;

/// <summary>进程入口：具体编排在 <see cref="Bootstrap"/>，保持入口可读且可测。</summary>
internal static class Program
{
    private static int Main(string[] args) => Bootstrap.Run(args);
}
