using VScreenPilot.Infrastructure;

namespace VScreenPilot.Cli;

/// <summary>`--help` 文案。内容属于对外契约，只允许标签宽度变化。</summary>
internal static class HelpText
{
    public static void Print()
    {
        Console.Out.WriteLine($"""
            VScreenPilot {BuildInfo.Version}

            用法:
              VScreenPilot [--allow-script] [--parent-pid <pid>] [--profile <name>]
              VScreenPilot --daemon [--profile <name>]
              VScreenPilot --cli env|snapshot|screen|status|stop [--force] [--profile <name>]
              VScreenPilot --doctor                                输出 JSON 诊断
              VScreenPilot --version                               输出版本
              VScreenPilot --help                                  输出本帮助

            选项:
              --allow-script            启用 run_script（默认关闭）
              --parent-pid <pid>        父进程退出时自动清理
              --profile <name>          多会话隔离：独立互斥体、状态文件、追踪与 artifact 目录
              --force                   配合 --cli stop：有活跃客户端时强制停止

            环境变量:
              VSP_DISPLAY_WIDTH        虚拟屏宽度（默认 1280，范围 800–3840）
              VSP_DISPLAY_HEIGHT       虚拟屏高度（默认 720，范围 600–2160）
              VSP_DISPLAY_REFRESH      虚拟屏刷新率（默认 60，范围 30–240）
              VSP_DISPLAY_OFFSET       虚拟屏相对主屏右侧的偏移（默认自动选空闲槽位）
              VSP_INPUT_AUTOFALLBACK=1 UIA/消息点击失败时允许自动降级 SendInput（默认关闭）
              VSP_ALLOW_SCRIPT=1        等价于 --allow-script
              VSP_ALLOW_UNSAFE_SCRIPT=1 绕过 run_script 危险命令黑名单（不是沙箱）
              VSP_APP_ALLOWLIST=dir1;dir2  仅允许启动这些目录下的 exe（默认允许全部）
              VSP_CLIENT_CWD=<dir>      覆盖客户端路径基准（默认 proxy cwd）
              VSP_SCREEN_LEASE_IDLE_MS  屏幕租约空闲毫秒数（默认 60000）
              VSP_DAEMON_IDLE_MS        最后一个客户端断开后的空闲宽限（默认 5000，0=立即退出）
              VSP_CLIENT_LIMIT          共享客户端上限（默认 16）
              VSP_SCREEN_LEASE_WAIT_MS  租约被占用时最多等待毫秒数（默认 0）
              VSP_TOTAL_BUFFER_MB       所有 MCP 连接输入总缓冲上限（默认 64）
              VSP_MAX_MESSAGE_MB        单条 MCP 消息上限（默认 16）

            显示设置在 daemon 启动时读取；修改后需先停止 daemon（--cli stop 或关闭客户端）再启动。
            """);
    }

    /// <summary>
    /// CLI 在抢锁/构造 SessionManager 之前分流：
    /// - 有 ctl：直接查询活跃实例；
    /// - 无 ctl 且互斥体空闲：env 走只读 helper，snapshot/screen 返回 -1 交给本地临时会话；
    /// - 无 ctl 且互斥体被占用：旧实例/异常实例，明确报错。
    /// </summary>
}
