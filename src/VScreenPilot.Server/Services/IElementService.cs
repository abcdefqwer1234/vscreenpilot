using VScreenPilot.Session;
using VScreenPilot.Views;

namespace VScreenPilot.Services;

/// <summary>UIA 元素查询与操作。</summary>
internal interface IElementService
{
    object Find(ElementQuery query);
    TableView ReadTable(TableQuery query);
    UiTreeView GetUiTree(UiTreeQuery query);
    InvokeElementView Invoke(
        string reference, bool allowClick, string? pattern, string? selectText,
        bool allowDestructive = false);
    SetElementValueView SetValue(
        string reference, string value, bool allowKeyboard, string? mode, bool allowDestructive,
        bool strictVerify);
    ElementTextView GetText(string reference, int maxChars);
    TabActionView SelectTab(string name, string nameMatch, int index, int? pid);
    TabActionView CloseTab(
        string name, string nameMatch, int index, int? pid, bool allowDestructive);
    bool OwnsRef(string reference);
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
            query.Prefer,
            query.Within,
            query.NewSnapshot,
            query.Scope));

    public TableView ReadTable(TableQuery query) => _session.ElementOps.ReadTable(query);

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

    public InvokeElementView Invoke(
        string reference, bool allowClick, string? pattern, string? selectText,
        bool allowDestructive = false) =>
        _session.ElementOps.InvokeElement(reference, allowClick, pattern, selectText, allowDestructive);

    public SetElementValueView SetValue(
        string reference, string value, bool allowKeyboard, string? mode, bool allowDestructive,
        bool strictVerify) =>
        _session.ElementOps.SetElementValue(
            reference, value, allowKeyboard, mode, allowDestructive, strictVerify);

    public ElementTextView GetText(string reference, int maxChars) =>
        _session.ElementOps.GetElementText(reference, maxChars);

    public TabActionView SelectTab(string name, string nameMatch, int index, int? pid) =>
        _session.ElementOps.SelectTab(name, nameMatch, index, pid);

    public TabActionView CloseTab(
        string name, string nameMatch, int index, int? pid, bool allowDestructive) =>
        _session.ElementOps.CloseTab(name, nameMatch, index, pid, allowDestructive);

    public bool OwnsRef(string reference) => _session.ElementOps.OwnsRef(reference);
}
