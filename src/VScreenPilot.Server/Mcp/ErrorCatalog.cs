using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace VScreenPilot.Mcp;

internal sealed record ErrorInfo(
    string Code,
    bool Retryable,
    string Stage,
    string Hint,
    string Summary);

/// <summary>
/// 所有对外错误码的唯一事实来源。新增错误码必须同时登记在这里和 docs/errors.md；
/// 单元测试会检查两者一致，避免“错误码散落在业务里、客户端无法依赖”。
/// </summary>
internal static class ErrorCatalog
{
    public static readonly IReadOnlyDictionary<string, ErrorInfo> All =
        new Dictionary<string, ErrorInfo>(StringComparer.Ordinal)
        {
            ["E_INVALID_ARG"] = new("E_INVALID_ARG", false, "validate",
                "检查参数类型/范围；每个工具的错误详情里会给出 field 与 expected", "参数不合法"),
            ["E_OUT_OF_BOUNDS"] = new("E_OUT_OF_BOUNDS", false, "validate",
                "坐标必须位于本会话虚拟屏矩形内；先调用 get_environment 读取 bounds", "坐标越界"),
            ["E_FOREIGN_WINDOW"] = new("E_FOREIGN_WINDOW", false, "validate",
                "目标是本会话未接管的窗口；先用 launch_or_place_app 接管", "外来窗口"),
            ["E_NO_TARGET"] = new("E_NO_TARGET", true, "resolve",
                "没有存活的目标应用；先 launch_or_place_app 或检查目标是否已退出", "无目标应用"),
            ["E_WINDOW_NOT_FOUND"] = new("E_WINDOW_NOT_FOUND", true, "resolve",
                "按标题没有找到窗口；改用 path 启动，或先确认窗口已出现", "窗口不存在"),
            ["E_WINDOW_TIMEOUT"] = new("E_WINDOW_TIMEOUT", true, "launch",
                "应用启动较慢；提高 wait_ms，或检查是否有模态对话框挡住主窗口", "等待窗口超时"),
            ["E_WINDOW_AMBIGUOUS"] = new("E_WINDOW_AMBIGUOUS", false, "resolve",
                "多个窗口匹配；用 pid 参数精确指定（details.windows 给出候选）", "窗口歧义"),
            ["E_ELEMENT_NOT_FOUND"] = new("E_ELEMENT_NOT_FOUND", true, "uia",
                "控件可能尚未出现；用 wait_for(element) 或放宽 name_match=contains", "控件不存在"),
            ["E_ELEMENT_STALE"] = new("E_ELEMENT_STALE", true, "uia",
                "元素已失效；重新 snapshot/find_element 获取新的 ref", "元素已失效"),
            ["E_STALE_SNAPSHOT"] = new("E_STALE_SNAPSHOT", true, "snapshot",
                "snapshot_id 已过期；重新调用 snapshot 并只使用新返回的 ref", "快照已过期"),
            ["E_PATTERN_UNSUPPORTED"] = new("E_PATTERN_UNSUPPORTED", false, "uia",
                "该控件不支持所选 pattern；尝试 pattern=default，或改用 click/keyboard", "控件不支持该模式"),
            ["E_DESTRUCTIVE_REQUIRED"] = new("E_DESTRUCTIVE_REQUIRED", false, "validate",
                "写入会覆盖非空内容；确需覆盖时传 allow_destructive=true", "需要破坏性写入确认"),
            ["E_FOCUS"] = new("E_FOCUS", true, "focus",
                "目标未在前台；先用 click(keep_foreground=true) 激活，或检查是否有模态框", "无法获得前台"),
            ["E_FOCUS_LOST"] = new("E_FOCUS_LOST", true, "focus",
                "输入过程中前台被抢走；重试，或检查用户是否正在操作桌面", "输入中丢失前台"),
            ["E_NO_INTERACTIVE_DESKTOP"] = new("E_NO_INTERACTIVE_DESKTOP", true, "desktop",
                "当前处于锁屏/UAC 安全桌面；等待用户返回普通桌面后重试（服务端会自动探测）", "没有交互桌面"),
            ["E_UIPI"] = new("E_UIPI", false, "input",
                "目标以更高完整性级别运行；以相同权限运行，或改用 UIA 语义操作", "被 UIPI 拦截"),
            ["E_INPUT"] = new("E_INPUT", true, "input",
                "输入未完整注入；检查前台窗口、输入法和目标是否禁用输入", "输入失败"),
            ["E_INPUT_UNSUPPORTED"] = new("E_INPUT_UNSUPPORTED", false, "input",
                "该窗口忽略窗口消息；改用 channel=input 或 CDP/OCR 通道", "输入通道不支持"),
            ["E_SYSTEM_KEY_BLOCKED"] = new("E_SYSTEM_KEY_BLOCKED", false, "input",
                "系统级组合会作用于用户桌面；确需发送时传 allow_system_keys=true", "系统键被拒绝"),
            ["E_APP_GONE"] = new("E_APP_GONE", true, "app",
                "目标进程已退出；重新 launch_or_place_app", "目标进程已退出"),
            ["E_DRIVER_OPEN"] = new("E_DRIVER_OPEN", false, "display",
                "Parsec VDD 设备打不开；运行 --doctor 查看 driver.open/open_error", "驱动设备打开失败"),
            ["E_DRIVER"] = new("E_DRIVER", true, "display",
                "驱动 IOCTL 失败；重新插拔虚拟屏或重启服务端", "驱动调用失败"),
            ["E_DISPLAY"] = new("E_DISPLAY", true, "display",
                "显示拓扑操作失败；检查是否有其他显示管理软件占用", "显示操作失败"),
            ["E_TIMEOUT"] = new("E_TIMEOUT", true, "tool",
                "操作超时；可重试，或先 release_environment 重置会话", "操作超时"),
            ["E_BUSY"] = new("E_BUSY", true, "gate",
                "另一项写操作正在进行；按 retry_after_ms 重试，只读观察不需要重试", "资源忙"),
            ["E_RATE_LIMITED"] = new("E_RATE_LIMITED", true, "rate",
                "请求速率超过限制；按 retry_after_ms 退避后重试", "请求过于频繁"),
            ["E_CANCELLED"] = new("E_CANCELLED", false, "tool",
                "请求已取消；不要自动重试", "请求已取消"),
            ["E_IO"] = new("E_IO", true, "io",
                "文件写入失败；检查路径权限与是否已存在同名文件", "IO 失败"),
            ["E_UNSUPPORTED_IMAGE"] = new("E_UNSUPPORTED_IMAGE", false, "perception",
                "只支持 8-bit 非隔行 RGBA/RGB PNG，且不超过 16M 像素", "图片格式不支持"),
            ["E_NO_BASELINE"] = new("E_NO_BASELINE", false, "verify",
                "基线区域与请求区域不一致；先对同一区域 capture_screen，或改传 baseline_frame_id", "没有可用基线"),
            ["E_DISABLED"] = new("E_DISABLED", false, "script",
                "该能力默认关闭；需要显式 --allow-script 或对应 capability", "能力未启用"),
            ["E_NOT_SUPPORTED"] = new("E_NOT_SUPPORTED", false, "tool",
                "当前平台/应用不支持该操作", "不支持"),
            ["E_CLIPBOARD_UNSAFE"] = new("E_CLIPBOARD_UNSAFE", false, "input",
                "剪贴板含图片/文件，写文本会永久清空；改用 channel=auto/keys，或先备份剪贴板", "剪贴板不安全"),
            ["E_APP_EXITED"] = new("E_APP_EXITED", true, "app",
                "目标进程已退出；重新 launch_or_place_app", "应用已退出"),
            ["E_APP_NOT_ALLOWED"] = new("E_APP_NOT_ALLOWED", false, "launch",
                "路径不在 VSP_APP_ALLOWLIST 允许的目录内；调整白名单或换可执行文件", "应用不在白名单"),
            ["E_APP_START"] = new("E_APP_START", false, "launch",
                "可执行文件不存在或无法启动；检查 path 与权限", "应用启动失败"),
            ["E_DISPLAY_ADD"] = new("E_DISPLAY_ADD", true, "display",
                "VDD 添加显示器失败；检查驱动状态与是否达到显示上限", "添加虚拟屏失败"),
            ["E_DISPLAY_MODE"] = new("E_DISPLAY_MODE", true, "display",
                "设置分辨率/位置失败；换一个分辨率或先摘除其他虚拟屏", "设置显示模式失败"),
            ["E_ELEMENT_DISABLED"] = new("E_ELEMENT_DISABLED", false, "uia",
                "控件当前被禁用；先完成前置步骤或等待其启用", "控件已禁用"),
            ["E_ELEMENT_OFFSCREEN"] = new("E_ELEMENT_OFFSCREEN", true, "uia",
                "控件在可视区外；先滚动到可见位置再操作", "控件不在可视区"),
            ["E_ELEMENT_READONLY"] = new("E_ELEMENT_READONLY", false, "uia",
                "控件只读；改用键盘或应用提供的编辑入口", "控件只读"),
            ["E_MULTIPLE_MATCHES"] = new("E_MULTIPLE_MATCHES", false, "perception",
                "模板命中多个位置；加 region 缩小范围或设 require_unique=false", "模板多重命中"),
            ["E_NEW_INSTANCE_UNSUPPORTED"] = new("E_NEW_INSTANCE_UNSUPPORTED", false, "launch",
                "该应用不支持强制新实例；改用 pid 接管或接受复用窗口", "不支持新实例"),
            ["E_NO_MATCH"] = new("E_NO_MATCH", true, "perception",
                "模板/颜色没有命中；放宽阈值或换 region", "未找到匹配"),
            ["E_ORPHAN_DISPLAY"] = new("E_ORPHAN_DISPLAY", true, "display",
                "存在未归属的虚拟屏且无历史记录；先人工确认并摘除，或用 --profile 隔离", "孤儿虚拟屏"),
            ["E_RELEASED"] = new("E_RELEASED", false, "session",
                "会话已释放；重新启动服务端或新建 profile", "会话已释放"),
            ["E_SCRIPT"] = new("E_SCRIPT", false, "script",
                "脚本执行失败；检查 PowerShell 语法与退出码", "脚本执行失败"),
            ["E_TARGET_OFFSCREEN"] = new("E_TARGET_OFFSCREEN", true, "window",
                "目标窗口不在虚拟屏内；先 launch_or_place_app 归位", "目标窗口不在虚拟屏"),
            ["E_UIA"] = new("E_UIA", true, "uia",
                "UIA provider 调用失败；重试或改用 channel=input", "UIA 调用失败"),
            ["E_INTERNAL"] = new("E_INTERNAL", false, "internal",
                "内部错误；用 get_log 读取最近错误，并附上 stage 反馈", "内部错误"),
        };

    public static ErrorInfo Get(string code) =>
        All.TryGetValue(code, out ErrorInfo? info)
            ? info
            : new ErrorInfo(code, false, "unknown", "未知错误码；用 get_log 读取详情", code);

    /// <summary>
    /// 把未包装异常映射成可操作错误：AggregateException 取内层，COM/Win32 映射到稳定错误码。
    /// 业务代码应尽量直接抛 ToolException；这里是最后一道兜底。
    /// </summary>
    public static ToolException Normalize(Exception exception, string stage = "internal")
    {
        if (exception is ToolException known)
        {
            return known;
        }

        if (exception is AggregateException aggregate &&
            aggregate.InnerExceptions.Count == 1)
        {
            return Normalize(aggregate.InnerExceptions[0], stage);
        }

        if (exception is COMException com)
        {
            string code = com.HResult switch
            {
                unchecked((int)0x80070003) or unchecked((int)0x80004005) => "E_APP_GONE",
                unchecked((int)0x80070005) => "E_UIPI",
                unchecked((int)0x80131505) => "E_TIMEOUT",
                _ => "E_INTERNAL",
            };

            var info = Get(code);
            return new ToolException(
                code,
                com.Message,
                new JsonObject { ["hresult"] = $"0x{com.HResult:X8}" },
                info.Retryable,
                info.Hint,
                stage);
        }

        if (exception is OperationCanceledException)
        {
            var info = Get("E_CANCELLED");
            return new ToolException("E_CANCELLED", "请求已取消", retryable: false,
                hint: info.Hint, stage: stage);
        }

        if (exception is UnauthorizedAccessException)
        {
            var info = Get("E_IO");
            return new ToolException("E_IO", exception.Message, retryable: false,
                hint: info.Hint, stage: stage);
        }

        var fallback = Get("E_INTERNAL");
        return new ToolException(
            "E_INTERNAL",
            exception.Message,
            new JsonObject { ["exception"] = exception.GetType().Name },
            fallback.Retryable,
            fallback.Hint,
            stage);
    }
}
