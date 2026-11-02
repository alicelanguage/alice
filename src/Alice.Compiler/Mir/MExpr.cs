namespace Alice.Compiler.Mir;

/// <summary>
/// MIR/AIR expression. <see cref="Type"/> and <see cref="Span"/> are optional while lowering migrates to typed
/// nodes (ADR 0032); the C# printer ignores both.
/// </summary>
public abstract class MExpr
{
    public AirType? Type;
    public Span? Span;
}

/// <summary>Leaf C# text: literals, names, type names. The typed leaves in MExpr.Core.cs derive from it while the
/// passes still read <see cref="Text"/> (ADR 0032 step 2); new code constructs those, not MCode.</summary>
public class MCode : MExpr
{
    public string Text;
    public MCode(string text) => Text = text;
    public static readonly MCode Empty = new("");
}

public sealed class MCall : MExpr
{
    public MExpr? Target;
    public string Name = "";
    public List<MExpr> Args = new();
    public bool Safe;
}

public sealed class MMember : MExpr
{
    public MExpr Target = null!;
    public string Name = "";
    public bool Safe;
}

public sealed class MInvoke : MExpr
{
    public MExpr Target = null!;
    public List<MExpr> Args = new();
}

public sealed class MNew : MExpr
{
    public string TypeCs = "";
    public List<MExpr> Args = new();
    public List<MExpr>? Init;        // collection initializer items
}

public sealed class MNewArray : MExpr
{
    public string ElemCs = "";
    public MExpr? Size;
    public List<MExpr>? Items;
}

public sealed class MIndex : MExpr
{
    public MExpr Target = null!;
    public List<MExpr> Args = new();
}

public sealed class MBinary : MExpr
{
    public AirBinOp Op;
    /// <summary>Evaluated in a checked context (integer + - * trap on overflow, ADR 0013); false where the C# form
    /// is wrapped in `unchecked(...)`. Nodes inside an `MBlock { Unchecked: true }` keep the default.</summary>
    public bool Checked = true;
    public MExpr L = null!;
    public MExpr R = null!;
}

public sealed class MUnary : MExpr
{
    public AirUnOp Op;
    /// <summary>Checked context for Neg/Inc/Dec, as <see cref="MBinary.Checked"/>.</summary>
    public bool Checked = true;
    public MExpr E = null!;
    public bool Postfix;
}

public sealed class MCond : MExpr
{
    public MExpr C = null!;
    public MExpr A = null!;
    public MExpr B = null!;
}

public sealed class MLambda : MExpr
{
    public List<string> Params = new();
    public bool Static;
    public bool Hot;          // [MethodImpl(AggressiveOptimization)]: a parallel chunk body, hot from its first call
    public MExpr? ExprBody;
    public List<MStmt>? Body;
    /// <summary>One parameter printed without parentheses: `static f => ...`.</summary>
    public bool BareParam;
    /// <summary>A block body of expression statements printed on one line: `(a) => { f(a); }`.</summary>
    public bool InlineBody;
}

public sealed class MCast : MExpr
{
    public string TypeCs = "";
    public MExpr E = null!;
}

public sealed class MIs : MExpr
{
    public MExpr E = null!;
    public string TypeCs = "";
}

/// <summary>Interpolated string formatted with the invariant culture; holes keep their static types (no boxing).</summary>
public sealed class MInterp : MExpr
{
    public List<string> Chunks = new();
    public List<MExpr> Holes = new();
}

public sealed class MTuple : MExpr { public List<MExpr> Items = new(); }

/// <summary>`{ k, v }` collection-initializer pair.</summary>
public sealed class MPair : MExpr
{
    public MExpr K = null!;
    public MExpr V = null!;
}

/// <summary>Argument with a prefix: `out var x`, `ref x`, `name: x`.</summary>
public sealed class MArg : MExpr
{
    public string Prefix = "";
    public MExpr E = null!;
}
