namespace VScreenPilot.Cli;

/// <summary>极简命令行解析：只支持 <c>--flag</c> 与 <c>--opt=value</c> / <c>--opt value</c>。</summary>
internal static class CommandLineOptions
{
    public static string? GetOption(string[] args, string option)
    {
        for (int index = 0; index < args.Length; index++)
        {
            if (args[index].StartsWith(option + "=", StringComparison.OrdinalIgnoreCase))
            {
                return args[index][(option.Length + 1)..];
            }

            if (string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase) &&
                index + 1 < args.Length)
            {
                return args[index + 1];
            }
        }

        return null;
    }

    public static bool HasFlag(string[] args, string flag) =>
        args.Any(arg => string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase));
}
