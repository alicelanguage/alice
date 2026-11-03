using System.Reflection;

namespace Alice.Compiler.Backend.Il.Opt;

/// <summary>
/// Method-level flags chosen from the emitted body (reports/il-shapes.md R9, R10):
/// <list type="bullet">
/// <item>R9: small loop-free, non-virtual bodies called from at most <see cref="MaxSites"/> places get
/// [AggressiveInlining]. In [AggressiveOptimization] callers (no PGO) the JIT's own heuristic decides on IL size and
/// shape: raytrace's 130-byte `sphere_isect` stayed a call in `ray_trace`'s loops (+17 % against the C# build,
/// whose 125-byte copy was inlined). The site bound keeps forced inlining from multiplying a body into a large
/// kernel: `prng` forced into lexer's `line` (4 sites) cost 5 %, while forced into hashmap's 2 sites it gained 3 %.
/// Bodies with a loop, an exception region, `localloc`, a `tail.` prefix, a self call or more than
/// <see cref="MaxLocals"/> locals are left alone (R9 blockers).</item>
/// <item>R10 (`+noao`, off by default): drop [AggressiveOptimization] (the C# backend's hot policy, mirrored by
/// AirToIl's HotPolicy). Measured on bend2 serial it costs 3-13 % (lexer, mandelbrot, bfs, hashmap): those kernels
/// run their loops in the first call, where tiering means Tier0 + OSR code.</item>
/// </list>
/// </summary>
public static class MethodFlags
{
    /// <summary>Largest body (instructions after IlBuilder's peepholes) marked [AggressiveInlining].</summary>
    public const int MaxInstructions = 120;

    /// <summary>R9: an inlinee with 32+ locals is refused by the JIT even with [AggressiveInlining].</summary>
    public const int MaxLocals = 24;

    /// <summary>Most call sites (call, callvirt, newobj in the whole module) of a method marked [AggressiveInlining].</summary>
    public const int MaxSites = 3;

    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IlModule, Dictionary<string, int>> sites = new();

    /// <summary>Call sites per method (by its listing name) over every body of <paramref name="module"/>.</summary>
    static Dictionary<string, int> Sites(IlModule module) => sites.GetValue(module, mod =>
    {
        var d = new Dictionary<string, int>();
        foreach (var t in mod.TypeDefOrder())
            foreach (var mm in t.Methods)
                if (mm.Body is { } b)
                    foreach (var c in BodyFacts.Of(b).Calls) d[c] = d.GetValueOrDefault(c) + 1;
        return d;
    });

    /// <summary>`ALICE_IL_NOINLINE=a,b`: method names never marked (experiments).</summary>
    static readonly HashSet<string> Skip = [.. (Environment.GetEnvironmentVariable("ALICE_IL_NOINLINE") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)];

    public static void Apply(IlMethodDef m, IlBuilder il)
    {
        if ((m.ImplAttributes & MethodImplAttributes.AggressiveOptimization) != 0
            && (IlOpt.On(IlOptPass.NoAo) || IlOpt.On(IlOptPass.NoAoLeaves) && Leaf(m, BodyFacts.Of(il))))
            m.ImplAttributes &= ~MethodImplAttributes.AggressiveOptimization;
        if (IlOpt.On(IlOptPass.AoLoops) && (m.ImplAttributes & MethodImplAttributes.AggressiveOptimization) == 0
            && m.Name is not (".cctor" or "Main") && m.GenericParameters.Count == 0 && BodyFacts.Of(il) is { Loop: true, Eh: false })
            m.ImplAttributes |= MethodImplAttributes.AggressiveOptimization;
        if (IlOpt.On(IlOptPass.Inline) && Inlinable(m, il))
            m.ImplAttributes |= MethodImplAttributes.AggressiveInlining;
    }

    /// <summary>A loop-free, non-recursive body (the hot policy's loop leaves; recursive fork kernels are not leaves).</summary>
    static bool Leaf(IlMethodDef m, BodyFacts f) => !f.Loop && !f.Calls.Contains(m.ToString());

    /// <summary>Whether <paramref name="m"/> is a small, loop-free body the JIT can inline (R9).</summary>
    public static bool Inlinable(IlMethodDef m, IlBuilder il)
    {
        const MethodImplAttributes Decided = MethodImplAttributes.NoInlining | MethodImplAttributes.AggressiveInlining
            | MethodImplAttributes.InternalCall | MethodImplAttributes.Synchronized;
        if ((m.ImplAttributes & Decided) != 0 || m.PInvoke != null) return false;
        if ((m.Attributes & (MethodAttributes.Virtual | MethodAttributes.Abstract | MethodAttributes.PinvokeImpl)) != 0) return false;
        if (m.Name is ".cctor" or "Main") return false;
        if (Skip.Contains(m.Name)) return false;
        if (m.GenericParameters.Count > 0) return false;   // shared generic code: dictionary lookups, R16
        if (il.Locals.Count > MaxLocals) return false;
        var facts = BodyFacts.Of(il);
        if (facts.Instructions > MaxInstructions || facts.Instructions == 0) return false;
        if (facts.Loop || facts.Eh || facts.Localloc || facts.Tail) return false;
        if (facts.Calls.Contains(m.ToString())) return false;
        // a fork dispatcher (`f` testing Par.Forkable, its `__fork_f_N` pair) recurses through its own clone and
        // closures: forcing it into callers only copies the fork machinery
        if (m.Name.StartsWith("__fork_", StringComparison.Ordinal) || facts.Calls.Any(c => c.Contains("__serial(", StringComparison.Ordinal) || c.Contains("::__fork_", StringComparison.Ordinal))) return false;
        return Sites(m.Owner.Module).GetValueOrDefault(m.ToString()) <= MaxSites;
    }
}

/// <summary>What <see cref="MethodFlags"/> needs to know about a body, read from <see cref="IlBuilder.Listing"/>
/// (the instruction list after IlBuilder's peepholes; the listing prints labels, regions and symbolic operands).</summary>
public sealed record BodyFacts(int Instructions, bool Loop, bool Eh, bool Localloc, bool Tail, List<string> Calls)
{
    public static BodyFacts Of(IlBuilder il) => Parse(il.Listing(optimize: true));

    public static BodyFacts Parse(string listing)
    {
        int n = 0;
        bool loop = false, eh = false, localloc = false, tail = false;
        var calls = new List<string>();
        var seen = new HashSet<string>();
        foreach (var raw in listing.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("// .try", StringComparison.Ordinal)) { eh = true; continue; }
            if (line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith(".locals", StringComparison.Ordinal)) continue;
            if (line[^1] == ':' && line[0] == 'L') { seen.Add(line[..^1]); continue; }
            n++;
            int sp = line.IndexOf(' ');
            string op = sp < 0 ? line : line[..sp];
            string arg = sp < 0 ? "" : line[(sp + 1)..];
            switch (op)
            {
                case "localloc": localloc = true; break;
                case "tail": tail = true; break;
                case "call" or "callvirt" or "newobj": calls.Add(arg); break;
                case "switch":
                    foreach (var t in arg.Trim('(', ')').Split(',', StringSplitOptions.TrimEntries)) if (seen.Contains(t)) loop = true;
                    break;
                default:
                    if ((op[0] == 'b' && op != "box" && op != "break" || op is "leave" or "leave.s") && seen.Contains(arg)) loop = true;
                    break;
            }
        }
        return new BodyFacts(n, loop, eh, localloc, tail, calls);
    }
}
