using System.Text.Json.Nodes;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Views;

namespace VScreenPilot.Services;

/// <summary>控制管道需要的最小会话操作：客户端作用域、租约、环境与显示状态。</summary>
internal interface IControlService
{
    IDisposable UseClient(ClientInfo client);
    int ActiveClientCount { get; }
    int OwnedAppCount { get; }
    JsonArray ClientSummaries(bool includeCwd = false);
    JsonObject LeaseDescribe();
    void DropClientRefs(string clientId);
    EnvironmentView EnsureEnvironment();
    DisplayInfo? VirtualDisplay { get; }
}
