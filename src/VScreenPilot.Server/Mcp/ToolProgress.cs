namespace VScreenPilot.Mcp;

/// <summary>
/// 把当前工具调用的 progress 回调沿 AsyncLocal 传给 SessionManager 的长循环。
/// 客户端没有提供 progressToken 时，Report 是 no-op。
/// </summary>
internal static class ToolProgress
{
    private static readonly AsyncLocal<Action<int, int, string>?> Current = new();

    public static IDisposable Begin(Action<int, int, string>? reporter)
    {
        Action<int, int, string>? previous = Current.Value;
        Current.Value = reporter;
        return new Scope(previous);
    }

    public static void Report(int current, int total, string message)
    {
        Action<int, int, string>? reporter = Current.Value;

        if (reporter is null)
        {
            return;
        }

        try
        {
            reporter(current, total, message);
        }
        catch
        {
            // progress 失败不能影响工具本身。
        }
    }

    private sealed class Scope : IDisposable
    {
        private readonly Action<int, int, string>? _previous;

        public Scope(Action<int, int, string>? previous)
        {
            _previous = previous;
        }

        public void Dispose() => Current.Value = _previous;
    }
}
