using System.Reflection;
using System.Reflection.Metadata;
using Alice.Compiler.Mir;

namespace Alice.Compiler.Backend.Il;

/// <summary>Statements, calls and assignments of <see cref="IlBody"/>.</summary>
internal sealed partial class IlBody
{
    sealed record Loop(IlLabel Break, IlLabel Continue, int TryDepth) { public int BreakDepth { get; init; } = TryDepth; }

    readonly Stack<Loop> loops = new();
    int tryDepth;
    IlLocal? retLocal;
    IlLabel? retLabel;

    // ------------------------------------------------------------------ entry

    /// <summary>Emits a whole body; a value-returning method that falls off its end throws (unreachable in valid MIR).</summary>
    public void Body(List<MStmt> body, Action? prologue = null)
    {
        prologue?.Invoke();
        Stmts(body, scope: false);
        if (!Terminates(body))
        {
            if (ret == typeof(void)) il.EmitReturn();
            else
            {
                il.EmitLoadConst("unreachable end of method");
                il.EmitNewObj(c.Module.Import(typeof(InvalidOperationException).GetConstructor([typeof(string)])!));
                il.EmitThrow();
            }
        }
        if (retLabel is { } rl)
        {
            il.MarkLabel(rl);
            if (retLocal != null) il.EmitLoadLocal(retLocal);
            il.EmitReturn();
        }
    }

    static bool Terminates(List<MStmt> body) => body.Count > 0 && body[^1] switch
    {
        MReturn or MThrow or MBreak or MContinue => true,
        MIf { Else: { } e } i => Terminates(i.Then) && Terminates(e),
        MBlock { Lock: null } b => Terminates(b.Body),
        MExprStmt { E: MCall { Name: "raise" or "abort" } } => false,
        _ => false,
    };

    void Stmts(List<MStmt> list, bool scope = true)
    {
        if (scope) { scopes.Add(new(StringComparer.Ordinal)); il.BeginScope(); }
        foreach (var s in list) Stmt(s);
        if (scope) { il.EndScope(); scopes.RemoveAt(scopes.Count - 1); }
    }

    void Mark(MStmt s)
    {
        if (s.Span is { } sp) span = sp;
        else if (s.Line > 0) span = new Span(c.File, s.Line, 1, 0);
        if (s.Line > 0 && c.File.Length > 0 && s is not (MBlock or MTry))
            il.MarkSequencePoint(Columns(s.Span is { } x && x.Line == s.Line ? x with { File = x.File.Length > 0 ? x.File : c.File } : new Span(c.File, s.Line, 0, 0)));
    }

    /// <summary>Real column and length for a statement's sequence point (review-r1 F19): a missing (or placeholder 1) column is the
    /// line's first non-blank character, a missing length runs to the end of the line (trailing blanks dropped).</summary>
    Span Columns(Span sp)
    {
        if (!c.SourceLines.TryGetValue(sp.File, out var lines) || sp.Line < 1 || sp.Line > lines.Length)
            return sp with { Col = Math.Max(1, sp.Col) };
        var text = lines[sp.Line - 1].TrimEnd();
        int col = sp.Col;
        if (col <= 1 || col > text.Length) { col = 1; while (col <= text.Length && char.IsWhiteSpace(text[col - 1])) col++; }
        int len = sp.Len > 0 ? sp.Len : text.Length - col + 1;
        return sp with { Col = col, Len = Math.Max(1, len) };
    }

    void Stmt(MStmt s)
    {
        c.Stats.Seen(s.GetType().Name);
        Mark(s);
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
                    if (i.Else != null && !Terminates(i.Then)) il.EmitBranch(end);
                    il.MarkLabel(els);
                    if (i.Else != null) Stmts(i.Else);
                    il.MarkLabel(end);
                    break;
                }
            case MWhile w:
                {
                    var body = il.DefineLabel();
                    var cond = il.DefineLabel();
                    var brk = il.DefineLabel();
                    il.EmitBranch(cond);
                    il.MarkLabel(body);
                    loops.Push(new(brk, cond, tryDepth));
                    Stmts(w.Body);
                    loops.Pop();
                    il.MarkLabel(cond);
                    Jump(w.Cond, true, body);
                    il.MarkLabel(brk);
                    break;
                }
            case MFor f: For(f); break;
            case MForeach fe: Foreach(fe); break;
            case MReturn r: Return(r); break;
            case MBreak: Branch(loops.Count > 0 ? loops.Peek() : throw No("MBreak:outside-loop"), brk: true); break;
            case MContinue: Branch(loops.Count > 0 ? loops.Peek() : throw No("MContinue:outside-loop"), brk: false); break;
            case MThrow t:
                {
                    var tt = Raw(t.Value);
                    if (!typeof(Exception).IsAssignableFrom(tt)) throw No("MThrow:type", tt.Name);
                    il.EmitThrow();
                    break;
                }
            case MTry tr: Try(tr); break;
            case MBlock b: Block(b); break;
            case MDeconstruct dc: Deconstruct(dc); break;
            case MUsing u: Using(u); break;
            case AirSwitch sw: Switch(sw); break;
            default: if (!StmtExt(s)) throw No(s.GetType().Name); break; // H2 il-emit2
        }
    }

    /// <summary>Dense jump table: `switch` on Value - Min; out-of-range values and -1 slots run the default.</summary>
    void Switch(AirSwitch sw)
    {
        var vt = Infer(sw.Value);
        var wt = IlTypes.Widen(vt.IsEnum ? Enum.GetUnderlyingType(vt) : vt);
        if (!IlTypes.IsIntegral(wt)) throw No("AirSwitch:value", vt.Name);
        EmitAs(sw.Value, vt, wt);
        if (sw.Min != 0) { Const(wt, sw.Min); il.EmitBinary(IlBinOp.Sub); }
        var dflt = il.DefineLabel();
        var end = il.DefineLabel();
        if (IlTypes.Is64(wt))
        {
            var tmp = Temp(wt);
            il.EmitStoreLocal(tmp);
            il.EmitLoadLocal(tmp);
            il.EmitLoadConst((long)sw.Table.Count);
            il.EmitBranch(dflt, IlCond.GeUn);
            il.EmitLoadLocal(tmp);
            il.Emit(ILOpCode.Conv_i4);
        }
        var caseLabels = sw.Cases.Select(_ => il.DefineLabel()).ToList();
        il.EmitSwitch([.. sw.Table.Select(k => k < 0 ? dflt : caseLabels[k])]);
        il.EmitBranch(dflt);
        for (int k = 0; k < sw.Cases.Count; k++)
        {
            il.MarkLabel(caseLabels[k]);
            Stmts(sw.Cases[k]);
            if (!Terminates(sw.Cases[k])) il.EmitBranch(end);
        }
        il.MarkLabel(dflt);
        if (sw.Default != null) Stmts(sw.Default);
        il.MarkLabel(end);
    }

    void Branch(Loop l, bool brk)
    {
        var target = brk ? l.Break : l.Continue;
        if (tryDepth > (brk ? l.BreakDepth : l.TryDepth)) il.EmitLeave(target); else il.EmitBranch(target);
    }

    void Return(MReturn r)
    {
        if (tryDepth > 0)
        {
            retLabel ??= il.DefineLabel();
            if (r.Value != null)
            {
                retLocal ??= il.DeclareLocal(T.Il(ret));
                Emit(r.Value, ret);
                il.EmitStoreLocal(retLocal);
            }
            il.EmitLeave(retLabel.Value);
            return;
        }
        if (r.Value != null)
        {
            if (ret == typeof(void)) { var t = Raw(r.Value); if (t != typeof(void)) il.EmitPop(); }
            else Emit(r.Value, ret);
        }
        il.EmitReturn();
    }

    void Decl(MDecl d)
    {
        Type t;
        if (d.TypeCs != null && !d.TypeCs.StartsWith("ref ")) t = T.Parse(d.TypeCs, span);
        else if (d.TypeCs != null) throw No("MDecl:ref-local", d.TypeCs);
        else if (d.Init != null) t = Infer(d.Init);
        else throw No("MDecl:untyped", d.Name);
        if (t == typeof(void)) throw No("MDecl:void", d.Name);
        if (d.Init is null || IsDefaultLit(d.Init))
        {
            var l0 = Declare(d.Name, t);
            DefaultValue(t);
            il.EmitStoreLocal(l0);
            return;
        }
        Emit(d.Init, t);
        var l = Declare(d.Name, t);
        il.EmitStoreLocal(l);
    }

    void ExprStmt(MExpr e)
    {
        if (e.Span is { } sp) span = sp;
        if (e is MUnary { Op: AirUnOp.Inc or AirUnOp.Dec } u)
        {
            c.Stats.Seen("MUnary");
            Assign(u.E, u.Op == AirUnOp.Inc ? "+=" : "-=", AirConst.Of(1), u.Checked);
            return;
        }
        var t = Raw(e);
        if (t != typeof(void)) il.EmitPop();
    }

    void Block(MBlock b)
    {
        if (b.Lock != null) { Lock(b); return; }
        if (b.Unchecked) uncheckedDepth++;
        try { Stmts(b.Body); }
        finally { if (b.Unchecked) uncheckedDepth--; }
    }

    /// <summary>`lock (x) { }`: Monitor.Enter(x, ref taken) in a try, Exit in the finally when taken.</summary>
    void Lock(MBlock b)
    {
        var lt = Infer(b.Lock!);
        if (lt.IsValueType) throw No("MBlock:lock-value");
        Raw(b.Lock!);
        var obj = Temp(lt);
        il.EmitStoreLocal(obj);
        var taken = Temp(typeof(bool));
        il.EmitLoadConst(false);
        il.EmitStoreLocal(taken);
        il.BeginTry();
        tryDepth++;
        il.EmitLoadLocal(obj);
        il.EmitLoadLocalAddress(taken);
        il.EmitCall(c.Module.Import(typeof(Monitor).GetMethod("Enter", [typeof(object), typeof(bool).MakeByRefType()])!));
        Stmts(b.Body);
        tryDepth--;
        il.BeginFinally();
        var skip = il.DefineLabel();
        il.EmitLoadLocal(taken);
        il.EmitBranch(skip, IlCond.False);
        il.EmitLoadLocal(obj);
        il.EmitCall(c.Module.Import(typeof(Monitor).GetMethod("Exit", [typeof(object)])!));
        il.MarkLabel(skip);
        il.EndTry();
    }

    void Try(MTry tr)
    {
        il.BeginTry();
        tryDepth++;
        Stmts(tr.Body);
        foreach (var k in tr.Catches)
        {
            var et = T.Parse(k.TypeCs, span);
            il.BeginCatch(T.Il(et));
            scopes.Add(new(StringComparer.Ordinal));
            if (k.Name != null) il.EmitStoreLocal(Declare(k.Name, et)); else il.EmitPop();
            Stmts(k.Body);
            scopes.RemoveAt(scopes.Count - 1);
        }
        if (tr.Finally != null)
        {
            il.BeginFinally();
            Stmts(tr.Finally);
        }
        tryDepth--;
        il.EndTry();
    }

    /// <summary>`using (var x = v) { }`: Dispose in a finally (null-checked for references).</summary>
    void Using(MUsing u)
    {
        if (u.Await) throw No("MUsing:await");
        var t = Infer(u.Value);
        scopes.Add(new(StringComparer.Ordinal));
        Emit(u.Value, t);
        var x = Declare(u.Name, t);
        il.EmitStoreLocal(x);
        il.BeginTry();
        tryDepth++;
        Stmts(u.Body);
        tryDepth--;
        il.BeginFinally();
        var dispose = typeof(IDisposable).GetMethod("Dispose")!;
        if (t.IsValueType)
        {
            il.EmitLoadLocalAddress(x);
            il.EmitConstrainedPrefix(T.Il(t));
            il.EmitCall(c.Module.Import(dispose), virtualCall: true);
        }
        else
        {
            var skip = il.DefineLabel();
            il.EmitLoadLocal(x);
            il.EmitBranch(skip, IlCond.False);
            il.EmitLoadLocal(x);
            il.EmitCall(c.Module.Import(dispose), virtualCall: true);
            il.MarkLabel(skip);
        }
        il.EndTry();
        scopes.RemoveAt(scopes.Count - 1);
    }

    void For(MFor f)
    {
        scopes.Add(new(StringComparer.Ordinal));
        il.BeginScope();
        Type t = f.VarType != null ? T.Parse(f.VarType, span) : Infer(f.Start);
        Emit(f.Start, t);
        var v = Declare(f.Var, t);
        il.EmitStoreLocal(v);
        var body = il.DefineLabel();
        var cont = il.DefineLabel();
        var cond = il.DefineLabel();
        var brk = il.DefineLabel();
        il.EmitBranch(cond);
        il.MarkLabel(body);
        loops.Push(new(brk, cont, tryDepth));
        Stmts(f.Body);
        loops.Pop();
        il.MarkLabel(cont);
        var name = new AirLocalRef(f.Var);
        switch (f.Step)
        {
            case { Op: "++" }: Assign(name, "+=", AirConst.Of(1)); break;
            case { Op: "--" }: Assign(name, "-=", AirConst.Of(1)); break;
            case { Op: "+=" or "-=" or "=", Value: { } sv }: Assign(name, f.Step.Op, sv); break;
            default: throw No("MFor:step", f.Step.Op);
        }
        il.MarkLabel(cond);
        Jump(new MBinary { Op = AirOps.ParseBin(f.Cmp), L = name, R = f.End }, true, body);
        il.MarkLabel(brk);
        il.EndScope();
        scopes.RemoveAt(scopes.Count - 1);
    }

    /// <summary>foreach over arrays and strings (index loops, as Roslyn), and the GetEnumerator pattern (struct
    /// enumerators such as List&lt;T&gt;.Enumerator stay unboxed; disposable enumerators are disposed in a finally).</summary>
    void Foreach(MForeach fe)
    {
        if (fe.Await) throw No("MForeach:await");
        var ct = Infer(fe.Coll);
        scopes.Add(new(StringComparer.Ordinal));
        il.BeginScope();
        var body = il.DefineLabel();
        var cont = il.DefineLabel();
        var brk = il.DefineLabel();
        if (ct.IsSZArray || ct == typeof(string))
        {
            var et = ct == typeof(string) ? typeof(char) : ct.GetElementType()!;
            Raw(fe.Coll);
            var arr = Temp(ct);
            il.EmitStoreLocal(arr);
            var i = Temp(typeof(int));
            il.EmitLoadConst(0);
            il.EmitStoreLocal(i);
            var cond = il.DefineLabel();
            il.EmitBranch(cond);
            il.MarkLabel(body);
            il.EmitLoadLocal(arr);
            il.EmitLoadLocal(i);
            if (ct == typeof(string)) il.EmitCall(c.Module.Import(typeof(string).GetMethod("get_Chars")!), virtualCall: true);
            else il.EmitLoadElement(T.Il(et));
            BindVars(fe.Vars, et);
            loops.Push(new(brk, cont, tryDepth));
            Stmts(fe.Body);
            loops.Pop();
            il.MarkLabel(cont);
            il.EmitLoadLocal(i);
            il.EmitLoadConst(1);
            il.EmitBinary(IlBinOp.Add);
            il.EmitStoreLocal(i);
            il.MarkLabel(cond);
            il.EmitLoadLocal(i);
            il.EmitLoadLocal(arr);
            il.Emit(ILOpCode.Ldlen);
            il.Emit(ILOpCode.Conv_i4);
            il.EmitBranch(body, IlCond.Lt);
            il.MarkLabel(brk);
        }
        else
        {
            var ge = FindInstanceMethod(ct, "GetEnumerator", 0) ?? throw No("MForeach:collection", ct.Name);
            var en = ge.ReturnType;
            var move = FindInstanceMethod(en, "MoveNext", 0) ?? throw No("MForeach:enumerator", en.Name);
            var cur = FindProperty(en, "Current") ?? throw No("MForeach:enumerator", en.Name);
            CallInstance(fe.Coll, ct, ge);
            var e = Temp(en);
            il.EmitStoreLocal(e);
            bool disposable = typeof(IDisposable).IsAssignableFrom(en);
            if (disposable) { il.BeginTry(); tryDepth++; }
            var cond = il.DefineLabel();
            il.EmitBranch(cond);
            il.MarkLabel(body);
            LoadRecv(e, en);
            EmitInstanceCall(en, cur.GetGetMethod()!);
            var et = Deref(cur.PropertyType);
            BindVars(fe.Vars, et);
            loops.Push(new(brk, cont, tryDepth) { BreakDepth = disposable ? tryDepth - 1 : tryDepth }); // il-emit2: brk lies after the try
            Stmts(fe.Body);
            loops.Pop();
            il.MarkLabel(cont);
            il.MarkLabel(cond);
            LoadRecv(e, en);
            EmitInstanceCall(en, move);
            il.EmitBranch(body, IlCond.True);
            if (disposable)
            {
                // `break` leaves to brk: it must lie after the protected region
                var after = il.DefineLabel();
                il.EmitLeave(after);
                tryDepth--;
                il.BeginFinally();
                var dispose = typeof(IDisposable).GetMethod("Dispose")!;
                if (en.IsValueType)
                {
                    il.EmitLoadLocalAddress(e);
                    il.EmitConstrainedPrefix(T.Il(en));
                    il.EmitCall(c.Module.Import(dispose), virtualCall: true);
                }
                else
                {
                    var skip = il.DefineLabel();
                    il.EmitLoadLocal(e);
                    il.EmitBranch(skip, IlCond.False);
                    il.EmitLoadLocal(e);
                    il.EmitCall(c.Module.Import(dispose), virtualCall: true);
                    il.MarkLabel(skip);
                }
                il.EndTry();
                il.MarkLabel(after);
                il.MarkLabel(brk);
            }
            else il.MarkLabel(brk);
        }
        il.EndScope();
        scopes.RemoveAt(scopes.Count - 1);
    }

    void LoadRecv(IlLocal l, Type t)
    {
        if (t.IsValueType) il.EmitLoadLocalAddress(l); else il.EmitLoadLocal(l);
    }

    /// <summary>The element on the stack bound to the loop variable(s): one name, or a deconstructed pair/tuple.</summary>
    void BindVars(List<string> vars, Type et)
    {
        if (vars.Count == 1) { il.EmitStoreLocal(Declare(vars[0], et)); return; }
        var tmp = Temp(et);
        il.EmitStoreLocal(tmp);
        for (int k = 0; k < vars.Count; k++)
        {
            if (vars[k] == "_") continue;
            il.EmitLoadLocalAddress(tmp);
            Type ft;
            if (et.IsGenericType && et.GetGenericTypeDefinition() == typeof(KeyValuePair<,>) && vars.Count == 2)
            {
                var p = et.GetProperty(k == 0 ? "Key" : "Value")!;
                il.EmitCall(T.Method(p.GetGetMethod()!));
                ft = p.PropertyType;
            }
            else if (et.IsGenericType && et.FullName!.StartsWith("System.ValueTuple`"))
            {
                var f = et.GetField("Item" + (k + 1)) ?? throw No("MForeach:deconstruct", et.Name);
                il.EmitLoadField(T.Field(f));
                ft = f.FieldType;
            }
            else throw No("MForeach:deconstruct", et.Name);
            il.EmitStoreLocal(Declare(vars[k], ft));
        }
    }

    void Deconstruct(MDeconstruct dc)
    {
        if (dc.Value is MTuple tup && tup.Items.Count == dc.Names.Count)
        {
            var temps = new List<(IlLocal L, Type T)>();
            for (int k = 0; k < tup.Items.Count; k++)
            {
                var t = dc.Declare || dc.Names[k] == "_" ? Infer(tup.Items[k]) : TypeOfTarget(dc.Names[k]);
                Emit(tup.Items[k], t);
                var tmp = Temp(t);
                il.EmitStoreLocal(tmp);
                temps.Add((tmp, t));
            }
            for (int k = 0; k < temps.Count; k++)
            {
                if (dc.Names[k] == "_") continue;
                if (dc.Declare) { il.EmitLoadLocal(temps[k].L); il.EmitStoreLocal(Declare(dc.Names[k], temps[k].T)); }
                else StoreName(dc.Names[k], () => { il.EmitLoadLocal(temps[k].L); return temps[k].T; });
            }
            return;
        }
        var vt = Infer(dc.Value);
        Emit(dc.Value, vt);
        var all = Temp(vt);
        il.EmitStoreLocal(all);
        for (int k = 0; k < dc.Names.Count; k++)
        {
            if (dc.Names[k] == "_") continue;
            int kk = k;
            Type Load()
            {
                if (vt.IsGenericType && vt.FullName!.StartsWith("System.ValueTuple`"))
                {
                    var f = vt.GetField("Item" + (kk + 1))!;
                    il.EmitLoadLocalAddress(all);
                    il.EmitLoadField(T.Field(f));
                    return f.FieldType;
                }
                if (T.Alice(vt) is { } a && a.Sym.FieldOrder.Count == dc.Names.Count && a.FindField(a.Sym.Fields[a.Sym.FieldOrder[kk]].CsName.TrimStart('@')) is { } af)
                {
                    LoadRecv(all, vt);
                    return LoadField(af, vt.IsValueType);
                }
                throw No("MDeconstruct:non-tuple", vt.Name);
            }
            if (dc.Declare) { var ft = Load(); il.EmitStoreLocal(Declare(dc.Names[k], ft)); }
            else StoreName(dc.Names[k], Load);
        }
    }

    Type TypeOfTarget(string name) =>
        FindLocal(name)?.T ?? (args.TryGetValue(Bare(name), out var a) ? a.Type : Implicit(name) is AField f ? f.Type : throw No("MCode:unknown-name", name));

    /// <summary>Stores into a named variable or implicit field; <paramref name="value"/> pushes the value and returns its type.</summary>
    void StoreName(string name, Func<Type> value)
    {
        if (FindLocal(name) is { } l) { var t = value(); Convert(t, l.T, false, true); il.EmitStoreLocal(l.L); return; }
        if (args.TryGetValue(Bare(name), out var a))
        {
            if (a.Type.IsByRef) { il.EmitLoadArg(a.Index); var t1 = value(); var el = a.Type.GetElementType()!; Convert(t1, el, false, true); il.EmitStoreObj(T.Il(el)); return; }
            var t = value(); Convert(t, a.Type, false, true); il.EmitStoreArg(a.Index); return;
        }
        if (Implicit(name) is AField f)
        {
            if (!f.IsStatic) il.EmitLoadArg(0);
            var t = value();
            Convert(t, f.Type, false, true);
            StoreField(f);
            return;
        }
        throw No("MAssign:unknown-target", name);
    }

    void StoreField(AField f)
    {
        if (f.Setter != null) il.EmitCall(f.Setter, virtualCall: !f.IsStatic && !f.Owner.IsValueType);
        else if (f.Field != null) il.EmitStoreField(f.Field);
        else throw No("MAssign:readonly", f.Name);
    }

    // ------------------------------------------------------------------ assignment

    static AirBinOp CompoundOp(string op, IlBody b) => op switch
    {
        "+=" => AirBinOp.Add, "-=" => AirBinOp.Sub, "*=" => AirBinOp.Mul, "/=" => AirBinOp.Div, "%=" => AirBinOp.Mod,
        "&=" => AirBinOp.BitAnd, "|=" => AirBinOp.BitOr, "^=" => AirBinOp.BitXor, "<<=" => AirBinOp.Shl, ">>=" => AirBinOp.Shr, ">>>=" => AirBinOp.UShr,
        _ => throw b.No("MAssign:op", op),
    };

    void Assign(MExpr target, string op, MExpr value, bool nodeChecked = true)
    {
        if (op == "??=")
        {
            var skip = il.DefineLabel();
            var tt = Infer(target);
            if (tt.IsValueType) throw No("MAssign:??=-value");
            Raw(target);
            il.EmitBranch(skip, IlCond.True);
            Assign(target, "=", value);
            il.MarkLabel(skip);
            return;
        }
        bool compound = op != "=";
        bool chk = nodeChecked && Checked;
        switch (target)
        {
            case MCode or AirLocalRef:
                {
                    string name = (target is MCode mc ? mc.Text : ((AirLocalRef)target).Name).Trim();
                    if (IsVar(name) || Implicit(name) is AField)
                    {
                        var t = TypeOfTarget(name);
                        var et = t.IsByRef ? t.GetElementType()! : t;
                        StoreName(name, () =>
                        {
                            if (!compound) { Emit(value, et); return et; }
                            Raw(target);
                            BinTail(et, op, value, chk);
                            return et;
                        });
                        return;
                    }
                    if (StaticPath(name) is { } sp) { StoreStatic(sp, target, compound, op, value, chk); return; }
                    if (Reparse(name) is { } parsed and not MCode) { Assign(parsed, op, value, nodeChecked); return; }
                    throw No("MAssign:unknown-target", name);
                }
            case MMember m when !m.Safe:
                {
                    if (TypeOf(m.Target) is { } st)
                    {
                        var sm = StaticMember(st, m.Name) ?? throw No("MAssign:static", st.Name + "." + m.Name);
                        StoreStatic(sm, target, compound, op, value, chk);
                        return;
                    }
                    var rt = Infer(m.Target);
                    string name = Bare(m.Name);
                    if (EventAssign(m, rt, name, op, value)) return;   // D1.10 il-emit2
                    if (T.Alice(rt)?.FindField(name) is { IsStatic: false } af)
                    {
                        Receiver(m.Target, rt);
                        if (compound)
                        {
                            il.EmitDup();
                            LoadField(af, rt.IsValueType);
                            BinTail(af.Type, op, value, chk);
                        }
                        else Emit(value, af.Type);
                        StoreField(af);
                        return;
                    }
                    if (FindClrField(rt, name) is { } fi)
                    {
                        Receiver(m.Target, rt);
                        if (compound) { il.EmitDup(); il.EmitLoadField(T.Field(fi)); BinTail(fi.FieldType, op, value, chk); }
                        else Emit(value, fi.FieldType);
                        il.EmitStoreField(T.Field(fi));
                        return;
                    }
                    if (FindClrProperty(rt, name) is { } p && p.GetSetMethod() is { } setter)
                    {
                        Receiver(m.Target, rt);
                        if (compound) { il.EmitDup(); EmitInstanceCall(rt, p.GetGetMethod()!); BinTail(p.PropertyType, op, value, chk); }
                        else Emit(value, p.PropertyType);
                        EmitInstanceCall(rt, setter);
                        return;
                    }
                    throw No("MAssign:member", rt.Name + "." + name);
                }
            case MIndex ix:
                {
                    var at = Infer(ix.Target);
                    if (at.IsSZArray && ix.Args.Count == 1)
                    {
                        var et = at.GetElementType()!;
                        var iet = T.Il(et);
                        if (compound || et.IsValueType && !IlTypes.IsNumeric(et) && et != typeof(bool) && !et.IsEnum && !et.IsPointer)
                        {
                            Raw(ix.Target);
                            Emit(ix.Args[0], typeof(int));
                            il.Emit(ILOpCode.Ldelema, iet);
                            if (compound) { il.EmitDup(); il.EmitLoadObj(iet); BinTail(et, op, value, chk); }
                            else Emit(value, et);
                            il.EmitStoreObj(iet);
                            return;
                        }
                        Raw(ix.Target);
                        Emit(ix.Args[0], typeof(int));
                        Emit(value, et);
                        il.EmitStoreElement(iet);
                        return;
                    }
                    if (compound)
                    {
                        // indexer compound: `x[i] = (T)(x[i] op v)` reads target and index twice, so spill both to
                        // temps first (review-r1 F7); a value-type receiver stays in place unless it is a ref struct
                        var cur = Infer(ix);
                        var six = new MIndex { Target = Spill(ix.Target, at, !at.IsValueType || at.IsByRefLike), Type = ix.Type, Span = ix.Span };
                        foreach (var a in ix.Args) six.Args.Add(Spill(a, Infer(a), true));
                        var bin = new MBinary { Op = CompoundOp(op, this), L = six, R = value, Checked = nodeChecked };
                        Assign(six, "=", new IlConv { To = cur, E = bin, Checked = chk });
                        return;
                    }
                    if (at.IsArray)
                    {
                        Raw(ix.Target);
                        foreach (var a in ix.Args) Emit(a, typeof(int));
                        Emit(value, at.GetElementType()!);
                        il.EmitCall(T.Method(at.GetMethod("Set")!));
                        return;
                    }
                    // ref-returning indexers (Span<T>): store through the reference
                    var getter = FindIndexer(at, ix.Args.Count, "get_Item");
                    if (getter != null && getter.ReturnType.IsByRef)
                    {
                        Call(new MCall { Target = ix.Target, Name = "get_Item", Args = ix.Args }, indexer: true, keepRef: true);
                        var el = getter.ReturnType.GetElementType()!;
                        Emit(value, el);
                        il.EmitStoreObj(T.Il(el));
                        return;
                    }
                    var r = Call(new MCall { Target = ix.Target, Name = "set_Item", Args = [.. ix.Args, value] }, indexer: true);
                    if (r != typeof(void)) il.EmitPop();
                    return;
                }
        }
        throw No("MAssign:target:" + target.GetType().Name, Printer.E(target));
    }

    int spillN;

    /// <summary>Evaluates <paramref name="e"/> once into a fresh named local and returns a reference to it; literals,
    /// locals and arguments are returned unchanged (review-r1 F7).</summary>
    MExpr Spill(MExpr e, Type t, bool allowed)
    {
        if (!allowed || Literal(e, out _) || e is AirLocalRef || e is MCode c && (IsVar(c.Text) || c.Text.Trim() == "this")) return e;
        var name = "__spill" + spillN++;
        Emit(e, t);
        il.EmitStoreLocal(Declare(name, t));
        return new AirLocalRef(name, e.Type);
    }

    MethodInfo? FindIndexer(Type t, int argc, string name)
    {
        if (T.Alice(t) != null) return null;
        try { return t.GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == argc); }
        catch (Exception) { return null; }
    }

    void StoreStatic(object m, MExpr target, bool compound, string op, MExpr value, bool chk)
    {
        switch (m)
        {
            case AField f:
                if (compound) { Raw(target); BinTail(f.Type, op, value, chk); } else Emit(value, f.Type);
                StoreField(f);
                return;
            case FieldInfo { IsLiteral: false, IsInitOnly: false } fi:
                if (compound) { Raw(target); BinTail(fi.FieldType, op, value, chk); } else Emit(value, fi.FieldType);
                il.EmitStoreField(T.Field(fi));
                return;
            case PropertyInfo p when p.GetSetMethod() is { } s:
                if (compound) { Raw(target); BinTail(p.PropertyType, op, value, chk); } else Emit(value, p.PropertyType);
                il.EmitCall(T.Method(s));
                return;
        }
        throw No("MAssign:static", m.ToString());
    }

    /// <summary>The current value of type <paramref name="t"/> is on the stack: apply `op` with <paramref name="value"/>
    /// and convert back to <paramref name="t"/> (C#: `x op= v` is `x = (T)(x op v)`).</summary>
    void BinTail(Type t, string op, MExpr value, bool chk)
    {
        var bop = CompoundOp(op, this);
        if (t == typeof(string) && bop == AirBinOp.Add)
        {
            var vt = Infer(value);
            var tmp = Temp(typeof(string));
            il.EmitStoreLocal(tmp);
            Concat(new MBinary { Op = AirBinOp.Add, L = new AirLocalRef("__bt_l"), R = value }, typeof(string), vt, tmp);
            return;
        }
        var tn = t.IsEnum ? Enum.GetUnderlyingType(t) : t;
        if (bop is AirBinOp.Shl or AirBinOp.Shr or AirBinOp.UShr)
        {
            var wt = IlTypes.Widen(tn);
            Numeric(tn, wt, false);
            ShiftCount(value, wt);
            il.Emit(bop == AirBinOp.Shl ? ILOpCode.Shl : bop == AirBinOp.UShr || IlTypes.IsUnsigned(wt) ? ILOpCode.Shr_un : ILOpCode.Shr);
            Numeric(wt, tn, false);
            return;
        }
        if (tn == typeof(bool))
        {
            Emit(value, typeof(bool));
            Arith(bop, typeof(bool), false);
            return;
        }
        var vt2 = Infer(value);
        Literal(value, out var lit);
        if (!IlTypes.IsNumeric(tn) || !(IlTypes.IsNumeric(vt2.IsEnum ? Enum.GetUnderlyingType(vt2) : vt2) || lit != null))
        {
            var cand = UserOpMethod(bop, t, vt2) as Cand ?? throw No("MAssign:compound-operator", $"{t.Name} {op} {vt2.Name}");
            var tmp = Temp(t);
            il.EmitStoreLocal(tmp);
            il.EmitLoadLocal(tmp);
            Convert(t, cand.Params[0], false, true);
            Emit(value, cand.Params[1]);
            if (cand.Alice != null) il.EmitCall(cand.Alice.Def); else il.EmitCall(T.Method(cand.Clr!));
            Convert(cand.Ret, t, chk);
            return;
        }
        var pt = IlTypes.Promote(tn, vt2.IsEnum ? Enum.GetUnderlyingType(vt2) : vt2, null, lit);
        if (pt == typeof(decimal)) throw No("MAssign:decimal-compound");
        Numeric(tn, pt, false);
        EmitAs(value, vt2, pt);
        Arith(bop, pt, chk);
        Numeric(pt, tn, chk && pt != tn && !IlTypes.ImplicitNumeric(tn, pt) ? chk : chk && IlTypes.IsIntegral(tn) && pt != tn);
    }

    /// <summary>String concatenation where the left operand already sits in <paramref name="left"/>.</summary>
    Type Concat(MBinary b, Type lt, Type rt, IlLocal left)
    {
        scopes.Add(new(StringComparer.Ordinal) { ["__bt_l"] = (left, typeof(string)) });
        try { return Concat(b, lt, rt); }
        finally { scopes.RemoveAt(scopes.Count - 1); }
    }

    // ------------------------------------------------------------------ calls

    List<ArgInfo> ArgInfos(List<MExpr> xs)
    {
        var r = new List<ArgInfo>(xs.Count);
        foreach (var x in xs)
        {
            if (x is MArg ma)
            {
                string pre = ma.Prefix.Trim();
                if (pre.Contains(':')) throw No("MArg:named", ma.Prefix);
                if (pre.StartsWith("out") && (pre.Length > 3 || ma.E is MCode { Text: "_" })) r.Add(new ArgInfo(typeof(void), null, false, "out var"));
                else r.Add(new ArgInfo(Infer(ma.E), null, false, pre + " "));
                continue;
            }
            if (IsNullLit(x) || IsDefaultLit(x) || IsMethodGroup(x)) { r.Add(new ArgInfo(typeof(object), null, true, "")); continue; }
            if (x is MLambda) throw No("MLambda");
            Literal(x, out var lit);
            r.Add(new ArgInfo(Infer(x), lit, false, ""));
        }
        return r;
    }

    MethodInfo? FindInstanceMethod(Type t, string name, int argc)
    {
        if (T.Alice(t) != null) return null;
        try
        {
            return t.GetMethods(BindingFlags.Public | BindingFlags.Instance).Where(m => m.Name == name && m.GetParameters().Length == argc && !m.IsGenericMethodDefinition)
                .OrderBy(m => m.DeclaringType == t ? 0 : 1).FirstOrDefault()
                ?? (t.IsInterface ? t.GetInterfaces().Select(i => i.GetMethod(name, Type.EmptyTypes)).FirstOrDefault(m => m != null && argc == 0) : null);
        }
        catch (Exception) { return null; }
    }

    static PropertyInfo? FindProperty(Type t, string name)
    {
        var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
        if (p != null || !t.IsInterface) return p;
        return t.GetInterfaces().Select(i => i.GetProperty(name)).FirstOrDefault(x => x != null);
    }

    /// <summary>Calls a CLR instance method on the receiver expression (address for value types).</summary>
    void CallInstance(MExpr recv, Type rt, MethodInfo m)
    {
        Receiver(recv, rt);
        EmitInstanceCall(rt, m);
    }

    /// <summary>The receiver (reference, or address for value types) is on the stack.</summary>
    void EmitInstanceCall(Type rt, MethodInfo m)
    {
        if (rt.IsValueType)
        {
            if (m.DeclaringType == rt) il.EmitCall(T.Method(m)); // il-emit2: a struct method cannot be overridden (ILVerify CallVirtOnValueType)
            else { il.EmitConstrainedPrefix(T.Il(rt)); il.EmitCall(T.Method(m), virtualCall: true); }
            return;
        }
        il.EmitCall(T.Method(m), virtualCall: !m.IsStatic);
    }

    bool IsBaseRef(MExpr? e) => e is MCode { Text: "base" };

    Type Call(MCall call, bool indexer = false, bool keepRef = false)
    {
        if (call.Target == null && call.Name == "unchecked" && call.Args.Count == 1)
        {
            uncheckedDepth++;
            try { return Raw(call.Args[0]); }
            finally { uncheckedDepth--; }
        }
        if (call.Target == null && call.Name == "checked" && call.Args.Count == 1)
        {
            // an operand of a wrapping operator (MirFacts.KeepChecked): checked again, as C# `checked(...)`
            int saved = uncheckedDepth;
            uncheckedDepth = 0;
            try { return Raw(call.Args[0]); }
            finally { uncheckedDepth = saved; }
        }
        if (call.Safe && call.Target != null)
            return SafeAccess(call.Target, new MCall { Target = new MCode("__safe_recv"), Name = call.Name, Args = call.Args, Span = call.Span }, call.Name);
        if (call.Args.Any(a => a is MLambda)) throw No("MLambda", call.Name);
        string name = Bare(call.Name);
        Type[]? explicitArgs = null;
        if (name.IndexOf('<') is int lt0 and > 0 && name.EndsWith('>'))
        {
            explicitArgs = [.. IlTypes.SplitTop(name[(lt0 + 1)..^1]).Select(x => T.Parse(x, span))];
            name = name[..lt0];
        }
        var infos = ArgInfos(call.Args);
        var cands = new List<Cand>();
        Cand? pick = null;
        MExpr? recv = null;
        Type? rt = null;
        bool baseCall = false;
        if (call.Target == null)
        {
            foreach (var m in owner.FindMethods(name)) if (m.IsStatic || !isStatic) cands.Add(IlTypes.FromAlice(m));
            for (var o = owner.Sym.Parent; o != null; o = o.Parent)
                if (T.BySym.TryGetValue(o, out var ot)) cands.AddRange(ot.FindMethods(name).Where(m => m.IsStatic).Select(IlTypes.FromAlice));
            pick = T.Pick(cands, infos);
            if (pick == null && owner != c.ProgramT)
                pick = T.Pick(c.ProgramT.FindMethods(name).Where(m => m.IsStatic).Select(IlTypes.FromAlice), infos);
            if (pick == null && owner.ClrBase != null && !isStatic)
                pick = T.Pick(ClrMethods(owner.ClrBase, name, instance: true, explicitArgs), infos);
            pick ??= T.Pick(IlTypes.PreludeNamed(name).Select(m => IlTypes.FromClr(m)), infos);
            if (pick == null && FindLocal(name) is { } dl && typeof(Delegate).IsAssignableFrom(dl.T))
                return Invoke(new MInvoke { Target = new AirLocalRef(name), Args = call.Args });
            if (pick == null) throw No("MCall:unresolved-free", name + "/" + call.Args.Count);
        }
        else if (IsBaseRef(call.Target))
        {
            if (owner.Base != null) pick = T.Pick(owner.Base.FindMethods(name).Where(m => !m.IsStatic).Select(IlTypes.FromAlice), infos);
            if (pick == null && owner.ClrBase != null) pick = T.Pick(ClrMethods(owner.ClrBase, name, instance: true, explicitArgs), infos);
            if (pick == null) throw No("MCall:unresolved-base", name);
            baseCall = true;
        }
        else if (TypeOf(call.Target) is { } st)
        {
            if (T.Alice(st) is { } a) pick = T.Pick(a.FindMethods(name).Where(m => m.IsStatic).Select(IlTypes.FromAlice), infos);
            else pick = T.Pick(ClrMethods(st, name, instance: false, explicitArgs), infos);
            if (pick == null) throw No("MCall:unresolved-static", st.Name + "." + name + "/" + call.Args.Count);
        }
        else
        {
            recv = call.Target;
            rt = Infer(recv);
            if (T.Alice(rt) is { } a)
            {
                pick = T.Pick(a.FindMethods(name).Where(m => !m.IsStatic).Select(IlTypes.FromAlice), infos);
                if (pick == null) pick = T.Pick(ClrMethods(a.ClrBase ?? (a.IsValueType ? typeof(ValueType) : typeof(object)), name, instance: true), infos);
            }
            else if (rt.IsPointer || rt == typeof(void)) throw No("MCall:receiver", rt.Name);
            else pick = T.Pick(ClrMethods(rt, name, instance: true, explicitArgs), infos);
            if (pick == null)
            {
                var withRecv = infos.Prepend(new ArgInfo(rt, null, false, "")).ToList();
                var ext = c.Extensions.Where(m => m.Name == name).Select(IlTypes.FromAlice)
                    .Concat(T.ExtensionsNamed(name).Select(m => IlTypes.FromClr(m, extension: true)));
                pick = T.Pick(ext, withRecv);
                if (pick != null) pick = pick with { Extension = true };
            }
            if (pick == null) throw No("MCall:unresolved-instance", rt.Name + "." + name + "/" + call.Args.Count);
        }

        // receiver
        int skip = 0;
        if (pick.Extension && recv != null)
        {
            var p0 = pick.Params[0];
            if (p0.IsByRef) Address(recv); else Emit(recv, p0);
            skip = 1;
        }
        else if (!pick.IsStatic)
        {
            if (recv == null || baseCall) il.EmitLoadArg(0);
            else
            {
                var decl = pick.Alice?.Owner.Proxy ?? pick.Clr!.DeclaringType!;
                if (rt!.IsValueType) Address(recv); else Emit(recv, decl.IsInterface || decl == typeof(object) || T.Assignable(rt, decl) ? rt : decl);
            }
        }
        PushArgs(pick, call.Args, skip);
        if (pick.Alice is { } am)
        {
            bool virt = !am.IsStatic && !baseCall && !am.Owner.IsValueType && !am.IsExtension;
            il.EmitCall(am.Def, virtualCall: virt);
        }
        else
        {
            var mi = pick.Clr!;
            if (pick.Clr is ConstructorInfo) throw No("MCall:ctor", name);
            var ownerT = recv == null || baseCall ? (owner.ClrBase ?? typeof(object)) : rt!;
            if (!mi.IsStatic && rt != null && rt.IsValueType && !pick.Extension && !baseCall)
            {
                if (mi.DeclaringType == rt) il.EmitCall(T.Method(mi)); // il-emit2: a struct method cannot be overridden (ILVerify CallVirtOnValueType)
                else { il.EmitConstrainedPrefix(T.Il(rt)); il.EmitCall(T.Method(mi), virtualCall: true); }
            }
            else il.EmitCall(T.Method(mi), virtualCall: !mi.IsStatic && !baseCall && !(recv == null && owner.IsValueType));
            _ = ownerT;
        }
        var r = pick.Ret;
        if (r.IsByRef && !keepRef) return Deref(r);
        return r;
    }

    IEnumerable<Cand> ClrMethods(Type t, string name, bool instance, Type[]? ex = null)
    {
        if (ex != null)
        {
            var flags0 = BindingFlags.Public | (instance ? BindingFlags.Instance : BindingFlags.Static | BindingFlags.FlattenHierarchy);
            foreach (var m in t.GetMethods(flags0).Where(m => m.Name == name && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == ex.Length))
            {
                MethodInfo closed;
                try { closed = m.MakeGenericMethod(ex); } catch (ArgumentException) { continue; }
                yield return IlTypes.FromClr(closed);
            }
            yield break;
        }
        var flags = BindingFlags.Public | (instance ? BindingFlags.Instance : BindingFlags.Static | BindingFlags.FlattenHierarchy);
        IEnumerable<MethodInfo> ms;
        try { ms = t.GetMethods(flags).Where(m => m.Name == name).ToList(); }
        catch (Exception) { ms = []; }
        if (instance && t.IsInterface)
            ms = ms.Concat(t.GetInterfaces().SelectMany(i => i.GetMethods().Where(m => m.Name == name))).Concat(typeof(object).GetMethods().Where(m => m.Name == name && !m.IsStatic));
        // hide overridden base declarations: keep the most derived per signature
        var seen = new HashSet<string>();
        foreach (var m in ms.OrderBy(m => Depth(t, m.DeclaringType)))
        {
            string sig = string.Join(",", m.GetParameters().Select(p => p.ParameterType.ToString())) + (m.IsGenericMethodDefinition ? "`" + m.GetGenericArguments().Length : "");
            if (seen.Add(sig)) yield return IlTypes.FromClr(m);
        }
    }

    static int Depth(Type t, Type? decl)
    {
        int d = 0;
        for (var b = t; b != null; b = b.BaseType, d++) if (b == decl) return d;
        return 1000;
    }

    /// <summary>Arguments in parameter order: conversions, out/ref addresses, `params` arrays, omitted defaults.</summary>
    void PushArgs(Cand pick, List<MExpr> xs, int skip)
    {
        int origCount = pick.Clr?.GetParameters().Length ?? pick.Alice!.Params.Length;
        int fixedCount = pick.Expanded ? origCount - 1 : int.MaxValue;
        Type? paramsElem = null;
        int i = 0;
        for (; i < xs.Count; i++)
        {
            int pi = i + skip;
            if (pi >= fixedCount)
            {
                paramsElem ??= pick.Params[pi];
                il.EmitLoadConst(xs.Count - i);
                il.EmitNewArray(T.Il(paramsElem));
                for (int k = i; k < xs.Count; k++)
                {
                    il.EmitDup();
                    il.EmitLoadConst(k - i);
                    Emit(xs[k], paramsElem);
                    il.EmitStoreElement(T.Il(paramsElem));
                }
                i = xs.Count;
                break;
            }
            Arg(xs[i], pick.Params[pi]);
        }
        if (pick.Expanded && skip + xs.Count == fixedCount)
        {
            var last = pick.Clr?.GetParameters()[^1].ParameterType ?? pick.Alice!.Params[^1];
            il.EmitLoadConst(0);
            il.EmitNewArray(T.Il(last.GetElementType()!));
        }
        for (int k = 0; k < pick.Optional; k++)
        {
            int pi = skip + xs.Count + k;
            var pt = pick.Params[pi];
            var dv = pick.DefaultsTail[k];
            if (ReferenceEquals(dv, IlTypes.ParamsMarker)) { il.EmitLoadConst(0); il.EmitNewArray(T.Il(pt.GetElementType()!)); continue; }
            if (dv == null || dv is DBNull || dv == Type.Missing) { DefaultValue(pt); continue; }
            if (dv is string s) { il.EmitLoadConst(s); continue; }
            if (dv is bool bv) { il.EmitLoadConst(bv); continue; }
            if (dv is char ch) { il.EmitLoadConst((int)ch); continue; }
            if (dv is MExpr me) { Emit(me, pt); continue; }
            var u = IlTypes.IsNullable(pt) ? Nullable.GetUnderlyingType(pt)! : pt;
            Const(u.IsEnum ? Enum.GetUnderlyingType(u) : u, dv);
            if (u != pt) il.EmitNewObj(T.Method(pt.GetConstructor([u])!));
        }
    }

    void Arg(MExpr x, Type pt)
    {
        if (x is MArg ma)
        {
            string pre = ma.Prefix.Trim();
            if (pre.StartsWith("out") && (pre.Length > 3 || ma.E is MCode { Text: "_" }))
            {
                var el = pt.GetElementType()!;
                if (ma.E is MCode { Text: "_" }) { il.EmitLoadLocalAddress(Temp(el)); return; }
                string nm = ma.E is MCode mc2 ? mc2.Text.Trim() : ((AirLocalRef)ma.E).Name;
                var l = Declare(nm, el);
                il.EmitLoadLocalAddress(l);
                return;
            }
            Address(ma.E);
            return;
        }
        if (pt.IsByRef)
        {
            var el = pt.GetElementType()!;
            var at = Infer(x);
            if (at == el && (x is MCode or AirLocalRef or MMember or MIndex)) { Address(x); return; }
            Emit(x, el);
            var tmp = Temp(el);
            il.EmitStoreLocal(tmp);
            il.EmitLoadLocalAddress(tmp);
            return;
        }
        Emit(x, pt);
    }
}
