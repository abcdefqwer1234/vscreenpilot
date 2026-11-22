namespace VspFixtures.Partial;

/// <summary>partial 类型的方法应在两个声明中都被解析。</summary>
internal sealed partial class PartialCases
{
    public void P1()
    {
    }

    public void P2()
    {
        int x = 1;
    }

    public void P3()
    {
        int x = 1;
        int y = 2;
    }
}

internal sealed partial class PartialCases
{
    public int P4()
    {
        return 4;
    }

    public int P5(
        int a)
    {
        return a;
    }

    public int P6()
    {
        int a = 1;
        int b = 2;
        return a + b;
    }
}
