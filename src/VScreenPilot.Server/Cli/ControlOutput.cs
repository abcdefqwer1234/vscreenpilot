using System.Text.Json;
using System.Text.Json.Nodes;

namespace VScreenPilot.Cli;

/// <summary>CLI/ctl 的 JSON 输出口径（缩进、ok=true 合并规则）。</summary>
internal static class ControlOutput
{
    public static int PrintControlResponse(JsonObject response)
    {
        if (response["ok"]?.GetValue<bool>() == true && response["result"] is JsonNode result)
        {
            // 与无实例时的本地 CLI 输出保持同一形状：所有成功结果都带 ok=true。
            JsonObject payload = result.DeepClone() as JsonObject ?? new JsonObject
            {
                ["result"] = result.DeepClone(),
            };

            payload["ok"] = true;
            PrintJson(payload);
            return 0;
        }

        PrintJson(response);
        return 1;
    }

    public static void PrintJson(JsonNode node)
    {
        Console.Out.WriteLine(node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
