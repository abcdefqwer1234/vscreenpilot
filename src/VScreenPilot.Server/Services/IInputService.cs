using System.Text.Json;
using VScreenPilot.Session;
using VScreenPilot.Views;

namespace VScreenPilot.Services;

/// <summary>坐标/引用/OCR/键盘输入。</summary>
internal interface IInputService
{
    ClickView Click(ClickRequest request);
    ClickRefView ClickRef(string reference, string? channel, bool keepForeground, bool fallbackInput);
    GestureView Gesture(JsonElement arguments);
    ClickTextView ClickText(ClickTextRequest request);
    TypeTextView TypeText(string text, int? pid, bool verify, int chunkChars, string? channel);
    PressKeysView PressKeys(
        string keys, bool keepForeground, int? pid, bool allowSystemKeys, bool observe);
    PressKeysView PressKeySequence(
        IReadOnlyList<string> keys, bool keepForeground, int? pid, bool allowSystemKeys, bool observe);
}

internal sealed class InputToolService : IInputService
{
    private readonly SessionManager _session;

    public InputToolService(SessionManager session) => _session = session;

    public ClickView Click(ClickRequest request) =>
        _session.InputOps.Click(
            request.X,
            request.Y,
            request.Button,
            request.Clicks,
            request.Mode,
            request.KeepForeground,
            request.Observe,
            request.FallbackInput);

    public ClickRefView ClickRef(
        string reference, string? channel, bool keepForeground, bool fallbackInput) =>
        _session.InputOps.ClickElementRef(reference, channel, keepForeground, fallbackInput);

    public GestureView Gesture(JsonElement arguments) => _session.InputOps.Gesture(arguments);

    public ClickTextView ClickText(ClickTextRequest request) =>
        _session.OcrOps.ClickText(
            request.Text,
            request.KeepForeground,
            request.Observe,
            request.Channel,
            request.RequireUnique,
            request.RegionX,
            request.RegionY,
            request.RegionWidth,
            request.RegionHeight,
            request.FallbackInput);

    public TypeTextView TypeText(
        string text, int? pid, bool verify, int chunkChars, string? channel) =>
        _session.InputOps.TypeText(text, pid, verify, chunkChars, channel);

    public PressKeysView PressKeys(
        string keys, bool keepForeground, int? pid, bool allowSystemKeys, bool observe) =>
        _session.InputOps.PressKeys(keys, keepForeground, pid, allowSystemKeys, observe);

    public PressKeysView PressKeySequence(
        IReadOnlyList<string> keys, bool keepForeground, int? pid, bool allowSystemKeys, bool observe) =>
        _session.InputOps.PressKeySequence(keys, keepForeground, pid, allowSystemKeys, observe);
}
