namespace PreferredFixture;

public sealed class Rules
{
    public int A(int v, bool b, bool c, bool d)
    {
        var boundary = v >= 0;
        var first = b && c;
        var second = c && d;
        var third = b && d;
        return (boundary ? 1 : 0) + (first ? 1 : 0) + (second ? 1 : 0) + (third ? 1 : 0);
    }
    public bool B(int v) => v >= 0;
    public bool C(int v) => v >= 0;
    public bool D(int v) => v >= 0;
}
