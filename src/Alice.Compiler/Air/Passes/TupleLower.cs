using System.Text;
using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;

namespace Alice.Compiler.Air.Passes;

/// <summary>
/// Tuples without Roslyn. A tuple literal is a `System.ValueTuple` constructor call (8+ elements nest the rest in
/// TRest); a named element read is `ItemN` (`Rest.ItemN` past the seventh); a deconstruction reads ItemN from a temp
/// (all values are evaluated before any target is written, so `(a, b) = (b, a)` swaps) or calls the type's
/// `Deconstruct(out ...)`; tuple syntax in local types is spelled as ValueTuple, and the element names of every such
/// local are recorded in <see cref="IlLowerContext.TupleLocalNames"/> for the PDB / TupleElementNames attribute (casts,
/// allocations, defaults and type tests are respelled too, without names).
/// </summary>
public sealed class TupleLower(IlLowerContext? ctx = null) : IlPass(ctx)
{
    public override string Name => "il-tuple";

    protected override MExpr LowerExpr(MExpr e)
    {
        switch (e)
        {
            case MTuple t:
                {
                    var types = new List<AirType>();
                    var names = new List<string?>();
                    var items = new List<MExpr>();
                    for (int k = 0; k < t.Items.Count; k++)
                    {
                        var item = t.Items[k];
                        string? name = null;
                        if (item is MArg { Prefix: [.., ':', ' '] p } a) { name = p[..^2]; item = a.E; }
                        var ty = t.Type is AirType.Tuple tt && k < tt.Items.Count ? tt.Items[k] : item.Type;
                        if (!IlTypes.Known(ty))
                        {
                            Unsupported(null, "tuple literal with an element of unknown type");
                            return e;
                        }
                        items.Add(item);
                        types.Add(ty!);
                        names.Add(name ?? (t.Type is AirType.Tuple { Names: { } tn } && k < tn.Count ? tn[k] : null));
                    }
                    var n = New(items, types);
                    n.Type = new AirType.Tuple(types, names.Any(x => x != null) ? names.Select((x, k) => x ?? "Item" + (k + 1)).ToList() : null);
                    return n;
                }
            case MMember { Target.Type: AirType.Tuple tt } m when !tt.Nullable:
                {
                    int k = -1;
                    if (m.Name.StartsWith("Item") && int.TryParse(m.Name.AsSpan(4), out int item) && item >= 1 && item <= tt.Items.Count) k = item - 1;
                    else if (tt.Names != null) k = tt.Names.ToList().FindIndex(n => n == m.Name || n.Length > 0 && Binder.Mangle(n) == m.Name);
                    if (k < 0) return e;
                    return Item(m.Target, tt, k);
                }
            // tuple syntax in the other type spellings (casts, allocations, defaults, type tests)
            case MCast c: c.TypeCs = Spell(c.TypeCs); break;
            case MNew n: n.TypeCs = Spell(n.TypeCs); break;
            case MNewArray a: a.ElemCs = Spell(a.ElemCs); break;
            case AirDefault { TypeCs: { } dt } d: d.TypeCs = Spell(dt); break;
            case MIs i: i.TypeCs = Spell(i.TypeCs); break;
            case AirAs a: a.TypeCs = Spell(a.TypeCs); break;
        }
        return e;
    }

    /// <summary>`new ValueTuple&lt;...&gt;(...)`, nesting from the eighth element on.</summary>
    static MNew New(List<MExpr> items, List<AirType> types)
    {
        if (items.Count <= 7)
            return new MNew { TypeCs = ValueTuple(types), Args = [.. items] };
        var rest = New(items.Skip(7).ToList(), types.Skip(7).ToList());
        rest.Type = new AirType.Tuple(types.Skip(7).ToList(), null);
        return new MNew { TypeCs = ValueTuple(types), Args = [.. items.Take(7), rest] };
    }

    static string ValueTuple(IReadOnlyList<AirType> types) =>
        types.Count <= 7
            ? "global::System.ValueTuple<" + string.Join(", ", types.Select(t => Spell(t.ToCs()))) + ">"
            : "global::System.ValueTuple<" + string.Join(", ", types.Take(7).Select(t => Spell(t.ToCs()))) + ", " + ValueTuple(types.Skip(7).ToList()) + ">";

    /// <summary>Element k of a tuple value: Item(k+1), through Rest past the seventh.</summary>
    static MExpr Item(MExpr target, AirType.Tuple tt, int k)
    {
        MExpr cur = target;
        int skip = 0;
        while (k - skip >= 7)
        {
            var restType = new AirType.Tuple(tt.Items.Skip(skip + 7).ToList(), null);
            cur = new MMember { Target = cur, Name = "Rest", Type = restType };
            skip += 7;
        }
        return new MMember { Target = cur, Name = "Item" + (k - skip + 1), Type = tt.Items[k] };
    }

    protected override List<MStmt>? Lower(MStmt s)
    {
        switch (s)
        {
            case MDecl d when d.TypeCs is { } cs && cs.Contains('('):
                {
                    var names = new List<string?>();
                    if (Spell(cs, names) is { } spelled && spelled != cs)
                    {
                        d.TypeCs = spelled;
                        if (names.Any(n => n != null)) Ctx.TupleLocalNames[d.Name] = names;
                    }
                    return null;
                }
            case MDeconstruct dc:
                return Deconstruct(dc);
        }
        return null;
    }

    List<MStmt>? Deconstruct(MDeconstruct dc)
    {
        var vt = dc.Value.Type;
        string tmp = Ctx.Temp("t");
        if (vt is AirType.Tuple tt && !tt.Nullable && tt.Items.Count == dc.Names.Count)
        {
            var spelled = new AirType.ClrRef(ValueTuple(tt.Items), null, []);
            var o = new List<MStmt> { Decl(tmp, spelled, dc.Value) };
            for (int k = 0; k < dc.Names.Count; k++)
            {
                if (dc.Names[k] == "_") continue;
                var read = Item(Local(tmp, tt), tt, k);
                o.Add(dc.Declare ? Decl(dc.Names[k], tt.Items[k], read) : new MAssign { Target = Local(dc.Names[k], tt.Items[k]), Value = read });
            }
            return o;
        }
        if (IlTypes.Clr(vt) is { } clr && IlTypes.Method(clr, "Deconstruct", dc.Names.Count) is { } m && m.GetParameters().All(p => p.IsOut))
        {
            var o = new List<MStmt> { Decl(tmp, vt!, dc.Value) };
            var call = Call(Local(tmp, vt), "Deconstruct", IlTypes.Void);
            foreach (var (name, p) in dc.Names.Zip(m.GetParameters()))
            {
                if (IlTypes.FromClr(p.ParameterType, vt as AirType.ClrRef) is not { } pt)
                {
                    Unsupported(dc, $"Deconstruct of `{vt!.ToCs()}` with a parameter of unknown type");
                    return null;
                }
                string target = name;
                if (name == "_" || dc.Declare) o.Add(Decl(target = name == "_" ? Ctx.Temp("discard") : name, pt, null));
                call.Args.Add(new MArg { Prefix = "out ", E = Local(target, pt) });
            }
            o.Add(Do(call));
            return o;
        }
        Unsupported(dc, "deconstruction of a value of unknown type");
        return null;
    }

    /// <summary>A C# type spelling with tuple syntax rewritten to ValueTuple (names dropped).</summary>
    public static string Spell(string cs) => Spell(cs, null) ?? cs;

    /// <summary>
    /// Rewrites `(int a, string)` anywhere in a C# type spelling to `global::System.ValueTuple&lt;int, string&gt;`,
    /// appending element names in pre-order to <paramref name="names"/>; null when the spelling does not parse.
    /// </summary>
    public static string? Spell(string cs, List<string?>? names)
    {
        if (!cs.Contains('(')) return cs;
        int i = 0;
        var sb = new StringBuilder();
        if (!Type(cs, ref i, sb, names)) return null;
        Ws(cs, ref i);
        return i == cs.Length ? sb.ToString() : null;
    }

    static void Ws(string s, ref int i)
    {
        while (i < s.Length && s[i] == ' ') i++;
    }

    static bool Type(string s, ref int i, StringBuilder o, List<string?>? names)
    {
        Ws(s, ref i);
        if (i >= s.Length) return false;
        if (s[i] == '(')
        {
            i++;
            var elems = new List<string>();
            var elemNames = new List<string?>();
            int at = names?.Count ?? 0;
            var inner = new List<string?>();
            while (true)
            {
                var e = new StringBuilder();
                if (!Type(s, ref i, e, inner)) return false;
                Ws(s, ref i);
                string? name = null;
                int st = i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '@')) i++;
                if (i > st) name = s[st..i];
                elems.Add(e.ToString());
                elemNames.Add(name);
                Ws(s, ref i);
                if (i < s.Length && s[i] == ',') { i++; continue; }
                if (i < s.Length && s[i] == ')') { i++; break; }
                return false;
            }
            if (elems.Count < 2) return false;
            o.Append(Nest(elems));
            if (names != null) { names.AddRange(elemNames); names.AddRange(inner); }
        }
        else
        {
            int st = i;
            while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '.' or ':' or '@')) i++;
            if (i == st) return false;
            o.Append(s, st, i - st);
            if (i < s.Length && s[i] == '<')
            {
                o.Append('<');
                i++;
                while (true)
                {
                    if (!Type(s, ref i, o, names)) return false;
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ',') { o.Append(", "); i++; continue; }
                    if (i < s.Length && s[i] == '>') { o.Append('>'); i++; break; }
                    return false;
                }
            }
        }
        // suffixes: T?, T[], T[,]
        while (i < s.Length && (s[i] == '?' || s[i] == '['))
        {
            if (s[i] == '?') { o.Append('?'); i++; continue; }
            int st = i;
            while (i < s.Length && s[i] != ']') i++;
            if (i == s.Length) return false;
            i++;
            o.Append(s, st, i - st);
        }
        return true;
    }

    static string Nest(List<string> elems) =>
        elems.Count <= 7
            ? "global::System.ValueTuple<" + string.Join(", ", elems) + ">"
            : "global::System.ValueTuple<" + string.Join(", ", elems.Take(7)) + ", " + Nest(elems.Skip(7).ToList()) + ">";
}
