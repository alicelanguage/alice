// THROWAWAY SPIKE (backend plan D1.1): see README.md in this folder. Not production code.
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using Alice.Compiler.Mir;
using static Alice.Compiler.Backend.IlSpike.ClrResolve;

namespace Alice.Compiler.Backend.IlSpike;

/// <summary>Spike switches for the three IL-only optimizations (README: "Optimizations").</summary>
public sealed class SpikeOptions
{
    /// <summary>(a) array loads/stores without bounds checks in these methods (hard-coded proof, see README).</summary>
    public HashSet<string> NoBounds = new();
    /// <summary>Methods emitted without branch shortening (set after a corrupt serialization, see README).</summary>
    public HashSet<string> LongBranches = new();
    /// <summary>(b) `tail.` on self-recursive calls in return position.</summary>
    public bool TailCalls;
    /// <summary>(b, variant) `tail.` on every call to an Alice method in return position (same return type).</summary>
    public bool TailAll;
    /// <summary>(c) `Own&lt;T&gt;.Rent(constant)` arrays become `localloc` buffers (pointer locals; implies no bounds checks on them).</summary>
    public bool StackArrays;
    /// <summary>(c) zero the localloc buffer (Rent returns zeroed memory; SkipLocalsInit leaves localloc dirty).</summary>
    public bool StackZero = true;
    /// <summary>Methods the C# backend marks [MethodImpl(AggressiveInlining)] / [MethodImpl(NoInlining)] (read from its output).</summary>
    public HashSet<string> Inline = new(), NoInline = new();
    /// <summary>Experiment switch: keep every `var` in a local (no stack scheduling of single-use locals).</summary>
    public bool NoStackSchedule;
    /// <summary>Methods the C# backend marks [MethodImpl(AggressiveOptimization)] (read from its output).</summary>
    public HashSet<string> Hot = new();
}

/// <summary>One Alice static method's IL signature and its MIR body.</summary>
public sealed class SpikeMethod
{
    public string Name = "";
    public Type Ret = typeof(void);
    public List<(string Name, Type Type)> Params = new();
    public List<MStmt> Body = new();
    public MethodBuilder Builder = null!;
    public bool IsMain;
}

/// <summary>Walks one MIR body and emits IL with ILGenerator.</summary>
public sealed class IlBody
{
    readonly ClrResolve r;
    readonly Dictionary<string, List<SpikeMethod>> methods;
    readonly SpikeMethod self;
    readonly SpikeOptions opt;
    readonly ILGenerator il;
    readonly List<Dictionary<string, LocalBuilder>> scopes = new();
    readonly Dictionary<string, (int Index, Type Type)> args = new();
    readonly Stack<(Label Break, Label Continue)> loops = new();
    readonly Dictionary<LocalBuilder, (Type Elem, int Length)> stackArrays = new();
    int uncheckedDepth;

    public IlBody(ClrResolve r, Dictionary<string, List<SpikeMethod>> methods, SpikeMethod self, SpikeOptions opt, ILGenerator il, bool[]? shortPlan = null)
    {
        plan = shortPlan;
        this.r = r;
        this.methods = methods;
        this.self = self;
        this.opt = opt;
        this.il = il;
        for (int i = 0; i < self.Params.Count; i++) args[self.Params[i].Name] = (i, self.Params[i].Type);
    }

    bool Checked => uncheckedDepth == 0;

    // ---- branch shortening: pass 1 (scratch method) records offsets, pass 2 uses the short form where it fits ----
    readonly bool[]? plan;
    readonly List<(int End, Label Target)> branches = new();
    readonly Dictionary<Label, int> marks = new();

    void Mark(Label l)
    {
        marks[l] = il.ILOffset;
        il.MarkLabel(l);
    }

    void Br(OpCode op, Label l)
    {
        int k = branches.Count;
        il.Emit(plan != null && plan[k] ? Short(op) : op, l);
        branches.Add((il.ILOffset, l));
    }

    /// <summary>After pass 1 (all long forms): which branches stay in sbyte range once shortened (shortening only shrinks distances).</summary>
    public string Debug() => string.Join(" ", branches.Select((b, i) => $"{i}:{b.End}->{(marks.TryGetValue(b.Target, out var t) ? t : -999)}{(plan != null && plan[i] ? "s" : "")}"));

    public bool[] ShortPlan() => branches.Select(b => marks[b.Target] - b.End is >= -125 and <= 120).ToArray();

    static OpCode Short(OpCode op) =>
        op == OpCodes.Br ? OpCodes.Br_S : op == OpCodes.Brtrue ? OpCodes.Brtrue_S : op == OpCodes.Brfalse ? OpCodes.Brfalse_S :
        op == OpCodes.Beq ? OpCodes.Beq_S : op == OpCodes.Bne_Un ? OpCodes.Bne_Un_S :
        op == OpCodes.Blt ? OpCodes.Blt_S : op == OpCodes.Blt_Un ? OpCodes.Blt_Un_S : op == OpCodes.Ble ? OpCodes.Ble_S : op == OpCodes.Ble_Un ? OpCodes.Ble_Un_S :
        op == OpCodes.Bgt ? OpCodes.Bgt_S : op == OpCodes.Bgt_Un ? OpCodes.Bgt_Un_S : op == OpCodes.Bge ? OpCodes.Bge_S : op == OpCodes.Bge_Un ? OpCodes.Bge_Un_S :
        throw new InvalidOperationException(op.Name);

    static bool Terminates(List<MStmt> body) => body.Count > 0 && body[^1] switch
    {
        MReturn or MThrow or MBreak or MContinue => true,
        MIf { Else: { } e } i => Terminates(i.Then) && Terminates(e),
        MBlock { Lock: null } b => Terminates(b.Body),
        _ => false,
    };

    /// <summary>Branch to <paramref name="target"/> when <paramref name="cond"/> == <paramref name="when"/>, fusing
    /// comparisons into `blt`/`bge.un`... and `!`/`&amp;&amp;`/`||` into the branch structure, as Roslyn does.</summary>
    void Jump(MExpr cond, bool when, Label target)
    {
        switch (cond)
        {
            case MUnary { Op: AirUnOp.Not } n: Jump(n.E, !when, target); return;
            case MBinary { Op: AirBinOp.And or AirBinOp.Or } b:
                {
                    bool and = b.Op == AirBinOp.And;
                    if (and != when) { Jump(b.L, when, target); Jump(b.R, when, target); return; }   // (a&&b)==false: a false or b false; (a||b)==true likewise
                    var skip = il.DefineLabel();
                    Jump(b.L, !when, skip);
                    Jump(b.R, when, target);
                    Mark(skip);
                    return;
                }
            case MBinary { Op: AirBinOp.Eq or AirBinOp.Ne } z when Comparable(z) && (IsZero(z.R) || IsZero(z.L)):
                {
                    // `x == 0` / `x != 0`: brfalse / brtrue on x, as Roslyn emits
                    var x = IsZero(z.R) ? z.L : z.R;
                    var (lt, rt, ll, rl) = Operands(z);
                    var xt = IsNumeric(lt) && IsNumeric(rt) || ll != null || rl != null ? Promote(lt, rt, ll, rl) : lt;
                    if (IsFloat(xt)) { Plain(cond, when, target); return; }
                    Expr(x, xt);
                    Br((z.Op == AirBinOp.Ne) == when ? OpCodes.Brtrue : OpCodes.Brfalse, target);
                    return;
                }
            case MBinary { Op: AirBinOp.Eq or AirBinOp.Ne or AirBinOp.Lt or AirBinOp.Le or AirBinOp.Gt or AirBinOp.Ge } b when Comparable(b):
                {
                    var pt = CompareOperands(b);
                    bool un = IsUnsigned(pt), fl = IsFloat(pt);
                    var op = (b.Op, when) switch
                    {
                        (AirBinOp.Eq, true) or (AirBinOp.Ne, false) => OpCodes.Beq,
                        (AirBinOp.Ne, true) or (AirBinOp.Eq, false) => OpCodes.Bne_Un,
                        (AirBinOp.Lt, true) => un ? OpCodes.Blt_Un : OpCodes.Blt,
                        (AirBinOp.Le, true) => un ? OpCodes.Ble_Un : OpCodes.Ble,
                        (AirBinOp.Gt, true) => un ? OpCodes.Bgt_Un : OpCodes.Bgt,
                        (AirBinOp.Ge, true) => un ? OpCodes.Bge_Un : OpCodes.Bge,
                        // negated: unordered (NaN) must take the "false" branch, so floats use the .un forms
                        (AirBinOp.Lt, false) => un || fl ? OpCodes.Bge_Un : OpCodes.Bge,
                        (AirBinOp.Le, false) => un || fl ? OpCodes.Bgt_Un : OpCodes.Bgt,
                        (AirBinOp.Gt, false) => un || fl ? OpCodes.Ble_Un : OpCodes.Ble,
                        (AirBinOp.Ge, false) => un || fl ? OpCodes.Blt_Un : OpCodes.Blt,
                        _ => throw new InvalidOperationException(),
                    };
                    Br(op, target);
                    return;
                }
        }
        Plain(cond, when, target);
    }

    void Plain(MExpr cond, bool when, Label target)
    {
        Expr(cond, typeof(bool));
        Br(when ? OpCodes.Brtrue : OpCodes.Brfalse, target);
    }

    bool IsZero(MExpr e) => Literal(e, out var v) && v is int or uint or long or ulong && System.Convert.ToDecimal(v) == 0m || e is MCode { Text: "false" };

    (Type L, Type R, object? LL, object? RL) Operands(MBinary b)
    {
        Literal(b.L, out var ll);
        Literal(b.R, out var rl);
        return (Infer(b.L), Infer(b.R), ll, rl);
    }

    /// <summary>A numeric comparison, or ==/!= of bools or enums: compares by value with one IL compare/branch.</summary>
    bool Comparable(MBinary b)
    {
        var (lt, rt, ll, rl) = Operands(b);
        return IsNumeric(lt) && IsNumeric(rt) || ll != null && IsNumeric(rt) || rl != null && IsNumeric(lt)
            || lt == rt && (lt == typeof(bool) || lt.IsEnum) && b.Op is AirBinOp.Eq or AirBinOp.Ne;
    }

    /// <summary>Emits both operands of a <see cref="Comparable"/> comparison in their promoted type.</summary>
    Type CompareOperands(MBinary b)
    {
        var (lt, rt, ll, rl) = Operands(b);
        var pt = IsNumeric(lt) && IsNumeric(rt) || ll != null && IsNumeric(rt) || rl != null && IsNumeric(lt) ? Promote(lt, rt, ll, rl) : lt;
        Expr(b.L, pt);
        Expr(b.R, pt);
        return pt;
    }

    bool NoBounds => opt.NoBounds.Contains(self.Name);

    public void Emit()
    {
        scopes.Add(new());
        if (!opt.NoStackSchedule) CountUses();
        if (self.IsMain)
        {
            il.Emit(OpCodes.Call, typeof(Alice.Runtime.Prelude).GetMethod("init", System.Type.EmptyTypes)!);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, typeof(Alice.Runtime.Prelude).GetProperty("ARGV")!.GetSetMethod()!);
        }
        Stmts(self.Body);
        if (Terminates(self.Body)) return;
        // fall-through end of a void method (Roslyn adds the same `ret`)
        if (self.Ret == typeof(void)) il.Emit(OpCodes.Ret);
        else { il.Emit(OpCodes.Ldstr, "unreachable"); il.Emit(OpCodes.Newobj, typeof(InvalidOperationException).GetConstructor([typeof(string)])!); il.Emit(OpCodes.Throw); }
    }

    // ---------------- statements ----------------

    void Stmts(List<MStmt> list)
    {
        scopes.Add(new());
        for (int i = 0; i < list.Count; i++)
        {
            // Roslyn's stack scheduling: `var x = e;` whose only use is the first value the next statement loads keeps
            // e on the evaluation stack (no stloc/ldloc, one local less). IL size gates the JIT's inliner (128 B with PGO).
            if (list[i] is MDecl { TypeCs: null, Init: { } init } d && i + 1 < list.Count && uses != null && uses.GetValueOrDefault(d.Name) == 1
                && !(opt.StackArrays && init is MCall { Name: "Rent" }) && !d.Name.StartsWith('@') && SafeFirst(list[i + 1]) is MCode { Text: var first } && first == d.Name)
            {
                var t = Infer(init);
                Expr(init, t);
                stacked[d.Name] = t;
                continue;
            }
            Stmt(list[i]);
            if (stacked.Count > 0) throw new InvalidOperationException("stack-scheduled local not consumed: " + string.Join(",", stacked.Keys));
        }
        scopes.RemoveAt(scopes.Count - 1);
    }

    // ---- stack scheduling of single-use locals ----
    readonly Dictionary<string, Type> stacked = new();
    Dictionary<string, int>? uses;

    /// <summary>MCode name occurrences in the whole body (writes count too); names also bound by for/deconstruct are excluded.</summary>
    void CountUses()
    {
        uses = new();
        var bound = new HashSet<string>();
        var decls = new Dictionary<string, int>();
        void V(object? o)
        {
            switch (o)
            {
                case null or string: return;
                case MCode c: uses[c.Text] = uses.GetValueOrDefault(c.Text) + 1; return;
                case MFor f: bound.Add(f.Var); break;
                case MDeconstruct dc: foreach (var n in dc.Names) bound.Add(n); break;
                case MDecl d: decls[d.Name] = decls.GetValueOrDefault(d.Name) + 1; break;
                case MLambda: bound.UnionWith(uses.Keys); return;
                case System.Collections.IEnumerable list: foreach (var x in list) V(x); return;
            }
            if (o is not (MExpr or MStmt or MCatch)) return;
            foreach (var f in o.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                if (!f.FieldType.IsPrimitive && f.FieldType != typeof(string) && !f.FieldType.IsEnum && f.FieldType != typeof(AirType)) V(f.GetValue(o));
        }
        V(self.Body);
        foreach (var n in bound) uses[n] = 99;
        foreach (var (n, k) in decls) if (k > 1) uses[n] = 99;
    }

    MExpr? SafeFirst(MStmt s)
    {
        try { return FirstLeaf(s); }
        catch (SpikeUnsupported) { return null; }
    }

    /// <summary>The MCode this statement emits first, when nothing (address, constant, branch) precedes its load.</summary>
    MExpr? FirstLeaf(MStmt s) => s switch
    {
        MDecl { Init: { } i } d when !(d.Init is MCode { Text: "default!" or "default" }) => First(i),
        MAssign { Op: "=", Target: MCode, Value: var v } => First(v),
        MAssign { Op: "=", Target: MIndex { Target: var arr }, Value: var v } when !(arr is MCode pc && FindLocal(pc.Text) is { } pl && stackArrays.ContainsKey(pl)) && Infer(arr).IsArray && !NoBounds => First(arr),
        MReturn { Value: { } v } => First(v),
        MExprStmt { E: MCall c } => First(c),
        _ => null,
    };

    MExpr? First(MExpr e)
    {
        e = Lower(e);
        switch (e)
        {
            case MCode c: return TryLiteral(c.Text, out _, out _) ? null : c;
            case MBinary { Op: AirBinOp.Coalesce }: return null;
            case MBinary b: return Literal(b.L, out var lv) && lv != null ? null : First(b.L);
            case MUnary { Op: AirUnOp.NotNull or AirUnOp.Not or AirUnOp.BitNot } u: return First(u.E);
            case MUnary { Op: AirUnOp.Neg } u when !(u.Checked && Checked && IsIntegral(Widen(Infer(u.E)))) && !Literal(u, out _): return First(u.E);
            case MCast c when !Literal(c.E, out _): return First(c.E);
            case MCall { Target: null, Name: "unchecked", Args: [var a] }: return First(a);
            case MCall c when c.Args.All(x => x is not MArg) && !c.Safe:
                {
                    var rs = Resolve(c);
                    if (rs.Receiver == null) return c.Args.Count > 0 ? First(c.Args[0]) : null;
                    if (rs.Extension || !Infer(rs.Receiver).IsValueType) return First(rs.Receiver);
                    return null;
                }
            case MMember m when !m.Safe:
                {
                    if (m.Target is MCode tc && !FindLocalOrArg(tc.Text) && r.TryType(tc.Text) != null) return null;
                    if (m.Target is MCode pc && FindLocal(pc.Text) is { } pl && stackArrays.ContainsKey(pl)) return null;
                    var t = Infer(m.Target);
                    return t.IsValueType && m.Target is MCode ? null : First(m.Target);
                }
            case MIndex { Args.Count: 1 } ix:
                {
                    if (ix.Target is MCode pc && FindLocal(pc.Text) is { } pl && stackArrays.ContainsKey(pl)) return null;
                    var t = Infer(ix.Target);
                    return t.IsArray || t == typeof(string) ? First(ix.Target) : null;
                }
            case MNew n when n.Init == null && n.Args.Count > 0: return First(n.Args[0]);
            case MNewArray { Size: { } sz, Items: null }: return First(sz);
            case MTuple t: return First(t.Items[0]);
        }
        return null;
    }

    void Stmt(MStmt s)
    {
        switch (s)
        {
            case MDecl d: Decl(d); break;
            case MAssign a: Assign(a.Target, a.Op, a.Value); break;
            case MExprStmt es: ExprStmt(es.E); break;
            case MIf i:
                {
                    var els = il.DefineLabel();
                    var end = il.DefineLabel();
                    Jump(i.Cond, false, els);
                    Stmts(i.Then);
                    if (i.Else != null && !Terminates(i.Then)) Br(OpCodes.Br, end);
                    Mark(els);
                    if (i.Else != null) Stmts(i.Else);
                    Mark(end);
                    break;
                }
            case MWhile w:
                {
                    var body = il.DefineLabel();
                    var cond = il.DefineLabel();
                    var brk = il.DefineLabel();
                    Br(OpCodes.Br, cond);
                    Mark(body);
                    loops.Push((brk, cond));
                    Stmts(w.Body);
                    loops.Pop();
                    Mark(cond);
                    Jump(w.Cond, true, body);
                    Mark(brk);
                    break;
                }
            case MFor f: For(f); break;
            case MReturn ret:
                if (ret.Value != null)
                {
                    if ((opt.TailCalls || opt.TailAll) && ret.Value is MCall c && (IsSelfCall(c) || opt.TailAll && IsAliceCall(c))) { Call(c, tail: true); il.Emit(OpCodes.Ret); break; }
                    Expr(ret.Value, self.Ret);
                }
                il.Emit(OpCodes.Ret);
                break;
            case MBreak: Br(OpCodes.Br, loops.Peek().Break); break;
            case MContinue: Br(OpCodes.Br, loops.Peek().Continue); break;
            case MBlock b:
                if (b.Lock != null) throw new SpikeUnsupported("MBlock:lock");
                if (b.Unchecked) uncheckedDepth++;
                Stmts(b.Body);
                if (b.Unchecked) uncheckedDepth--;
                break;
            case MDeconstruct dc: Deconstruct(dc); break;
            case MThrow t:
                Expr(t.Value, null);
                il.Emit(OpCodes.Throw);
                break;
            default: throw new SpikeUnsupported(s.GetType().Name);
        }
    }

    LocalBuilder Declare(string name, Type t)
    {
        var lb = il.DeclareLocal(t);
        scopes[^1][name] = lb;
        return lb;
    }

    LocalBuilder? FindLocal(string name)
    {
        for (int i = scopes.Count - 1; i >= 0; i--) if (scopes[i].TryGetValue(name, out var lb)) return lb;
        return null;
    }

    void Decl(MDecl d)
    {
        if (d.Init != null) d.Init = Lower(d.Init);
        if (opt.StackArrays && d.Init is MCall { Name: "Rent", Target: MCode tc, Args: [MCode len] } && r.TryType(tc.Text) is { IsGenericType: true } ot
            && ot.GetGenericTypeDefinition() == typeof(Alice.Runtime.Own<>) && TryLiteral(len.Text, out var lt, out var lv) && lt == typeof(int))
        {
            var elem = ot.GetGenericArguments()[0];
            var p = Declare(d.Name, elem.MakePointerType());
            int n = (int)lv!;
            il.Ldc(n);
            il.Emit(OpCodes.Conv_U);
            il.Emit(OpCodes.Sizeof, elem);
            il.Emit(OpCodes.Mul_Ovf_Un);
            il.Emit(OpCodes.Localloc);
            il.Emit(OpCodes.Stloc, p);
            if (opt.StackZero)
            {
                il.Emit(OpCodes.Ldloc, p);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Ldc(n);
                il.Emit(OpCodes.Sizeof, elem);
                il.Emit(OpCodes.Mul);
                il.Emit(OpCodes.Initblk);
            }
            stackArrays[p] = (elem, n);
            return;
        }
        Type t = d.TypeCs != null ? r.Type(d.TypeCs) : d.Init != null ? Infer(d.Init) : throw new SpikeUnsupported("MDecl:untyped");
        if (d.Init is null or MCode { Text: "default!" or "default" })
        {
            var lb0 = Declare(d.Name, t);
            il.Emit(OpCodes.Ldloca, lb0);
            il.Emit(OpCodes.Initobj, t);
            return;
        }
        Expr(d.Init, t);
        var lb = Declare(d.Name, t);
        il.Emit(OpCodes.Stloc, lb);
    }

    void For(MFor f)
    {
        scopes.Add(new());
        Type t = f.VarType != null ? r.Type(f.VarType) : Infer(f.Start);
        Expr(f.Start, t);
        var v = Declare(f.Var, t);
        il.Emit(OpCodes.Stloc, v);
        var body = il.DefineLabel();
        var cont = il.DefineLabel();
        var cond = il.DefineLabel();
        var brk = il.DefineLabel();
        Br(OpCodes.Br, cond);
        Mark(body);
        loops.Push((brk, cont));
        Stmts(f.Body);
        loops.Pop();
        Mark(cont);
        var name = new MCode(f.Var);
        switch (f.Step.Replace(" ", ""))
        {
            case "++": Assign(name, "+=", new MCode("1")); break;
            case "--": Assign(name, "-=", new MCode("1")); break;
            case var st when st.Length > 2 && st[1] == '=' && st[0] is '+' or '-': Assign(name, st[..2], new MCode(st[2..])); break;
            default: throw new SpikeUnsupported("MFor:step", f.Step);
        }
        Mark(cond);
        var cmp = new MBinary { Op = AirOps.ParseBin(f.Cmp), L = name, R = f.End };
        Jump(cmp, true, body);
        Mark(brk);
        scopes.RemoveAt(scopes.Count - 1);
    }

    void Deconstruct(MDeconstruct dc)
    {
        if (dc.Value is MTuple tup && tup.Items.Count == dc.Names.Count)
        {
            var temps = new List<LocalBuilder>();
            foreach (var item in tup.Items)
            {
                var t = dc.Declare ? Infer(item) : TypeOfName(dc.Names[temps.Count]);
                Expr(item, t);
                var tmp = il.DeclareLocal(t);
                il.Emit(OpCodes.Stloc, tmp);
                temps.Add(tmp);
            }
            for (int i = 0; i < temps.Count; i++)
            {
                if (dc.Names[i] == "_") continue;
                var target = dc.Declare ? Declare(dc.Names[i], temps[i].LocalType) : null;
                il.Emit(OpCodes.Ldloc, temps[i]);
                if (target != null) il.Emit(OpCodes.Stloc, target);
                else Store(dc.Names[i]);
            }
            return;
        }
        var vt = Infer(dc.Value);
        if (!vt.IsGenericType || !vt.FullName!.StartsWith("System.ValueTuple`")) throw new SpikeUnsupported("MDeconstruct:non-tuple");
        Expr(dc.Value, vt);
        var all = il.DeclareLocal(vt);
        il.Emit(OpCodes.Stloc, all);
        var ts = vt.GetGenericArguments();
        for (int i = 0; i < dc.Names.Count; i++)
        {
            if (dc.Names[i] == "_") continue;
            il.Emit(OpCodes.Ldloca, all);
            il.Emit(OpCodes.Ldfld, vt.GetField("Item" + (i + 1))!);
            if (dc.Declare) il.Emit(OpCodes.Stloc, Declare(dc.Names[i], ts[i]));
            else Store(dc.Names[i]);
        }
    }

    Type TypeOfName(string name) =>
        stacked.TryGetValue(name, out var st) ? st : FindLocal(name)?.LocalType ?? (args.TryGetValue(name, out var a) ? a.Type : throw new SpikeUnsupported("MCode:unknown-name", name));

    void Store(string name)
    {
        if (FindLocal(name) is { } lb) { il.Emit(OpCodes.Stloc, lb); return; }
        if (args.TryGetValue(name, out var a)) { il.Starg(a.Index); return; }
        throw new SpikeUnsupported("MAssign:unknown-target", name);
    }

    void ExprStmt(MExpr e)
    {
        if (e is MUnary { Op: AirUnOp.Inc or AirUnOp.Dec } u)
        {
            Assign(u.E, u.Op == AirUnOp.Inc ? "+=" : "-=", new MCode("1"));
            return;
        }
        e = Lower(e);
        if (e is MCall { Name: "Release", Args: [MCode rel] } && FindLocal(rel.Text) is { } rl && stackArrays.ContainsKey(rl)) return;   // (c) stack buffer: nothing to return
        var t = Expr(e, null);
        if (t != typeof(void)) il.Emit(OpCodes.Pop);
    }

    static AirBinOp CompoundOp(string op) => op switch
    {
        "+=" => AirBinOp.Add, "-=" => AirBinOp.Sub, "*=" => AirBinOp.Mul, "/=" => AirBinOp.Div, "%=" => AirBinOp.Mod,
        "&=" => AirBinOp.BitAnd, "|=" => AirBinOp.BitOr, "^=" => AirBinOp.BitXor, "<<=" => AirBinOp.Shl, ">>=" => AirBinOp.Shr, ">>>=" => AirBinOp.UShr,
        _ => throw new SpikeUnsupported("MAssign:op", op),
    };

    void Assign(MExpr target, string op, MExpr value)
    {
        bool compound = op != "=";
        switch (target)
        {
            case MCode { Text: var name } when FindLocal(name) != null || args.ContainsKey(name):
                {
                    var t = TypeOfName(name);
                    if (compound) CompoundValue(target, t, op, value); else Expr(value, t);
                    Store(name);
                    return;
                }
            case MIndex { Target: var arrE, Args: [var idx] }:
                {
                    if (arrE is MCode ac && FindLocal(ac.Text) is { } pl && stackArrays.TryGetValue(pl, out var sa))
                    {
                        PtrElem(pl, sa.Elem, idx);
                        if (compound) { il.Emit(OpCodes.Dup); il.Ldobj(sa.Elem); BinTail(sa.Elem, op, value); }
                        else Expr(value, sa.Elem);
                        il.Stobj(sa.Elem);
                        return;
                    }
                    var at = Infer(arrE);
                    if (!at.IsArray) throw new SpikeUnsupported("MAssign:indexer", at.Name);
                    var et = at.GetElementType()!;
                    if (NoBounds || compound)
                    {
                        ElemAddr(arrE, at, et, idx, NoBounds);
                        if (compound) { il.Emit(OpCodes.Dup); il.Ldobj(et); BinTail(et, op, value); }
                        else Expr(value, et);
                        il.Stobj(et);
                        return;
                    }
                    Expr(arrE, at);
                    Expr(idx, typeof(int));
                    Expr(value, et);
                    il.Stelem(et);
                    return;
                }
            case MMember { Target: var objE, Name: var field }:
                {
                    var (fi, isStatic) = FieldOf(objE, field);
                    if (fi == null) throw new SpikeUnsupported("MAssign:property", field);
                    if (isStatic)
                    {
                        if (compound) CompoundValue(target, fi.FieldType, op, value); else Expr(value, fi.FieldType);
                        il.Emit(OpCodes.Stsfld, fi);
                        return;
                    }
                    Address(objE);
                    if (compound) { il.Emit(OpCodes.Dup); il.Emit(OpCodes.Ldfld, fi); BinTail(fi.FieldType, op, value); }
                    else Expr(value, fi.FieldType);
                    il.Emit(OpCodes.Stfld, fi);
                    return;
                }
        }
        throw new SpikeUnsupported("MAssign:target:" + target.GetType().Name, Printer.E(target));
    }

    /// <summary>`x op= v` with the current value already loadable by evaluating <paramref name="target"/>.</summary>
    void CompoundValue(MExpr target, Type t, string op, MExpr value)
    {
        Expr(target, t);
        BinTail(t, op, value);
    }

    /// <summary>Current value of type <paramref name="t"/> is on the stack: apply `op` with <paramref name="value"/>, convert back to t.</summary>
    void BinTail(Type t, string op, MExpr value)
    {
        var bop = CompoundOp(op);
        if (bop is AirBinOp.Shl or AirBinOp.Shr or AirBinOp.UShr)
        {
            var wt = Widen(t);
            Convert(t, wt, false);
            ShiftCount(value, wt);
            il.Emit(bop == AirBinOp.Shl ? OpCodes.Shl : bop == AirBinOp.UShr || IsUnsigned(wt) ? OpCodes.Shr_Un : OpCodes.Shr);
            Convert(wt, t, false);
            return;
        }
        Literal(value, out var lit);
        var pt = Promote(t, Infer(value), null, lit);
        Convert(t, pt, false);
        Expr(value, pt);
        Arith(bop, pt, Checked);
        Convert(pt, t, Checked);   // C# compound assignment: (T)(x op v), checked in a checked context
    }

    // ---------------- expressions ----------------

    /// <summary>Emits <paramref name="e"/>, converted to <paramref name="want"/> when given; returns the pushed type.</summary>
    Type Expr(MExpr e, Type? want)
    {
        if (want != null && Literal(e, out var lit) && lit != null && IsNumeric(want) && LiteralFits(lit, want))
        {
            Const(want, lit);
            return want;
        }
        if (want != null && e is MCode { Text: "null" } && !want.IsValueType) { il.Emit(OpCodes.Ldnull); return want; }
        if (want != null && e is MCond c) { Cond(c, want); return want; }
        var t = Raw(e);
        if (want != null && want != t) Convert(t, want, false, implicitOnly: true);
        return want ?? t;
    }

    bool Literal(MExpr e, out object? v)
    {
        v = null;
        if (e is MCode c && FindLocalOrArg(c.Text) == false && TryLiteral(c.Text, out _, out v)) return true;
        if (e is MUnary { Op: AirUnOp.Neg, E: MCode c2 } && TryLiteral("-" + c2.Text, out var t2, out v) && IsNumeric(t2)) return true;
        v = null;
        return false;
    }

    bool FindLocalOrArg(string name) => FindLocal(name) != null || args.ContainsKey(name) || stacked.ContainsKey(name);

    void Const(Type t, object v)
    {
        var big = v switch { int i => (decimal)i, uint u => u, long l => l, ulong ul => ul, float f => (decimal)f, double d => (decimal)d, _ => 0m };
        if (t == typeof(float)) { il.Emit(OpCodes.Ldc_R4, v is float f ? f : (float)big); return; }
        if (t == typeof(double)) { il.Emit(OpCodes.Ldc_R8, v is double d ? d : v is float f2 ? f2 : (double)big); return; }
        if (Is64(t)) { il.Emit(OpCodes.Ldc_I8, t == typeof(ulong) ? unchecked((long)(ulong)big) : (long)big); return; }
        long x = (long)big;
        il.Ldc(unchecked((int)x));
    }

    Type Raw(MExpr e)
    {
        e = Lower(e);
        switch (e)
        {
            case MCode c when Leaf(c) is { } lt: return lt;
            case MCode c: return Code(c.Text);
            case MBinary b: return Binary(b);
            case MUnary u: return Unary(u);
            case MCond c: { var t = Infer(c); Cond(c, t); return t; }
            case MCast c: return Cast(c);
            case MCall c: return Call(c, tail: false);
            case MMember m: return Member(m);
            case MIndex ix: return Index(ix);
            case MNewArray na: return NewArray(na);
            case MNew n: return New(n);
            case MTuple t:
                {
                    var tt = Infer(t);
                    var items = tt.GetGenericArguments();
                    for (int i = 0; i < t.Items.Count; i++) Expr(t.Items[i], items[i]);
                    il.Emit(OpCodes.Newobj, tt.GetConstructor(items)!);
                    return tt;
                }
        }
        throw new SpikeUnsupported(e.GetType().Name);
    }

    /// <summary>
    /// Typed AIR nodes (D0.4-D0.7) as the spike's core forms: ownership and loop-shape nodes become the call /
    /// member / arithmetic they print as. Read from their fields, not from their C# spelling.
    /// </summary>
    MExpr Lower(MExpr e) => e switch
    {
        AirRent rn => new MCall { Target = new AirTypeRef(new AirType.ClrRef(rn.PoolCs, null, [])), Name = "Rent", Args = rn.Args },
        AirRelease rl => new MCall { Target = new AirTypeRef(new AirType.ClrRef(rl.PoolCs, null, [])), Name = "Release", Args = [rl.Target] },
        AirLength ln => new MMember { Target = ln.Target, Name = ln.Member },
        AirArrayLength al => new MMember { Target = new AirLocalRef(al.Array), Name = "Length" },
        AirTripCount tc => tc.Inclusive ? new MBinary { Op = AirBinOp.Add, L = new MBinary { Op = AirBinOp.Sub, L = tc.End, R = tc.Start }, R = AirConst.Int(1) }
                                        : new MBinary { Op = AirBinOp.Sub, L = tc.End, R = tc.Start },
        AirOffset of => new MBinary { Op = AirBinOp.Add, L = of.Base, R = of.Offset },
        _ => e,
    };

    /// <summary>Typed leaves the spike reads by their fields; null for leaves handled by name/literal (AirLocalRef,
    /// AirParamRef and AirConst carry the same name/literal in their fields as in Text).</summary>
    Type? Leaf(MCode c)
    {
        switch (c)
        {
            case AirLocalRef { Narrowed: true }: throw new SpikeUnsupported("AirLocalRef:narrowed");
            // unresolved by the binder (ADR 0032 says reject); Main's ARGV arrives this way, so names of parameters/locals and
            // CLR static paths are still accepted, anything else is reported as AirName
            case AirName n: return FindLocalOrArg(n.Path) || StaticPath(n.Path) != null ? Code(n.Path) : throw new SpikeUnsupported("AirName", n.Path);
            case AirRawCs: throw new SpikeUnsupported("AirRawCs", c.Text);
            case AirThis or AirBase: throw new SpikeUnsupported("AirThis");
            case AirFieldRef f: throw new SpikeUnsupported("AirFieldRef", f.Name);
            case AirNull: il.Emit(OpCodes.Ldnull); return typeof(object);
            case AirDefault { Type: { } dt }:
                {
                    var t = AirClr(dt);
                    var tmp = il.DeclareLocal(t);
                    il.Emit(OpCodes.Ldloca, tmp);
                    il.Emit(OpCodes.Initobj, t);
                    il.Emit(OpCodes.Ldloc, tmp);
                    return t;
                }
            case AirSizeOf so: il.Emit(OpCodes.Sizeof, AirClr(so.Of)); return typeof(int);
            case AirLocalRef lr: return Code(lr.Name);
            case AirParamRef pr: return Code(pr.Name);
            case AirConst k: return Code(k.Kind == AirConstKind.String ? "\"" + k.Literal + "\"" : k.Literal);
        }
        return null;
    }

    Type? LeafType(MCode c) => c switch
    {
        AirLocalRef { Narrowed: false } lr => TypeOfName(lr.Name),
        AirParamRef pr => TypeOfName(pr.Name),
        AirDefault { Type: { } dt } => AirClr(dt),
        AirSizeOf => typeof(int),
        AirNull => typeof(object),
        _ => null,
    };

    /// <summary>AirType -> CLR type: the reflection type when the AirType carries one, else its C# spelling.</summary>
    Type AirClr(AirType t) => t is AirType.ClrRef { Clr: { IsGenericTypeDefinition: false } clr, Nullable: false } ? clr
        : t is AirType.Prim { Kind: var k, Nullable: false } && k != PrimKind.Null ? r.Type(t.ToCs()) : r.Type(t.ToCs());

    Type Code(string text)
    {
        if (stacked.Remove(text, out var onStack)) return onStack;   // already on the evaluation stack (stack scheduling)
        if (FindLocal(text) is { } lb)
        {
            if (stackArrays.ContainsKey(lb)) throw new SpikeUnsupported("StackArray:escapes", text);
            il.Emit(OpCodes.Ldloc, lb);
            return lb.LocalType;
        }
        if (args.TryGetValue(text, out var a)) { il.Ldarg(a.Index); return a.Type; }
        if (TryLiteral(text, out var t, out var v))
        {
            if (t == typeof(bool)) il.Emit((bool)v! ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
            else if (t == typeof(string)) il.Emit(OpCodes.Ldstr, (string)v!);
            else if (t == typeof(char)) il.Ldc((int)(char)v!);
            else if (v == null) il.Emit(OpCodes.Ldnull);
            else Const(t, v);
            return t;
        }
        // a static member path: `global::System.Math.PI`, `int.MaxValue`
        if (StaticPath(text) is { } sp) return LoadStatic(sp);
        throw new SpikeUnsupported("MCode:other", text);
    }

    MemberInfo? StaticPath(string text)
    {
        int dot = text.LastIndexOf('.');
        if (dot <= 0 || text.Contains('(')) return null;
        var owner = r.TryType(text[..dot]);
        if (owner == null) return null;
        string name = text[(dot + 1)..];
        return (MemberInfo?)owner.GetField(name, BindingFlags.Public | BindingFlags.Static) ?? owner.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
    }

    Type LoadStatic(MemberInfo m)
    {
        if (m is FieldInfo f)
        {
            if (f.IsLiteral) { var v = f.GetRawConstantValue()!; if (v is bool bb) il.Emit(bb ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0); else Const(f.FieldType, v); return f.FieldType; }
            il.Emit(OpCodes.Ldsfld, f);
            return f.FieldType;
        }
        var p = (PropertyInfo)m;
        il.Emit(OpCodes.Call, p.GetGetMethod()!);
        return p.PropertyType;
    }

    void Cond(MCond c, Type t)
    {
        // `c ? 1 : 0` is the bool itself (Roslyn emits the comparison value, no branch: unpredictable data stays branch-free)
        if (IsIntegral(t) && Literal(c.A, out var a1) && Literal(c.B, out var b0) && a1 != null && b0 != null
            && System.Convert.ToDecimal(a1) is var av && System.Convert.ToDecimal(b0) is var bv && (av, bv) is (1m, 0m) or (0m, 1m))
        {
            Expr(c.C, typeof(bool));
            if (av == 0m) { il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq); }
            if (Is64(t)) il.Emit(OpCodes.Conv_U8);
            return;
        }
        var els = il.DefineLabel();
        var end = il.DefineLabel();
        // Roslyn's shape (false arm first, then the true arm at the branch target): RyuJIT if-converts it to setcc/cmov
        Jump(c.C, true, els);
        Expr(c.B, t);
        Br(OpCodes.Br, end);
        Mark(els);
        Expr(c.A, t);
        Mark(end);
    }

    Type Binary(MBinary b)
    {
        bool chk = b.Checked && Checked;
        if (b.Op is AirBinOp.And or AirBinOp.Or)
        {
            var shortcut = il.DefineLabel();
            var end = il.DefineLabel();
            Expr(b.L, typeof(bool));
            Br(b.Op == AirBinOp.And ? OpCodes.Brfalse : OpCodes.Brtrue, shortcut);
            Expr(b.R, typeof(bool));
            Br(OpCodes.Br, end);
            Mark(shortcut);
            il.Emit(b.Op == AirBinOp.And ? OpCodes.Ldc_I4_0 : OpCodes.Ldc_I4_1);
            Mark(end);
            return typeof(bool);
        }
        if (b.Op == AirBinOp.Coalesce) throw new SpikeUnsupported("MBinary:Coalesce");
        var lt = Infer(b.L);
        var rt = Infer(b.R);
        if (b.Op is AirBinOp.Shl or AirBinOp.Shr or AirBinOp.UShr)
        {
            var wt = Widen(lt);
            Expr(b.L, wt);
            ShiftCount(b.R, wt);
            il.Emit(b.Op == AirBinOp.Shl ? OpCodes.Shl : b.Op == AirBinOp.UShr || IsUnsigned(wt) ? OpCodes.Shr_Un : OpCodes.Shr);
            return wt;
        }
        if (b.Op == AirBinOp.Add && (lt == typeof(string) || rt == typeof(string)))
        {
            Expr(b.L, lt);
            if (lt.IsValueType) il.Emit(OpCodes.Box, lt);
            Expr(b.R, rt);
            if (rt.IsValueType) il.Emit(OpCodes.Box, rt);
            il.Emit(OpCodes.Call, typeof(string).GetMethod("Concat", [typeof(object), typeof(object)])!);
            return typeof(string);
        }
        Literal(b.L, out var ll);
        Literal(b.R, out var rl);
        Type pt;
        if (IsNumeric(lt) && IsNumeric(rt) || ll != null && IsNumeric(rt) || rl != null && IsNumeric(lt)) pt = Promote(lt, rt, ll, rl);
        else if (lt == rt && (lt == typeof(bool) || !lt.IsValueType || lt.IsEnum)) pt = lt;
        else throw new SpikeUnsupported("MBinary:operand-types", $"{lt.Name} {b.Op} {rt.Name}");
        if (pt == typeof(string) && b.Op is AirBinOp.Eq or AirBinOp.Ne)
        {
            Expr(b.L, pt);
            Expr(b.R, pt);
            il.Emit(OpCodes.Call, typeof(string).GetMethod("op_Equality", [typeof(string), typeof(string)])!);
            if (b.Op == AirBinOp.Ne) { il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq); }
            return typeof(bool);
        }
        Expr(b.L, pt);
        Expr(b.R, pt);
        if (Compare(b.Op, pt)) return typeof(bool);
        Arith(b.Op, pt, chk);
        return pt;
    }

    /// <summary>The shift count as Roslyn emits it: masked to the operand width (IL leaves larger counts undefined).</summary>
    void ShiftCount(MExpr count, Type operand)
    {
        int mask = Is64(operand) ? 63 : 31;
        if (Literal(count, out var v) && v is int n) { il.Ldc(n & mask); return; }
        Expr(count, typeof(int));
        il.Ldc(mask);
        il.Emit(OpCodes.And);
    }

    bool Compare(AirBinOp op, Type t)
    {
        bool un = IsUnsigned(t), fl = IsFloat(t);
        switch (op)
        {
            case AirBinOp.Eq: il.Emit(OpCodes.Ceq); return true;
            case AirBinOp.Ne: il.Emit(OpCodes.Ceq); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq); return true;
            case AirBinOp.Lt: il.Emit(un ? OpCodes.Clt_Un : OpCodes.Clt); return true;
            case AirBinOp.Gt: il.Emit(un ? OpCodes.Cgt_Un : OpCodes.Cgt); return true;
            // a >= b is !(a < b), where for floats "<" must be unordered so NaN compares false
            case AirBinOp.Ge: il.Emit(un || fl ? OpCodes.Clt_Un : OpCodes.Clt); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq); return true;
            case AirBinOp.Le: il.Emit(un || fl ? OpCodes.Cgt_Un : OpCodes.Cgt); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq); return true;
        }
        return false;
    }

    void Arith(AirBinOp op, Type t, bool chk)
    {
        bool un = IsUnsigned(t), integral = IsIntegral(t);
        chk &= integral;
        il.Emit(op switch
        {
            AirBinOp.Add => chk ? (un ? OpCodes.Add_Ovf_Un : OpCodes.Add_Ovf) : OpCodes.Add,
            AirBinOp.Sub => chk ? (un ? OpCodes.Sub_Ovf_Un : OpCodes.Sub_Ovf) : OpCodes.Sub,
            AirBinOp.Mul => chk ? (un ? OpCodes.Mul_Ovf_Un : OpCodes.Mul_Ovf) : OpCodes.Mul,
            AirBinOp.Div => un ? OpCodes.Div_Un : OpCodes.Div,
            AirBinOp.Mod => un ? OpCodes.Rem_Un : OpCodes.Rem,
            AirBinOp.BitAnd => OpCodes.And,
            AirBinOp.BitOr => OpCodes.Or,
            AirBinOp.BitXor => OpCodes.Xor,
            _ => throw new SpikeUnsupported("MBinary:" + op),
        });
    }

    Type Unary(MUnary u)
    {
        switch (u.Op)
        {
            case AirUnOp.NotNull: return Raw(u.E);
            case AirUnOp.Not: Expr(u.E, typeof(bool)); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Ceq); return typeof(bool);
            case AirUnOp.BitNot: { var t = Widen(Infer(u.E)); Expr(u.E, t); il.Emit(OpCodes.Not); return t; }
            case AirUnOp.Neg:
                {
                    if (Literal(u, out var lit) && lit != null) { TryLiteral("-" + ((MCode)u.E).Text, out var lt, out _); Const(lt, lit); return lt; }
                    var t = Widen(Infer(u.E));
                    if (t == typeof(uint)) t = typeof(long);
                    if (u.Checked && Checked && IsIntegral(t)) { Const(t, 0); Expr(u.E, t); il.Emit(OpCodes.Sub_Ovf); }
                    else { Expr(u.E, t); il.Emit(OpCodes.Neg); }
                    return t;
                }
            case AirUnOp.Inc or AirUnOp.Dec when u.E is MCode { Text: var name } && FindLocalOrArg(name):
                {
                    var t = TypeOfName(name);
                    if (u.Postfix) Code(name);
                    Assign(u.E, u.Op == AirUnOp.Inc ? "+=" : "-=", new MCode("1"));
                    if (!u.Postfix) Code(name);
                    return t;
                }
        }
        throw new SpikeUnsupported("MUnary:" + u.Op);
    }

    Type Cast(MCast c)
    {
        var to = r.Type(c.TypeCs);
        if (Literal(c.E, out var lit) && lit != null && IsNumeric(to))
        {
            // Roslyn folds a cast of a constant; out-of-range constants are compile errors outside unchecked
            object v = to == typeof(float) ? System.Convert.ToSingle(lit) : to == typeof(double) ? System.Convert.ToDouble(lit) : lit;
            if (IsFloat(to) || LiteralFits(lit, to)) { Const(to, v); return to; }
        }
        var from = Raw(c.E);
        Convert(from, to, Checked);
        return to;
    }

    /// <summary>Numeric/boxing conversion from -> to; explicit numeric conversions are overflow-checked when <paramref name="chk"/>.</summary>
    void Convert(Type from, Type to, bool chk, bool implicitOnly = false)
    {
        if (from == to) return;
        if (IsNumeric(from) && IsNumeric(to))
        {
            if (implicitOnly && !ImplicitNumeric(from, to)) throw new SpikeUnsupported("Conversion:implicit", $"{from.Name}->{to.Name}");
            bool un = IsUnsigned(from) && !IsFloat(from);
            if (to == typeof(float)) { if (un) il.Emit(OpCodes.Conv_R_Un); il.Emit(OpCodes.Conv_R4); return; }
            if (to == typeof(double)) { if (un) il.Emit(OpCodes.Conv_R_Un); il.Emit(OpCodes.Conv_R8); return; }
            // widening between integers that never overflows needs no check
            bool widening = ImplicitNumeric(from, to);
            if (widening)
            {
                if (Is64(to) && !Is64(from)) il.Emit(un ? OpCodes.Conv_U8 : OpCodes.Conv_I8);
                return;   // 32-bit and smaller share the int32 stack type
            }
            bool fl = IsFloat(from);
            OpCode op = (to, chk) switch
            {
                (_, false) when to == typeof(int) => OpCodes.Conv_I4,
                (_, false) when to == typeof(uint) => OpCodes.Conv_U4,
                (_, false) when to == typeof(long) => fl ? OpCodes.Conv_I8 : un ? OpCodes.Conv_U8 : OpCodes.Conv_I8,
                (_, false) when to == typeof(ulong) => fl ? OpCodes.Conv_U8 : un ? OpCodes.Conv_U8 : OpCodes.Conv_I8,
                (_, false) when to == typeof(short) => OpCodes.Conv_I2,
                (_, false) when to == typeof(ushort) || to == typeof(char) => OpCodes.Conv_U2,
                (_, false) when to == typeof(byte) => OpCodes.Conv_U1,
                (_, false) when to == typeof(sbyte) => OpCodes.Conv_I1,
                _ when to == typeof(int) => un ? OpCodes.Conv_Ovf_I4_Un : OpCodes.Conv_Ovf_I4,
                _ when to == typeof(uint) => un ? OpCodes.Conv_Ovf_U4_Un : OpCodes.Conv_Ovf_U4,
                _ when to == typeof(long) => un ? OpCodes.Conv_Ovf_I8_Un : OpCodes.Conv_Ovf_I8,
                _ when to == typeof(ulong) => un ? OpCodes.Conv_Ovf_U8_Un : OpCodes.Conv_Ovf_U8,
                _ when to == typeof(short) => un ? OpCodes.Conv_Ovf_I2_Un : OpCodes.Conv_Ovf_I2,
                _ when to == typeof(ushort) || to == typeof(char) => un ? OpCodes.Conv_Ovf_U2_Un : OpCodes.Conv_Ovf_U2,
                _ when to == typeof(byte) => un ? OpCodes.Conv_Ovf_U1_Un : OpCodes.Conv_Ovf_U1,
                _ when to == typeof(sbyte) => un ? OpCodes.Conv_Ovf_I1_Un : OpCodes.Conv_Ovf_I1,
                _ => throw new SpikeUnsupported("Conversion", $"{from.Name}->{to.Name}"),
            };
            il.Emit(op);
            return;
        }
        if (from.IsValueType && !to.IsValueType && to.IsAssignableFrom(from)) { il.Emit(OpCodes.Box, from); return; }
        if (!from.IsValueType && !to.IsValueType && (to.IsAssignableFrom(from) || from == typeof(object) && implicitOnly)) return;
        if (!implicitOnly && !from.IsValueType && !to.IsValueType) { il.Emit(OpCodes.Castclass, to); return; }
        if (!implicitOnly && from == typeof(object) && to.IsValueType) { il.Emit(OpCodes.Unbox_Any, to); return; }
        throw new SpikeUnsupported("Conversion", $"{from.Name}->{to.Name}");
    }

    // ---------------- calls / members ----------------

    bool IsAliceCall(MCall c) => c.Target == null && methods.TryGetValue(c.Name, out var l) && l.Count == 1 && self.Ret == l[0].Ret;

    bool IsSelfCall(MCall c) => c.Target == null && c.Name == self.Name && methods.TryGetValue(c.Name, out var l) && l.Count == 1 && self.Ret == l[0].Ret;

    sealed record Resolved(MethodBase? Clr, SpikeMethod? Alice, MExpr? Receiver, bool Extension, Type[] ParamTypes, Type Ret);

    Resolved Resolve(MCall c)
    {
        var argTypes = c.Args.Select(Infer).ToList();
        var lits = c.Args.Select(a => Literal(a, out var v) ? v : null).ToList();
        if (c.Target == null)
        {
            if (methods.TryGetValue(c.Name, out var mine))
            {
                var m = mine.FirstOrDefault(x => x.Params.Count == c.Args.Count && x.Params.Select((p, i) => p.Type == argTypes[i] || lits[i] != null && LiteralFits(lits[i], p.Type) || ImplicitNumeric(argTypes[i], p.Type) || p.Type == typeof(object)).All(b => b))
                    ?? throw new SpikeUnsupported("MCall:alice-overload", c.Name);
                return new(null, m, null, false, m.Params.Select(p => p.Type).ToArray(), m.Ret);
            }
            var pm = Pick(PreludeNamed(c.Name), argTypes, lits);
            if (pm != null) return Clr(pm, null, false);
            throw new SpikeUnsupported("MCall:unresolved-free", c.Name);
        }
        if (c.Target is MCode tc && !FindLocalOrArg(tc.Text) && !TryLiteral(tc.Text, out _, out _) && r.TryType(tc.Text) is { } st)
        {
            var sm = Pick(st.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => m.Name == c.Name), argTypes, lits)
                ?? throw new SpikeUnsupported("MCall:unresolved-static", tc.Text + "." + c.Name);
            return Clr(sm, null, false);
        }
        var rt = Infer(c.Target);
        if (r.AliceFields.ContainsKey(rt)) throw new SpikeUnsupported("MCall:alice-instance-method", c.Name);
        var inst = Pick(rt.GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.Name == c.Name), argTypes, lits);
        if (inst != null) return Clr(inst, c.Target, false);
        var ext = Pick(ExtensionsNamed(c.Name), argTypes.Prepend(rt).ToList(), lits.Prepend(null).ToList());
        if (ext != null) return Clr(ext, c.Target, true);
        throw new SpikeUnsupported("MCall:unresolved-instance", rt.Name + "." + c.Name);
    }

    static Resolved Clr(MethodBase m, MExpr? recv, bool ext) =>
        new(m, null, recv, ext, m.GetParameters().Select(p => p.ParameterType).ToArray(), m is MethodInfo mi ? mi.ReturnType : typeof(void));

    Type Call(MCall c, bool tail)
    {
        if (c.Target == null && c.Name == "unchecked" && c.Args.Count == 1)
        {
            uncheckedDepth++;
            var t = Raw(c.Args[0]);
            uncheckedDepth--;
            return t;
        }
        if (c.Safe) throw new SpikeUnsupported("MCall:null-safe");
        if (c.Args.Any(a => a is MArg)) throw new SpikeUnsupported("MArg");
        var rs = Resolve(c);
        int k = 0;
        if (rs.Receiver != null)
        {
            if (rs.Extension) { Expr(rs.Receiver, rs.ParamTypes[0]); k = 1; }
            else if (Infer(rs.Receiver).IsValueType) Address(rs.Receiver);
            else Expr(rs.Receiver, null);
        }
        for (int i = 0; i < c.Args.Count; i++) Expr(c.Args[i], rs.ParamTypes[i + k]);
        if (tail) il.Emit(OpCodes.Tailcall);
        if (rs.Alice != null) il.Emit(OpCodes.Call, rs.Alice.Builder);
        else if (rs.Clr is ConstructorInfo) throw new SpikeUnsupported("MCall:ctor");
        else
        {
            var mi = (MethodInfo)rs.Clr!;
            il.Emit(!mi.IsStatic && !mi.DeclaringType!.IsValueType && mi.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, mi);
        }
        return rs.Ret;
    }

    /// <summary>Pushes the address of a value-type expression (locals/args by address, others via a temp).</summary>
    void Address(MExpr e)
    {
        if (e is MCode c && FindLocal(c.Text) is { } lb) { il.Emit(OpCodes.Ldloca, lb); return; }
        if (e is MCode c2 && args.TryGetValue(c2.Text, out var a)) { il.Ldarga(a.Index); return; }
        var t = Raw(e);
        var tmp = il.DeclareLocal(t);
        il.Emit(OpCodes.Stloc, tmp);
        il.Emit(OpCodes.Ldloca, tmp);
    }

    (FieldInfo? Field, bool Static) FieldOf(MExpr obj, string name)
    {
        if (obj is MCode tc && !FindLocalOrArg(tc.Text) && r.TryType(tc.Text) is { } st)
            return (st.GetField(name, BindingFlags.Public | BindingFlags.Static), true);
        var t = Infer(obj);
        if (r.AliceFields.TryGetValue(t, out var fs) && fs.TryGetValue(name, out var f)) return (f, false);
        if (t is not TypeBuilder && t.GetField(name, BindingFlags.Public | BindingFlags.Instance) is { } cf) return (cf, false);
        return (null, false);
    }

    Type Member(MMember m)
    {
        if (m.Safe) throw new SpikeUnsupported("MMember:null-safe");
        if (m.Target is MCode tc && !FindLocalOrArg(tc.Text) && r.TryType(tc.Text) is { } st)
        {
            var sm = (MemberInfo?)st.GetField(m.Name, BindingFlags.Public | BindingFlags.Static) ?? st.GetProperty(m.Name, BindingFlags.Public | BindingFlags.Static)
                ?? throw new SpikeUnsupported("MMember:unresolved-static", tc.Text + "." + m.Name);
            return LoadStatic(sm);
        }
        if (m.Target is MCode pc && m.Name == "Length" && FindLocal(pc.Text) is { } pl && stackArrays.TryGetValue(pl, out var sa))
        {
            il.Ldc(sa.Length);
            return typeof(int);
        }
        var t = Infer(m.Target);
        if (t.IsArray && m.Name == "Length")
        {
            Expr(m.Target, t);
            il.Emit(OpCodes.Ldlen);
            il.Emit(OpCodes.Conv_I4);
            return typeof(int);
        }
        var (f, _) = FieldOf(m.Target, m.Name);
        if (f != null)
        {
            if (t.IsValueType && m.Target is MCode) Address(m.Target); else Expr(m.Target, t);
            il.Emit(OpCodes.Ldfld, f);
            return f.FieldType;
        }
        if (t is TypeBuilder) throw new SpikeUnsupported("MMember:alice-property", m.Name);
        var p = t.GetProperty(m.Name, BindingFlags.Public | BindingFlags.Instance) ?? throw new SpikeUnsupported("MMember:unresolved", t.Name + "." + m.Name);
        var g = p.GetGetMethod()!;
        if (t.IsValueType) { Address(m.Target); il.Emit(OpCodes.Call, g); }
        else { Expr(m.Target, t); il.Emit(g.IsVirtual ? OpCodes.Callvirt : OpCodes.Call, g); }
        return p.PropertyType;
    }

    void PtrElem(LocalBuilder p, Type elem, MExpr idx)
    {
        il.Emit(OpCodes.Ldloc, p);
        Expr(idx, typeof(int));
        il.Emit(OpCodes.Conv_I);
        il.Emit(OpCodes.Sizeof, elem);
        il.Emit(OpCodes.Mul);
        il.Emit(OpCodes.Add);
    }

    static readonly MethodInfo ArrayData = typeof(MemoryMarshal).GetMethods().Single(m => m.Name == "GetArrayDataReference" && m.IsGenericMethodDefinition && m.GetParameters()[0].ParameterType.IsArray);

    /// <summary>Address of arr[idx]: `ldelema` (bounds-checked) or, when proven in bounds, data reference + idx * sizeof.</summary>
    void ElemAddr(MExpr arrE, Type at, Type et, MExpr idx, bool unchecked_)
    {
        Expr(arrE, at);
        if (unchecked_)
        {
            il.Emit(OpCodes.Call, ArrayData.MakeGenericMethod(et));
            Expr(idx, typeof(int));
            il.Emit(OpCodes.Conv_I);
            il.Emit(OpCodes.Sizeof, et);
            il.Emit(OpCodes.Mul);
            il.Emit(OpCodes.Add);
            return;
        }
        Expr(idx, typeof(int));
        il.Emit(OpCodes.Ldelema, et);
    }

    Type Index(MIndex ix)
    {
        if (ix.Args.Count != 1) throw new SpikeUnsupported("MIndex:multi");
        if (ix.Target is MCode pc && FindLocal(pc.Text) is { } pl && stackArrays.TryGetValue(pl, out var sa))
        {
            PtrElem(pl, sa.Elem, ix.Args[0]);
            il.Ldobj(sa.Elem);
            return sa.Elem;
        }
        var t = Infer(ix.Target);
        if (t.IsArray)
        {
            var et = t.GetElementType()!;
            if (NoBounds) { ElemAddr(ix.Target, t, et, ix.Args[0], true); il.Ldobj(et); return et; }
            Expr(ix.Target, t);
            Expr(ix.Args[0], typeof(int));
            il.Ldelem(et);
            return et;
        }
        if (t == typeof(string))
        {
            Expr(ix.Target, t);
            Expr(ix.Args[0], typeof(int));
            il.Emit(OpCodes.Callvirt, typeof(string).GetMethod("get_Chars")!);
            return typeof(char);
        }
        if (t is TypeBuilder) throw new SpikeUnsupported("MIndex:alice-indexer");
        var getter = t.GetProperties().FirstOrDefault(p => p.GetIndexParameters().Length == 1)?.GetGetMethod() ?? throw new SpikeUnsupported("MIndex:indexer", t.Name);
        if (t.IsValueType) Address(ix.Target); else Expr(ix.Target, t);
        Expr(ix.Args[0], getter.GetParameters()[0].ParameterType);
        il.Emit(t.IsValueType ? OpCodes.Call : OpCodes.Callvirt, getter);
        return getter.ReturnType;
    }

    Type NewArray(MNewArray na)
    {
        if (na.ElemCs.Contains('[')) throw new SpikeUnsupported("MNewArray:jagged");
        if (na.ElemCs == "" && na.Items != null) throw new SpikeUnsupported("MNewArray:implicit");
        var et = r.Type(na.ElemCs);
        if (na.Items == null)
        {
            Expr(na.Size!, typeof(int));
            il.Emit(OpCodes.Newarr, et);
            return et.MakeArrayType();
        }
        il.Ldc(na.Items.Count);
        il.Emit(OpCodes.Newarr, et);
        for (int i = 0; i < na.Items.Count; i++)
        {
            il.Emit(OpCodes.Dup);
            il.Ldc(i);
            Expr(na.Items[i], et);
            il.Stelem(et);
        }
        return et.MakeArrayType();
    }

    Type New(MNew n)
    {
        if (n.Init != null) throw new SpikeUnsupported("MNew:initializer");
        var t = r.Type(n.TypeCs);
        if (r.AliceCtors.TryGetValue(t, out var ac))
        {
            var ps = ac is ConstructorBuilder ? r.AliceFields[t].Values.Select(f => f.FieldType).ToArray() : ac.GetParameters().Select(p => p.ParameterType).ToArray();
            if (ps.Length != n.Args.Count) throw new SpikeUnsupported("MNew:alice-ctor-arity", n.TypeCs);
            for (int i = 0; i < ps.Length; i++) Expr(n.Args[i], ps[i]);
            il.Emit(OpCodes.Newobj, ac);
            return t;
        }
        if (t is TypeBuilder) throw new SpikeUnsupported("MNew:alice-class", n.TypeCs);
        var argTypes = n.Args.Select(Infer).ToList();
        var lits = n.Args.Select(a => Literal(a, out var v) ? v : null).ToList();
        if (t.IsValueType && n.Args.Count == 0)
        {
            var tmp = il.DeclareLocal(t);
            il.Emit(OpCodes.Ldloca, tmp);
            il.Emit(OpCodes.Initobj, t);
            il.Emit(OpCodes.Ldloc, tmp);
            return t;
        }
        var ctor = (ConstructorInfo?)Pick(t.GetConstructors(), argTypes, lits) ?? throw new SpikeUnsupported("MNew:ctor", n.TypeCs);
        var cps = ctor.GetParameters();
        for (int i = 0; i < cps.Length; i++) Expr(n.Args[i], cps[i].ParameterType);
        il.Emit(OpCodes.Newobj, ctor);
        return t;
    }

    // ---------------- type inference (what `var` means; Roslyn does this for the C# backend) ----------------

    public Type Infer(MExpr e)
    {
        e = Lower(e);
        if (e is MCode tl && LeafType(tl) is { } ltt) return ltt;
        switch (e)
        {
            case MCode c:
                if (stacked.TryGetValue(c.Text, out var stk)) return stk;
                if (FindLocal(c.Text) is { } lb) return lb.LocalType;
                if (args.TryGetValue(c.Text, out var a)) return a.Type;
                if (TryLiteral(c.Text, out var lt, out _)) return lt;
                if (StaticPath(c.Text) is { } sp) return sp is FieldInfo f ? f.FieldType : ((PropertyInfo)sp).PropertyType;
                throw new SpikeUnsupported("MCode:other", c.Text);
            case MBinary b:
                {
                    if (b.Op is AirBinOp.And or AirBinOp.Or or AirBinOp.Eq or AirBinOp.Ne or AirBinOp.Lt or AirBinOp.Le or AirBinOp.Gt or AirBinOp.Ge) return typeof(bool);
                    var l = Infer(b.L);
                    if (b.Op is AirBinOp.Shl or AirBinOp.Shr or AirBinOp.UShr) return Widen(l);
                    var rr = Infer(b.R);
                    if (b.Op == AirBinOp.Add && (l == typeof(string) || rr == typeof(string))) return typeof(string);
                    Literal(b.L, out var ll);
                    Literal(b.R, out var rl);
                    return Promote(l, rr, ll, rl);
                }
            case MUnary u:
                return u.Op switch
                {
                    AirUnOp.Not => typeof(bool),
                    AirUnOp.Neg when Literal(u, out _) => TryLiteral("-" + ((MCode)u.E).Text, out var nt, out _) ? nt : typeof(int),
                    AirUnOp.Neg => Widen(Infer(u.E)) is var w && w == typeof(uint) ? typeof(long) : Widen(Infer(u.E)),
                    AirUnOp.BitNot => Widen(Infer(u.E)),
                    AirUnOp.Inc or AirUnOp.Dec or AirUnOp.NotNull => Infer(u.E),
                    _ => throw new SpikeUnsupported("MUnary:" + u.Op),
                };
            case MCond c:
                {
                    var ta = Infer(c.A);
                    var tb = Infer(c.B);
                    if (ta == tb) return ta;
                    Literal(c.A, out var la);
                    Literal(c.B, out var lb2);
                    if (la != null && LiteralFits(la, tb)) return tb;
                    if (lb2 != null && LiteralFits(lb2, ta)) return ta;
                    if (ImplicitNumeric(ta, tb)) return tb;
                    if (ImplicitNumeric(tb, ta)) return ta;
                    if (c.B is MCode { Text: "null" } && !ta.IsValueType) return ta;
                    if (c.A is MCode { Text: "null" } && !tb.IsValueType) return tb;
                    throw new SpikeUnsupported("MCond:types", ta.Name + "/" + tb.Name);
                }
            case MCast c: return r.Type(c.TypeCs);
            case MCall c:
                if (c.Target == null && c.Name == "unchecked" && c.Args.Count == 1) return Infer(c.Args[0]);
                return Resolve(c).Ret;
            case MMember m:
                {
                    if (m.Target is MCode tc && !FindLocalOrArg(tc.Text) && r.TryType(tc.Text) is { } st)
                    {
                        var sm = (MemberInfo?)st.GetField(m.Name, BindingFlags.Public | BindingFlags.Static) ?? st.GetProperty(m.Name, BindingFlags.Public | BindingFlags.Static)
                            ?? throw new SpikeUnsupported("MMember:unresolved-static", tc.Text + "." + m.Name);
                        return sm is FieldInfo f ? f.FieldType : ((PropertyInfo)sm).PropertyType;
                    }
                    if (m.Name == "Length" && m.Target is MCode pc && FindLocal(pc.Text) is { } pl && stackArrays.ContainsKey(pl)) return typeof(int);
                    var t = Infer(m.Target);
                    if (t.IsArray && m.Name == "Length") return typeof(int);
                    var (fi, _) = FieldOf(m.Target, m.Name);
                    if (fi != null) return fi.FieldType;
                    if (t is TypeBuilder) throw new SpikeUnsupported("MMember:alice-property", m.Name);
                    return t.GetProperty(m.Name, BindingFlags.Public | BindingFlags.Instance)?.PropertyType ?? throw new SpikeUnsupported("MMember:unresolved", t.Name + "." + m.Name);
                }
            case MIndex ix:
                {
                    if (ix.Target is MCode pc && FindLocal(pc.Text) is { } pl && stackArrays.TryGetValue(pl, out var sa)) return sa.Elem;
                    var t = Infer(ix.Target);
                    if (t.IsArray) return t.GetElementType()!;
                    if (t == typeof(string)) return typeof(char);
                    if (t is TypeBuilder) throw new SpikeUnsupported("MIndex:alice-indexer");
                    return t.GetProperties().FirstOrDefault(p => p.GetIndexParameters().Length == 1)?.PropertyType ?? throw new SpikeUnsupported("MIndex:indexer", t.Name);
                }
            case MNewArray na:
                if (na.ElemCs == "" || na.ElemCs.Contains('[')) throw new SpikeUnsupported(na.ElemCs == "" ? "MNewArray:implicit" : "MNewArray:jagged");
                return r.Type(na.ElemCs).MakeArrayType();
            case MNew n: return r.Type(n.TypeCs);
            case MTuple t:
                {
                    var items = t.Items.Select(Infer).ToArray();
                    if (items.Length is < 2 or > 7) throw new SpikeUnsupported("MTuple:arity");
                    return System.Type.GetType("System.ValueTuple`" + items.Length)!.MakeGenericType(items);
                }
        }
        throw new SpikeUnsupported(e.GetType().Name);
    }
}

/// <summary>Compact encodings (ILGenerator emits exactly the opcode it is given; IL size drives the JIT's inliner).</summary>
static class IlCompact
{
    public static void Ldc(this ILGenerator il, int v)
    {
        switch (v)
        {
            case -1: il.Emit(OpCodes.Ldc_I4_M1); return;
            case 0: il.Emit(OpCodes.Ldc_I4_0); return;
            case 1: il.Emit(OpCodes.Ldc_I4_1); return;
            case 2: il.Emit(OpCodes.Ldc_I4_2); return;
            case 3: il.Emit(OpCodes.Ldc_I4_3); return;
            case 4: il.Emit(OpCodes.Ldc_I4_4); return;
            case 5: il.Emit(OpCodes.Ldc_I4_5); return;
            case 6: il.Emit(OpCodes.Ldc_I4_6); return;
            case 7: il.Emit(OpCodes.Ldc_I4_7); return;
            case 8: il.Emit(OpCodes.Ldc_I4_8); return;
        }
        if (v is >= sbyte.MinValue and <= sbyte.MaxValue) il.Emit(OpCodes.Ldc_I4_S, (sbyte)v);
        else il.Emit(OpCodes.Ldc_I4, v);
    }

    public static void Ldarg(this ILGenerator il, int i)
    {
        switch (i)
        {
            case 0: il.Emit(OpCodes.Ldarg_0); return;
            case 1: il.Emit(OpCodes.Ldarg_1); return;
            case 2: il.Emit(OpCodes.Ldarg_2); return;
            case 3: il.Emit(OpCodes.Ldarg_3); return;
        }
        if (i <= 255) il.Emit(OpCodes.Ldarg_S, (byte)i); else il.Emit(OpCodes.Ldarg, (short)i);
    }

    public static void Starg(this ILGenerator il, int i)
    {
        if (i <= 255) il.Emit(OpCodes.Starg_S, (byte)i); else il.Emit(OpCodes.Starg, (short)i);
    }

    public static void Ldarga(this ILGenerator il, int i)
    {
        if (i <= 255) il.Emit(OpCodes.Ldarga_S, (byte)i); else il.Emit(OpCodes.Ldarga, (short)i);
    }
}

static class IlTyped
{
    static OpCode? Pick(Type t, OpCode i1, OpCode u1, OpCode i2, OpCode u2, OpCode i4, OpCode u4, OpCode i8, OpCode r4, OpCode r8, OpCode @ref)
    {
        if (t == typeof(sbyte)) return i1;
        if (t == typeof(byte) || t == typeof(bool)) return u1;
        if (t == typeof(short)) return i2;
        if (t == typeof(ushort) || t == typeof(char)) return u2;
        if (t == typeof(int)) return i4;
        if (t == typeof(uint)) return u4;
        if (t == typeof(long) || t == typeof(ulong)) return i8;
        if (t == typeof(float)) return r4;
        if (t == typeof(double)) return r8;
        if (t is not TypeBuilder && !t.IsValueType && !t.IsGenericParameter && !t.IsPointer) return @ref;
        return null;
    }

    public static void Ldelem(this ILGenerator il, Type t)
    {
        if (Pick(t, OpCodes.Ldelem_I1, OpCodes.Ldelem_U1, OpCodes.Ldelem_I2, OpCodes.Ldelem_U2, OpCodes.Ldelem_I4, OpCodes.Ldelem_U4, OpCodes.Ldelem_I8, OpCodes.Ldelem_R4, OpCodes.Ldelem_R8, OpCodes.Ldelem_Ref) is { } op) il.Emit(op);
        else il.Emit(OpCodes.Ldelem, t);
    }

    public static void Stelem(this ILGenerator il, Type t)
    {
        if (Pick(t, OpCodes.Stelem_I1, OpCodes.Stelem_I1, OpCodes.Stelem_I2, OpCodes.Stelem_I2, OpCodes.Stelem_I4, OpCodes.Stelem_I4, OpCodes.Stelem_I8, OpCodes.Stelem_R4, OpCodes.Stelem_R8, OpCodes.Stelem_Ref) is { } op) il.Emit(op);
        else il.Emit(OpCodes.Stelem, t);
    }

    public static void Ldobj(this ILGenerator il, Type t)
    {
        if (Pick(t, OpCodes.Ldind_I1, OpCodes.Ldind_U1, OpCodes.Ldind_I2, OpCodes.Ldind_U2, OpCodes.Ldind_I4, OpCodes.Ldind_U4, OpCodes.Ldind_I8, OpCodes.Ldind_R4, OpCodes.Ldind_R8, OpCodes.Ldind_Ref) is { } op) il.Emit(op);
        else il.Emit(OpCodes.Ldobj, t);
    }

    public static void Stobj(this ILGenerator il, Type t)
    {
        if (Pick(t, OpCodes.Stind_I1, OpCodes.Stind_I1, OpCodes.Stind_I2, OpCodes.Stind_I2, OpCodes.Stind_I4, OpCodes.Stind_I4, OpCodes.Stind_I8, OpCodes.Stind_R4, OpCodes.Stind_R8, OpCodes.Stind_Ref) is { } op) il.Emit(op);
        else il.Emit(OpCodes.Stobj, t);
    }
}
