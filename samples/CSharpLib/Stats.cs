namespace CSharpLib;

/// <summary>A plain C# library consumed from Alice (see ../AliceApp).</summary>
public static class Stats
{
    public static double Median(double[] xs)
    {
        var s = (double[])xs.Clone();
        Array.Sort(s);
        return s.Length % 2 == 1 ? s[s.Length / 2] : (s[s.Length / 2 - 1] + s[s.Length / 2]) / 2;
    }
}

public class Counter
{
    public int Count { get; private set; }
    public void Hit(int n = 1) => Count += n;
}
