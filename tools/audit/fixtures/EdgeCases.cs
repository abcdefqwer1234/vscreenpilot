namespace VspFixtures.Edge;

/// <summary>特性、注释体、字符串/raw string、集合初始化器、循环等边界。</summary>
internal sealed class EdgeCases
{
    [Obsolete("fixture")]
    public void E1()
    {
    }

    public void E2()
    {
        // 只有注释
    }

    public void E3()
    {
        string text = "a { b } c";
    }

    public void E4()
    {
        string text = @"raw ""quoted"" { }";
    }

    public void E5()
    {
        var map = new Dictionary<string, int>
        {
            ["a"] = 1,
        };
    }

    public void E6()
    {
        for (int i = 0; i < 3; i++)
        {
        }
    }
}
