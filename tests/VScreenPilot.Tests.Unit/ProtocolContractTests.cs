using System.Text;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using VScreenPilot.Mcp;
using VScreenPilot.Perception;
using VScreenPilot.Diagnostics;
using VScreenPilot.Session;
using Xunit;
using VScreenPilot.Errors;

using VScreenPilot.Server.Infrastructure;

using VScreenPilot.Tools;

namespace VScreenPilot.Tests;

public sealed class ProtocolContractTests
{
    [Fact]
    public void ImageContentIsBase64Utf8Bytes()
    {
        const int width = 4;
        const int height = 4;
        var bgra = new byte[width * height * 4];

        for (int index = 0; index < bgra.Length; index += 4)
        {
            bgra[index] = 30;      // B
            bgra[index + 1] = 60;  // G
            bgra[index + 2] = 90;  // R
            bgra[index + 3] = 255;
        }

        byte[] png = PngWriter.Encode(bgra, width, height, 1.0, out _, out _);
        ToolResponse response = ToolResponse.Text(new JsonObject { ["ok"] = true }).AddImage(png);

        CallToolResult result = McpTransport.ToResult(response);
        var image = Assert.IsType<ImageContentBlock>(result.Content[1]);

        // SDK 的 Data 契约是“base64 编码后的 UTF-8 字节”，不是原始 PNG 字节。
        string base64 = Encoding.UTF8.GetString(image.Data.Span);
        byte[] decoded = Convert.FromBase64String(base64);

        Assert.Equal(png, decoded);
        Assert.Equal(new byte[] { 137, 80, 78, 71 }, decoded[..4]);
        Assert.Equal("image/png", image.MimeType);
    }

    [Theory]
    [InlineData("报告 - PowerPoint", "PowerPoint", true)]
    [InlineData("PowerPoint", "PowerPoint", false)]
    [InlineData("PowerPoint - 开始", "PowerPoint", false)]
    [InlineData("工作簿1 - Excel", "Excel", true)]
    [InlineData("Excel", "Excel", false)]
    [InlineData("", "PowerPoint", false)]
    public void DocumentWindowTitleClassification(string title, string product, bool expected)
    {
        Assert.Equal(expected, AppLauncher.LooksLikeDocumentWindow(title, product));
    }

    [Fact]
    public void EmailRedactionKeepsDomainAndCounts()
    {
        int count = 0;
        string redacted = PrivacyRedactor.RedactEmails(
            "联系 alice@example.com 或 bob@corp.cn", ref count);

        Assert.Equal(2, count);
        Assert.DoesNotContain("alice@", redacted);
        Assert.DoesNotContain("bob@", redacted);
        Assert.Contains("***@example.com", redacted);
        Assert.Contains("***@corp.cn", redacted);
    }

    [Fact]
    public void EmailRedactionLeavesPlainTextAlone()
    {
        int count = 0;
        string value = PrivacyRedactor.RedactEmails("没有邮箱 @ 只有符号", ref count);

        Assert.Equal(0, count);
        Assert.Equal("没有邮箱 @ 只有符号", value);
    }

    [Fact]
    public void OverlayDrawsInsideBoundsOnly()
    {
        const int width = 32;
        const int height = 32;
        var bgra = new byte[width * height * 4];

        for (int index = 0; index < bgra.Length; index += 4)
        {
            bgra[index + 3] = 255;
        }

        PngOverlay.DrawBox(bgra, width, height, 4, 4, 10, 10, 255, 0, 0, 2);

        int inside = (4 * width + 4) * 4;
        Assert.Equal(255, bgra[inside + 2]);
        Assert.Equal(0, bgra[inside]);

        // 越界不应抛异常，也不应写到缓冲区外。
        PngOverlay.DrawBox(bgra, width, height, -20, -20, 5, 5, 0, 255, 0, 2);
        PngOverlay.DrawNumber(bgra, width, height, 30, 30, 99);
    }

    [Fact]
    public void ErrorCatalogContainsNewClipboardCode()
    {
        ErrorInfo info = ErrorCatalog.Get("E_CLIPBOARD_UNSAFE");

        Assert.Equal("E_CLIPBOARD_UNSAFE", info.Code);
        Assert.Equal("input", info.Stage);
        Assert.Contains("剪贴板", info.Hint);
    }

    [Fact]
    public void ErrorCatalogContainsDataLossCode()
    {
        ErrorInfo info = ErrorCatalog.Get("E_DATA_LOSS_REQUIRED");

        Assert.Equal("release", info.Stage);
        Assert.Contains("allow_data_loss", info.Hint);
    }

    [Fact]
    public void JsonRedactionWalksNestedTrees()
    {
        var tree = new JsonObject
        {
            ["name"] = "alice@example.com",
            ["children"] = new JsonArray
            {
                new JsonObject { ["value"] = "bob@corp.cn" },
                new JsonObject { ["value"] = "no email here" },
            },
        };

        int count = 0;
        PrivacyRedactor.RedactJsonStrings(tree, ref count);

        Assert.Equal(2, count);
        Assert.DoesNotContain("alice@", tree.ToJsonString());
        Assert.DoesNotContain("bob@", tree.ToJsonString());
        Assert.Contains("***@example.com", tree.ToJsonString());
        Assert.Contains("no email here", tree.ToJsonString());
    }

    [Theory]
    [InlineData("VSP һ FIX һ MARKER", "VSP-FIX-MARKER", true)]
    [InlineData("VSP FIX MARKER", "VSP-FIX-MARKER", true)]
    [InlineData("完全不同的文本", "VSP-FIX-MARKER", false)]
    [InlineData("报告标题", "报告", true)]
    public void OcrMatchingToleratesPunctuation(string haystack, string needle, bool expected)
    {
        Assert.Equal(expected, SessionManager.OcrTextMatches(haystack, needle));
    }
}
