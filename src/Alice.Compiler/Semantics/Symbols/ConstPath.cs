namespace Alice.Compiler.Semantics;

/// <summary>A dotted constant path split into a type and a member chain: `DateTime.UnixEpoch.Year`, `Stopwatch.StartNew`.</summary>
public sealed class ConstPath
{
    public AType Owner = AType.Unknown;
    public List<(string Name, bool IsCall)> Members = new();
}
