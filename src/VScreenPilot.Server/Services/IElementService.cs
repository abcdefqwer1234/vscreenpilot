using VScreenPilot.Session;
using VScreenPilot.Views;

namespace VScreenPilot.Services;

/// <summary>UIA 元素查询与操作。</summary>
internal interface IElementService
{
    object Find(ElementQuery query);
    UiTreeView GetUiTree(UiTreeQuery query);
    InvokeElementView Invoke(string reference, bool allowClick, string? pattern);
    SetElementValueView SetValue(
        string reference, string value, bool allowKeyboard, string? mode, bool allowDestructive);
    ElementTextView GetText(string reference, int maxChars);
}

internal sealed class ElementToolService : IElementService
{
    private readonly SessionManager _session;

    public ElementToolService(SessionManager session) => _session = session;

    public object Find(ElementQuery query) =>
        _session.VerifyOps.FindElement(new FindQuery(
            query.Name,
            query.ControlType,
            query.AutomationId,
            query.NameMatch,
            query.Index,
            query.All,
            query.MaxResults,
            query.IncludeOffscreen,
            query.Pid,
            query.Role,
            query.Prefer));

    public UiTreeView GetUiTree(UiTreeQuery query) =>
        _session.ElementOps.GetUiTree(
            new UiTreeOptions(
                query.MaxDepth,
                query.InteractiveOnly,
                query.MaxNodes,
                query.Root,
                query.Pid),
            query.Format,
            query.NewSnapshot);

    public InvokeElementView Invoke(string reference, bool allowClick, string? pattern) =>
        _session.ElementOps.InvokeElement(reference, allowClick, pattern);

    public SetElementValueView SetValue(
        string reference, string value, bool allowKeyboard, string? mode, bool allowDestructive) =>
        _session.ElementOps.SetElementValue(reference, value, allowKeyboard, mode, allowDestructive);

    public ElementTextView GetText(string reference, int maxChars) =>
        _session.ElementOps.GetElementText(reference, maxChars);
}
