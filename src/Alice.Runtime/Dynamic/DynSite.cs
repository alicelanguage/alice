using System.Runtime.CompilerServices;

namespace Alice.Runtime;

/// <summary>What a dynamic site does (ADR 0033, `Any` receivers on the IL backend).</summary>
public enum DynKind : byte
{
    /// <summary>`x.name(args)`.</summary>
    Call,
    /// <summary>`x.name`: a property, a public field, then a zero-argument method (Alice calls without parens).</summary>
    GetMember,
    /// <summary>`x.name = v`: a property setter, a public field, then the Alice setter `name=` (`__set_name`).</summary>
    SetMember,
    /// <summary>`x[i, ...]`.</summary>
    GetIndex,
    /// <summary>`x[i, ...] = v`.</summary>
    SetIndex,
    /// <summary>`l op r` with a dynamic operand.</summary>
    BinaryOp,
    /// <summary>`op x` with a dynamic operand.</summary>
    UnaryOp,
    /// <summary>Implicit conversion of a dynamic value to a static type (a typed local, a condition).</summary>
    Convert,
    /// <summary>Explicit conversion (`x.as(T)` / a cast).</summary>
    Cast,
    /// <summary>`f.(args)`: a delegate held in a dynamic value.</summary>
    Invoke,
}

/// <summary>Operators of <see cref="DynKind.BinaryOp"/> / <see cref="DynKind.UnaryOp"/> sites (C# semantics: checked
/// integer arithmetic, truncating `/` and `%`, the same as the C# backend's `dynamic`).</summary>
public enum DynOp : byte
{
    None,
    Add, Sub, Mul, Div, Mod,
    Shl, Shr, UShr,
    BitAnd, BitOr, BitXor,
    Eq, Ne, Lt, Le, Gt, Ge,
    Neg, Plus, Not, BitNot,
}

/// <summary>
/// One dynamic call site: a static field of the compiled module (one per site, initialized once), passed by
/// reference to <see cref="Dyn"/>. It caches resolved targets by the receiver's runtime type and the arguments'
/// runtime types: a monomorphic entry first, then a polymorphic inline cache of <see cref="PolySize"/> entries; past
/// that the site is megamorphic and replaces entries round-robin (resolution then hits the per-type candidate cache).
/// Entries are immutable and published by one reference write, so concurrent callers at worst resolve twice.
/// </summary>
public struct DynSite
{
    /// <summary>Entries of the polymorphic cache after the monomorphic one.</summary>
    public const int PolySize = 4;

    public readonly DynKind Kind;
    /// <summary>The member name as the compiler spelled it (`empty_q`, `Length`), tried first; then its .NET spelling
    /// (<see cref="Names.Dotnet"/> of the Alice name: `empty?` -> IsEmpty).</summary>
    public readonly string Name;
    /// <summary>Argument count of the site (index arguments for GetIndex/SetIndex, without the assigned value).</summary>
    public readonly int Arity;
    public readonly DynOp Op;
    /// <summary>A safe call (`x&amp;.name`): a nil receiver gives nil instead of an error.</summary>
    public readonly bool Safe;
    /// <summary>The target type of a Convert/Cast site.</summary>
    public readonly Type? To;

    internal DynEntry? Mono;
    internal DynEntry?[]? Poly;
    internal int Misses;
    internal readonly string DotnetName;

    public DynSite(DynKind kind, string name, int arity = 0, bool safe = false)
    {
        Kind = kind;
        Name = name.StartsWith('@') ? name[1..] : name;
        Arity = arity;
        Safe = safe;
        DotnetName = Names.Dotnet(AliceSpelling(Name));
    }

    /// <summary>An operator site.</summary>
    public DynSite(DynKind kind, DynOp op) : this(kind, op.ToString(), kind == DynKind.BinaryOp ? 2 : 1) => Op = op;

    /// <summary>A Convert/Cast site to <paramref name="to"/>.</summary>
    public DynSite(DynKind kind, Type to) : this(kind, to.Name, 1) => To = to;

    /// <summary>Resolved entries currently cached (monomorphic + polymorphic), for tests and `alice explain`.</summary>
    public readonly int CachedCount
    {
        get
        {
            int n = Mono != null ? 1 : 0;
            if (Poly != null) foreach (var e in Poly) if (e != null) n++;
            return n;
        }
    }

    /// <summary>Lookups that missed the cache (each one resolved through reflection or the candidate cache).</summary>
    public readonly int MissCount => Misses;

    /// <summary>More receiver/argument type combinations seen than the inline cache holds.</summary>
    public readonly bool IsMegamorphic => Misses > PolySize + 1;

    /// <summary>`empty_q` -> `empty?`, `save_b` -> `save!` (the compiler's mangling of predicate and bang names).</summary>
    static string AliceSpelling(string n) =>
        n.EndsWith("_q", StringComparison.Ordinal) ? n[..^2] + "?" : n.EndsWith("_b", StringComparison.Ordinal) ? n[..^2] + "!" : n;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal readonly DynEntry? Find(Type recv, Type? recv2, ReadOnlySpan<object?> args)
    {
        var m = Mono;
        if (m != null && m.Matches(recv, recv2, args)) return m;
        return FindPoly(recv, recv2, args);
    }

    readonly DynEntry? FindPoly(Type recv, Type? recv2, ReadOnlySpan<object?> args)
    {
        var p = Poly;
        if (p == null) return null;
        foreach (var e in p)
            if (e != null && e.Matches(recv, recv2, args)) return e;
        return null;
    }

    internal void Add(DynEntry e)
    {
        int miss = Misses++;
        if (Mono == null) { Mono = e; return; }
        var old = Poly;
        var p = new DynEntry?[PolySize];
        if (old != null) Array.Copy(old, p, PolySize);
        int free = Array.IndexOf(p, null);
        p[free >= 0 ? free : miss % PolySize] = e;   // megamorphic: round-robin replacement
        Poly = p;
    }
}

/// <summary>One cached resolution: valid for exactly this receiver type (and second operand type for operators)
/// and these argument runtime types (null: a nil argument).</summary>
internal sealed class DynEntry
{
    public readonly Type Recv;
    public readonly Type? Recv2;
    public readonly Type?[] ArgTypes;
    public readonly DynThunk Thunk;

    public DynEntry(Type recv, Type? recv2, ReadOnlySpan<object?> args, DynThunk thunk)
    {
        Recv = recv;
        Recv2 = recv2;
        ArgTypes = new Type?[args.Length];
        for (int i = 0; i < args.Length; i++) ArgTypes[i] = args[i]?.GetType();
        Thunk = thunk;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Matches(Type recv, Type? recv2, ReadOnlySpan<object?> args)
    {
        if (!ReferenceEquals(recv, Recv) || !ReferenceEquals(recv2, Recv2)) return false;
        var ts = ArgTypes;
        if (ts.Length != args.Length) return false;
        for (int i = 0; i < ts.Length; i++)
            if (!ReferenceEquals(args[i]?.GetType(), ts[i])) return false;
        return true;
    }
}
