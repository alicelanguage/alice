namespace Alice.Compiler.Mir;

// Closure-conversion AIR (backend plan D1.5, ADR 0033 "Closure ABI"). Built only by Air/Passes/ClosureConvert.cs,
// which runs only for the IL backend: the C# path never sees these nodes. Dump forms are in Mir/MirDump.Closures.cs,
// a debugging C# spelling in Backend/Printer.Closures.cs.

public enum AirDisplayKind
{
    /// <summary>Heap display class: the closure may escape the method; written captures live in its fields.</summary>
    Class,
    /// <summary>
    /// Stack display struct (by-ref-like): the closure never escapes the call it is passed to. Read-only captures are
    /// value fields, written captures are `ref` fields to the method's own locals; the closure method is static and
    /// takes the display as its first parameter, `ref`.
    /// </summary>
    Struct,
}

/// <summary>
/// The runtime entry a display struct is passed to (Alice.Runtime ParKernels.cs). A kernel struct implements the
/// interface with its closure method as the instance method `Invoke` (the struct method's `ref` display parameter is
/// the instance's `this`): Range = `IParKernel` for `Par.Range&lt;TKernel&gt;(n, ref k[, grain])`, Reduce =
/// `IParKernel&lt;T&gt;` for `Par.Reduce&lt;T, TKernel&gt;(n, ref k, combine[, grain])`, Fork = `IForkBranch&lt;T&gt;` for
/// `Par.Fork&lt;A, B, TA, TB&gt;(ref a, ref b)` (and the 3-way form). None: a directly invoked closure.
/// </summary>
public enum AirKernelRole { None, Range, Reduce, Fork }

/// <summary>One captured variable: a field of a display type.</summary>
public sealed class AirCapture
{
    /// <summary>The field name (the captured local's name; `&lt;&gt;4__this` for `this`, `&lt;&gt;8__N` for a link).</summary>
    public string Field = "";
    /// <summary>The enclosing expression the field is initialized from at display creation (a local, a parameter,
    /// `this`, or a field of the enclosing closure's own display).</summary>
    public MExpr Source = null!;
    public AirType? Type;
    /// <summary>The field's C# type when known (from the declaration or the use site), else null.</summary>
    public string? TypeCs;
    /// <summary>Written by the closure or after its creation by the enclosing code.</summary>
    public bool Written;
    /// <summary>A `ref` field (struct displays only): the closure writes the enclosing local in place.</summary>
    public bool ByRef;
    public bool IsThis;
    /// <summary>The field holds a reference to a hoisted block display (<see cref="AirDisplayType.Hoisted"/>).</summary>
    public bool IsLink;
}

/// <summary>A synthesized display type: one per converted capturing closure, plus one per block whose locals are
/// written and captured by an escaping closure (<see cref="Hoisted"/>).</summary>
public sealed class AirDisplayType
{
    public string Name = "";
    public AirDisplayKind Kind;
    /// <summary>Holds hoisted locals of one block (shared by every closure that captures them); has no methods.</summary>
    public bool Hoisted;
    /// <summary>Struct displays: the runtime entry it is passed to (kernels never hold `ref` fields).</summary>
    public AirKernelRole Role;
    public List<AirCapture> Fields = new();
    /// <summary>A struct with `ref` fields: a `ref struct`, only ever invoked directly on the creating thread.</summary>
    public bool ByRefLike => Kind == AirDisplayKind.Struct && Fields.Any(f => f.ByRef);
    public List<AirClosureMethod> Methods = new();
}

/// <summary>A lambda body turned into a method.</summary>
public sealed class AirClosureMethod
{
    public string Name = "";
    /// <summary>The lambda's parameters as written (`lo`, `int x`); a struct display's method has the display first.</summary>
    public List<string> Params = new();
    public MExpr? ExprBody;
    public List<MStmt>? Body;
    /// <summary>Static: a capture-free lambda (owner = the `&lt;&gt;c` holder) or a struct display's method.</summary>
    public bool Static;
    public bool Hot;
    /// <summary>Capture-free only: the `&lt;&gt;c` static field caching the delegate (as <see cref="AirDelegateCreate.CachedField"/>).</summary>
    public string? CachedField;
    /// <summary>The display the method is an instance (Class) or static-with-ref-display (Struct) member of; null for
    /// capture-free lambdas, which live on the module's `&lt;&gt;c` holder class.</summary>
    public AirDisplayType? Owner;
}

/// <summary>
/// A delegate over a closure method. Capture-free: <see cref="Target"/> is null and <see cref="CachedField"/> names the
/// static field of the `&lt;&gt;c` holder that caches the one instance (created lazily on first use, as Roslyn's
/// `&lt;&gt;9__N`). Class display: <see cref="Target"/> is the display instance. Struct display: <see cref="Target"/> is
/// the display local, passed by ref (<see cref="ByRefTarget"/>); only legal in a non-escaping position.
/// </summary>
public sealed class AirDelegateCreate : MExpr
{
    public AirClosureMethod Method = null!;
    public string? CachedField;
    public MExpr? Target;
    public bool ByRefTarget;
}

/// <summary>Creates a display instance; each field is initialized from its <see cref="AirCapture.Source"/>
/// (a hoisted display starts empty: its fields are assigned where the locals were declared).</summary>
public sealed class AirDisplayNew : MExpr
{
    public AirDisplayType Display = null!;
}

/// <summary>A field of a display instance: a redirected captured local (read, or as an assignment target, write).</summary>
public sealed class AirDisplayField : MExpr
{
    public MExpr Display = null!;
    public AirDisplayType Owner = null!;
    public string Field = "";
}
