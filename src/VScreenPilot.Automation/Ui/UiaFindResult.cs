namespace VScreenPilot.Uia;

/// <summary>批量查找结果：同时携带“是否被截断”，避免用线程静态字段传递。</summary>
internal sealed record UiaFindResult(List<UiaElementInfo> Items, bool Truncated);
