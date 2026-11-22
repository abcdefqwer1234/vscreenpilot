namespace VspFixtures.Basic;

/// <summary>基础方法长度、多行签名、泛型、out、注释与字符串花括号。</summary>
internal sealed class BasicCases
{
    public void Alpha()
    {
    }

    public void Beta()
    {
        int a = 1;
        int b = 2;
    }

    public void Gamma(
        int a,
        int b)
    {
        int c = a + b;
    }

    public T Delta<T>(T value)
    {
        return value;
    }

    public bool Epsilon(out int value)
    {
        value = 0;
        return true;
    }

    public string Zeta()
    {
        return "}{";
    }

    public void Eta()
    {
        // } { 注释里的花括号不应影响解析
    }

    public void Theta()
    {
        int x = 1;
    }

    public int Iota() => 1;

    private sealed class Nested
    {
        public void Inner()
        {
        }
    }
}

internal sealed record Kappa(int A, int B);
