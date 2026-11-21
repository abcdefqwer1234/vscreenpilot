using VScreenPilot.Mcp;
using VScreenPilot.Session;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class ClientPathTests
{
    private static (ClientSessionRegistry Clients, ClientPathPolicy Paths) NewPolicy()
    {
        var clients = new ClientSessionRegistry();
        return (clients, new ClientPathPolicy(clients));
    }

    private static string NewSandboxDirectory()
    {
        // 必须避开系统 temp：ValidatePngPath 允许 temp 作为所有客户端的公共中转根，
        // 用 temp 无法验证“客户端 cwd 之间互相隔离”。
        string path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VScreenPilot",
            "test-cwd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void RelativeExecutablePathUsesClientCwd()
    {
        string cwd = NewSandboxDirectory();

        try
        {
            (ClientSessionRegistry clients, ClientPathPolicy paths) = NewPolicy();

            using IDisposable scope = clients.Use(new ClientContext
            {
                Id = "c-path",
                Cwd = cwd,
                Roots = new[] { cwd },
            });

            Assert.Equal(Path.Combine(cwd, "app.exe"), paths.Resolve("app.exe"));
            Assert.Equal(Path.Combine(cwd, "out.png"), paths.ValidateSavePath("out.png"));
            Assert.Equal(
                Path.Combine(cwd, "out.png"),
                paths.ValidateSavePath(Path.Combine(cwd, "out.png")));
        }
        finally
        {
            Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public void PngOutsideClientCwdIsRejected()
    {
        string cwd = NewSandboxDirectory();

        try
        {
            (ClientSessionRegistry clients, ClientPathPolicy paths) = NewPolicy();

            using IDisposable scope = clients.Use(new ClientContext
            {
                Id = "c-path-2",
                Cwd = cwd,
                Roots = new[] { cwd },
            });

            ToolException exception = Assert.Throws<ToolException>(
                () => paths.ValidateSavePath(@"C:\Windows\vsp-outside.png"));
            Assert.Equal("E_INVALID_ARG", exception.Code);

            var roots = exception.Details!["allowed_roots"]!.AsArray()
                .Select(node => node!.GetValue<string>())
                .ToArray();
            Assert.Contains(roots, root =>
                string.Equals(root, cwd, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(cwd, recursive: true);
        }
    }

    [Fact]
    public void ClientCwdIsIsolatedBetweenScopes()
    {
        string first = NewSandboxDirectory();
        string second = NewSandboxDirectory();

        try
        {
            string firstOutput = Path.Combine(first, "out.png");
            string secondOutput = Path.Combine(second, "out.png");
            (ClientSessionRegistry clients, ClientPathPolicy paths) = NewPolicy();

            using (clients.Use(new ClientContext
                   {
                       Id = "c-a",
                       Cwd = first,
                       Roots = new[] { first },
                   }))
            {
                Assert.Equal(firstOutput, paths.ValidateSavePath(firstOutput));
            }

            using (clients.Use(new ClientContext
                   {
                       Id = "c-b",
                       Cwd = second,
                       Roots = new[] { second },
                   }))
            {
                Assert.Equal(secondOutput, paths.ValidateSavePath(secondOutput));

                // 第二个客户端不能写第一个客户端目录（反之亦然）。
                ToolException exception = Assert.Throws<ToolException>(
                    () => paths.ValidateSavePath(firstOutput));
                Assert.Equal("E_INVALID_ARG", exception.Code);
            }
        }
        finally
        {
            Directory.Delete(first, recursive: true);
            Directory.Delete(second, recursive: true);
        }
    }

    [Fact]
    public void RegistriesAreIndependentPerSession()
    {
        var first = new ClientSessionRegistry();
        var second = new ClientSessionRegistry();

        using (first.Use(new ClientContext { Id = "c1", Cwd = @"C:\first" }))
        {
            Assert.Equal("c1", first.CurrentId);
            Assert.Null(second.Current);
            Assert.Equal("c0", second.CurrentId);
        }

        first.Enter(new ClientContext { Id = "c2" });
        Assert.Equal(1, first.ActiveCount);
        Assert.Equal(0, second.ActiveCount);

        first.Leave("c2");
        Assert.Equal(0, first.ActiveCount);
    }
}
