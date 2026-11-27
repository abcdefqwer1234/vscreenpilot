using System.Text.Json;
using VScreenPilot.Session;
using VScreenPilot.Views;

namespace VScreenPilot.Services;

/// <summary>坐标/引用/OCR/键盘输入。</summary>
internal interface IInputService
{
    ClickView Click(ClickRequest request);
    ClickRefView ClickRef(
        string reference, string? channel, bool keepForeground, bool fallbackInput,
        bool allowDestructive = false);
    GestureView Gesture(JsonElement arguments);
    ClickTextView ClickText(ClickTextRequest request);
    TypeTextView TypeText(
        string text, int? pid, bool verify, int chunkChars, string? channel,
        long? hwnd, bool strictVerify, int paceMs, string? target = null);
    PressKeysView PressKeys(
        string keys, bool keepForeground, int? pid, bool allowSystemKeys, bool observe, long? hwnd,
        string? target = null);
    PressKeysView PressKeySequence(
        IReadOnlyList<string> keys, bool keepForeground, int? pid, bool allowSystemKeys, bool observe,
        long? hwnd, string? target = null);
    InputLeaseView LeaseInput(
        string action, int? pid, int ttlMs, bool exclusive, bool force);
}

internal sealed class InputToolService : IInputService
{
    private readonly SessionManager _session;

    public InputToolService(SessionManager session) => _session = session;

    public ClickView Click(ClickRequest request) =>
        _session.InputOps.Click(new SessionClickRequest(
            request.X,
            request.Y,
            request.Button,
            request.Clicks,
            request.Mode,
            request.KeepForeground,
            request.Observe,
            request.FallbackInput,
            request.ContextMenu,
            request.IntervalMs,
            request.AllowDestructive,
            request.SettleMs,
            request.AbortOnWindowChange));

    public ClickRefView ClickRef(
        string reference, string? channel, bool keepForeground, bool fallbackInput,
        bool allowDestructive = false) =>
        _session.InputOps.ClickElementRef(reference, channel, keepForeground, fallbackInput, allowDestructive);

    public GestureView Gesture(JsonElement arguments) => _session.InputOps.Gesture(arguments);

    public ClickTextView ClickText(ClickTextRequest request) =>
        _session.OcrOps.ClickText(new TextClickRequest(
            request.Text,
            new OcrReadRequest(
                request.RegionX,
                request.RegionY,
                request.RegionWidth,
                request.RegionHeight,
                null,
                0,
                request.Upscale,
                request.OnOutOfBoundsClip),
            new TextClickOptions(
                request.Channel,
                request.KeepForeground,
                request.Observe,
                request.RequireUnique,
                request.FallbackInput,
                request.AllowDestructive)));

    public TypeTextView TypeText(
        string text, int? pid, bool verify, int chunkChars, string? channel,
        long? hwnd, bool strictVerify, int paceMs, string? target = null) =>
        _session.InputOps.TypeText(
            text, pid, verify, chunkChars, channel, hwnd, strictVerify, paceMs, target);

    public PressKeysView PressKeys(
        string keys, bool keepForeground, int? pid, bool allowSystemKeys, bool observe, long? hwnd,
        string? target = null) =>
        _session.InputOps.PressKeys(
            keys, keepForeground, pid, allowSystemKeys, observe, hwnd, target);

    public PressKeysView PressKeySequence(
        IReadOnlyList<string> keys, bool keepForeground, int? pid, bool allowSystemKeys, bool observe,
        long? hwnd, string? target = null) =>
        _session.InputOps.PressKeySequence(
            keys, keepForeground, pid, allowSystemKeys, observe, hwnd, target);

    public InputLeaseView LeaseInput(
        string action, int? pid, int ttlMs, bool exclusive, bool force) =>
        _session.InputOps.LeaseInput(action, pid, ttlMs, exclusive, force);
}
