using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Lowering;

/// <summary>
/// Allocation-free iteration. `src.map{..}.select{..}.sum` with literal blocks becomes one counted loop:
/// no delegates, no closures, no intermediate arrays, no enumerator boxing (arrays/lists/strings/ranges index directly).
/// </summary>
public sealed partial class Lowerer
{
    sealed record Stage(string Kind, LambdaExpr Block);

    static readonly HashSet<string> StageNames = new() { "map", "select", "reject" };
    static readonly HashSet<string> BlockSinks = new() { "each", "each_with_index", "map", "select", "reject", "sum", "count", "any_q", "all_q", "find", "reduce", "times", "upto", "downto" };
    static readonly HashSet<string> PlainSinks = new() { "sum", "size", "length", "count", "to_a", "any_q", "max", "min", "first" };

    int noHoist;

    MExpr? TryInline(CallExpr c)
    {
        string name = Binder.Mangle(c.Name);
        if (noHoist > 0 || !BlockSinks.Contains(name) || c.Block == null || c.Recv == null) return null;
        if (name is "times" or "upto" or "downto") return CountedLoop(c, name);
        return ParallelOrSerial(c, name);
    }

    MExpr? TryFuseSink(CallExpr c)
    {
        string name = Binder.Mangle(c.Name);
        if (noHoist > 0 || !PlainSinks.Contains(name) || c.Recv is not CallExpr inner || inner.Res != "ext" || inner.Block == null || !StageNames.Contains(inner.Name)) return null;
        if (c.Args.Count > 0) return null;
        return ParallelOrSerial(c, name);
    }

    static bool Pure(LambdaExpr b) => !Contains(b.Body, s => s is ReturnStmt or NextStmt or BreakStmt);

    static bool Contains(List<Stmt> body, Func<Stmt, bool> pred)
    {
        foreach (var s in body)
        {
            if (pred(s)) return true;
            switch (s)
            {
                case IfStmt i when i.Bodies.Any(b => Contains(b, pred)) || (i.Else != null && Contains(i.Else, pred)): return true;
                case WhileStmt w when Contains(w.Body, pred): return true;
                case ForStmt f when Contains(f.Body, pred): return true;
                case CaseStmt cs when cs.Whens.Any(w => Contains(w.Body, pred)) || (cs.Else != null && Contains(cs.Else, pred)): return true;
                case BeginStmt bs when Contains(bs.Body, pred) || bs.Rescues.Any(r => Contains(r.Body, pred)) || (bs.Ensure != null && Contains(bs.Ensure, pred)): return true;
            }
        }
        return false;
    }

    static bool HasValueTail(LambdaExpr b) => b.Body.Count > 0 && b.Body[^1] is ExprStmt es && Binder.ValueOf(es.Expr) != null;

    MExpr? CountedLoop(CallExpr c, string name)
    {
        var b = c.Block!;
        if (b.ParamSyms.Count > 1) return null;
        if (name != "times" && c.Args.Count != 1) return null;
        var recv = Expr(c.Recv!);
        string v = b.ParamSyms.Count == 1 ? b.ParamSyms[0].CsName : Temp("i");
        var loop = new MFor { Var = v };
        switch (name)
        {
            case "times": loop.Start = new MCode("0"); loop.Cmp = "<"; loop.End = recv; break;
            case "upto": loop.Start = recv; loop.Cmp = "<="; loop.End = Expr(c.Args[0].Value!); break;
            default: loop.Start = recv; loop.Cmp = ">="; loop.End = Expr(c.Args[0].Value!); loop.Step = "--"; break;
        }
        ctx.Add("loop");
        loop.Body = Stmts(b.Body, Tail.None, null);
        ctx.RemoveAt(ctx.Count - 1);
        pre.Add(loop);
        return MCode.Empty;
    }

    MExpr? Pipeline(CallExpr sink, string sinkName)
    {
        // collect map/select/reject stages under the sink
        var stages = new List<Stage>();
        Syntax.Expr src = sink.Recv!;
        while (src is CallExpr st && st.Res == "ext" && st.Block != null && StageNames.Contains(st.Name)
               && st.Block.ParamSyms.Count == 1 && Pure(st.Block) && HasValueTail(st.Block) && st.Recv != null)
        {
            stages.Insert(0, new Stage(st.Name, st.Block));
            src = st.Recv;
        }
        if (src.Type.Sym?.SoaOf != null) return SoaPipeline(sink, sinkName, src, stages);
        AType elem = src is RangeExpr r0 ? (r0.A.Type.IsInteger ? r0.A.Type : AType.Int) : Builtins.ElemOf(src.Type);
        if (!elem.IsKnown) return null;
        var sb = sink.Block;
        bool valueSink = sinkName is not ("each" or "each_with_index");
        if (sb != null)
        {
            if (valueSink && (!Pure(sb) || !HasValueTail(sb))) return null;
            int want = sinkName is "each_with_index" or "reduce" ? 2 : 1;
            if (sb.ParamSyms.Count > want) return null;
            if (sinkName == "reduce" && (sink.Args.Count != 1 || sb.ParamSyms.Count != 2)) return null;
        }
        else if (sinkName == "reduce") return null;

        // element type flowing into the sink
        AType cur = elem;
        foreach (var s in stages)
            if (s.Kind == "map") cur = s.Block.Result;
        if (!cur.IsKnown) return null;
        AType sinkVal = sb != null && valueSink ? sb.Result : cur;
        if ((sinkName is "map" or "sum") && !sinkVal.IsKnown) return null;

        // result accumulator
        var before = new List<MStmt>();
        var after = new List<MStmt>();
        string res = Temp("r");
        string? count = null;
        bool preallocMap = sinkName == "map" && stages.All(s => s.Kind == "map") && src is not RangeExpr && src.Type.Kind == "array";
        string? srcTemp = null;
        MExpr srcExpr;
        if (src is RangeExpr) srcExpr = MCode.Empty;
        else
        {
            srcExpr = Expr(src);
            if (srcExpr is not MCode sc || !IsSimple(sc.Text))
            {
                srcTemp = Temp("src");
                before.Add(new MDecl { Name = srcTemp, Init = srcExpr });
                srcExpr = new MCode(srcTemp);
            }
        }
        string curCs = TypeCs(cur);
        switch (sinkName)
        {
            case "map":
                if (preallocMap)
                {
                    before.Add(new MDecl { Name = res, Init = new MNewArray { ElemCs = TypeCs(sinkVal), Size = new MMember { Target = srcExpr, Name = "Length" } } });
                    count = Temp("n");
                    before.Add(new MDecl { Name = count, TypeCs = "int", Init = new MCode("0") });
                }
                else before.Add(new MDecl { Name = res, Init = new MNew { TypeCs = $"global::System.Collections.Generic.List<{TypeCs(sinkVal)}>" } });
                break;
            case "select":
            case "reject":
            case "to_a":
                before.Add(new MDecl { Name = res, Init = new MNew { TypeCs = $"global::System.Collections.Generic.List<{curCs}>" } });
                break;
            case "sum":
                before.Add(new MDecl { Name = res, TypeCs = TypeCs(sinkVal), Init = new MCode($"default({TypeCs(sinkVal)})") });
                break;
            case "count":
            case "size":
            case "length":
                before.Add(new MDecl { Name = res, TypeCs = "int", Init = new MCode("0") });
                break;
            case "any_q":
                before.Add(new MDecl { Name = res, TypeCs = "bool", Init = new MCode("false") });
                break;
            case "all_q":
                before.Add(new MDecl { Name = res, TypeCs = "bool", Init = new MCode("true") });
                break;
            case "find":
                before.Add(new MDecl { Name = res, TypeCs = curCs + (cur.Nullable ? "" : "?"), Init = new MCode("default") });
                break;
            case "first":
            case "max":
            case "min":
                count = Temp("has");
                before.Add(new MDecl { Name = res, TypeCs = curCs, Init = new MCode("default!") });
                before.Add(new MDecl { Name = count, TypeCs = "bool", Init = new MCode("false") });
                after.Add(new MIf { Cond = new MUnary { Op = "!", E = new MCode(count) }, Then = { new MThrow { Value = new MNew { TypeCs = "global::System.InvalidOperationException", Args = { new MCode("\"Sequence contains no elements\"") } } } } });
                break;
            case "reduce":
                before.Add(new MDecl { Name = res, TypeCs = TypeCs(sb!.ParamSyms[0].Type.IsKnown ? sb.ParamSyms[0].Type : sink.Args[0].Value!.Type), Init = Expr(sink.Args[0].Value!) });
                break;
            case "each_with_index":
                count = Temp("i");
                before.Add(new MDecl { Name = count, TypeCs = "int", Init = new MCode("-1") });
                break;
        }

        // loop body: stages, then sink
        var body = new List<MStmt>();
        string loopVar = Temp("e");
        MExpr element = new MCode(loopVar);
        if (sinkName == "each_with_index") body.Add(new MExprStmt { E = new MUnary { Op = "++", E = new MCode(count!), Postfix = true } });
        ctx.Add("loop");
        // Filters nest the rest of the body in `if (cond) { ... }` (not `continue`) so the JIT can if-convert to cmov.
        var target = body;
        foreach (var s in stages)
        {
            target.Add(new MDecl { Name = s.Block.ParamSyms[0].CsName, Init = element });
            var (stmts, v) = BlockValue(s.Block);
            target.AddRange(stmts);
            if (s.Kind == "map")
            {
                string m = Temp("m");
                target.Add(new MDecl { Name = m, Init = v });
                element = new MCode(m);
            }
            else
            {
                var gate = new MIf { Cond = s.Kind == "select" ? v : new MUnary { Op = "!", E = v } };
                target.Add(gate);
                target = gate.Then;
            }
        }
        Sink(target, sinkName, sb, element, res, count, preallocMap);
        ctx.RemoveAt(ctx.Count - 1);

        // SIMD prologue for numeric arrays; the scalar loop below finishes the remaining tail
        var vec = src is RangeExpr ? null : TryVectorize(src, srcExpr, elem, stages, sinkName, sb, res, count, preallocMap);
        if (vec != null) before.AddRange(vec.Value.Stmts);

        // the loop itself
        MStmt loop;
        if (src is RangeExpr r)
            loop = new MFor { Var = loopVar, Start = Expr(r.A), Cmp = r.Excl ? "<" : "<=", End = Expr(r.B), Body = body };
        else if (IsList(src.Type) && vec == null && boundLo == null && boundHi == null && Method is not { IsAsync: true }
                 && ListStable(stages.Select(s => s.Block).Append(sb)))
        {
            // the body cannot add/remove/replace the list's storage: iterate its backing span (no version/bounds checks)
            string span = Temp("sp"), idx = Temp("k");
            before.Add(new MDecl { Name = span, Init = new MCode($"global::System.Runtime.InteropServices.CollectionsMarshal.AsSpan({((MCode)srcExpr).Text})") });
            body.Insert(0, new MDecl { Name = loopVar, Init = new MIndex { Target = new MCode(span), Args = { new MCode(idx) } } });
            loop = new MFor { Var = idx, Start = new MCode("0"), Cmp = "<", End = new MCode(span + ".Length"), Body = body };
        }
        else if (src.Type.Kind == "array" || src.Type.IsString || IsList(src.Type))
        {
            string idx = Temp("k");
            body.Insert(0, new MDecl { Name = loopVar, Init = new MIndex { Target = srcExpr, Args = { new MCode(idx) } } });
            loop = new MFor
            {
                Var = idx,
                Start = new MCode(vec?.Index ?? boundLo ?? "0"),
                Cmp = "<",
                End = boundHi != null ? new MCode(boundHi) : new MMember { Target = srcExpr, Name = src.Type.Kind == "array" || src.Type.IsString ? "Length" : "Count" },
                Body = body,
            };
        }
        else loop = new MForeach { Vars = { loopVar }, Coll = srcExpr, Body = body };

        pre.AddRange(before);
        pre.Add(loop);
        pre.AddRange(after);
        switch (sinkName)
        {
            case "each":
            case "each_with_index":
                return MCode.Empty;
            case "map":
                return preallocMap ? new MCode(res) : new MCall { Target = new MCode(res), Name = "ToArray" };
            case "select":
            case "reject":
            case "to_a":
                return new MCall { Target = new MCode(res), Name = "ToArray" };
        }
        return new MCode(res);
    }

    /// <summary>
    /// True when no block can change a List's storage: calls are limited to pure Alice methods, statics on
    /// value/BCL math types, value-type or string receivers, and Prelude output; no call takes a collection.
    /// </summary>
    bool ListStable(IEnumerable<LambdaExpr?> blocks)
    {
        bool ok = true;
        foreach (var b in blocks)
        {
            if (b == null) continue;
            AstWalk.Each(b.Body, n =>
            {
                if (!ok || n is not CallExpr c) return;
                if (c.Args.Any(a => a.Value?.Type is { } at && (IsList(at) || at.Kind == "array" || at.Clr is { } ac && typeof(System.Collections.IEnumerable).IsAssignableFrom(ac) && ac != typeof(string)))) { ok = false; return; }
                ok = c.Res switch
                {
                    "alice" => c.Sym is MethodSym m && MethodPure(m),
                    "free" => c.Name is "puts" or "print" or "p" or "format",
                    "clr" or "ext" or "field" => c.Recv is ConstExpr || c.Recv?.Type is { } rt && (rt.Kind is "prim" or "struct" or "enum" || rt.IsString),
                    _ => false,
                };
            });
        }
        return ok;
    }

    static bool IsList(AType t) => t.Clr is { IsGenericType: true } c && c.GetGenericTypeDefinition() == typeof(List<>);

    /// <summary>Lowers a block's leading statements and returns its tail value expression.</summary>
    (List<MStmt>, MExpr) BlockValue(LambdaExpr b)
    {
        var stmts = new List<MStmt>();
        for (int i = 0; i < b.Body.Count - 1; i++) Into(stmts, b.Body[i], Tail.None, null);
        var v = Isolated(((ExprStmt)b.Body[^1]).Expr, stmts);
        return (stmts, v);
    }

    void Sink(List<MStmt> body, string name, LambdaExpr? sb, MExpr el, string res, string? count, bool prealloc)
    {
        if (sb != null && name != "reduce" && sb.ParamSyms.Count >= 1) body.Add(new MDecl { Name = sb.ParamSyms[0].CsName, Init = el });
        if (name == "each_with_index" && sb != null && sb.ParamSyms.Count == 2) body.Add(new MDecl { Name = sb.ParamSyms[1].CsName, Init = new MCode(count!) });
        if (name == "reduce")
        {
            body.Add(new MDecl { Name = sb!.ParamSyms[0].CsName, Init = new MCode(res) });
            body.Add(new MDecl { Name = sb.ParamSyms[1].CsName, Init = el });
        }
        if (name is "each" or "each_with_index")
        {
            body.AddRange(Stmts(sb!.Body, Tail.None, null));
            return;
        }
        MExpr v = el;
        if (sb != null)
        {
            var (stmts, tail) = BlockValue(sb);
            body.AddRange(stmts);
            v = tail;
        }
        switch (name)
        {
            case "map":
                if (prealloc) body.Add(new MAssign { Target = new MIndex { Target = new MCode(res), Args = { new MUnary { Op = "++", E = new MCode(count!), Postfix = true } } }, Value = v });
                else body.Add(new MExprStmt { E = new MCall { Target = new MCode(res), Name = "Add", Args = { v } } });
                break;
            case "select":
                body.Add(new MIf { Cond = v, Then = { new MExprStmt { E = new MCall { Target = new MCode(res), Name = "Add", Args = { el } } } } });
                break;
            case "reject":
                body.Add(new MIf { Cond = new MUnary { Op = "!", E = v }, Then = { new MExprStmt { E = new MCall { Target = new MCode(res), Name = "Add", Args = { el } } } } });
                break;
            case "to_a":
                body.Add(new MExprStmt { E = new MCall { Target = new MCode(res), Name = "Add", Args = { el } } });
                break;
            case "sum":
                body.Add(new MAssign { Target = new MCode(res), Op = "+=", Value = v });
                break;
            case "count":
            case "size":
            case "length":
                if (sb != null) body.Add(new MIf { Cond = v, Then = { new MExprStmt { E = new MUnary { Op = "++", E = new MCode(res), Postfix = true } } } });
                else body.Add(new MExprStmt { E = new MUnary { Op = "++", E = new MCode(res), Postfix = true } });
                break;
            case "any_q":
                body.Add(new MIf { Cond = sb != null ? v : new MCode("true"), Then = { new MAssign { Target = new MCode(res), Value = new MCode("true") }, new MBreak() } });
                break;
            case "all_q":
                body.Add(new MIf { Cond = new MUnary { Op = "!", E = v }, Then = { new MAssign { Target = new MCode(res), Value = new MCode("false") }, new MBreak() } });
                break;
            case "find":
                body.Add(new MIf { Cond = v, Then = { new MAssign { Target = new MCode(res), Value = el }, new MBreak() } });
                break;
            case "first":
                body.Add(new MAssign { Target = new MCode(res), Value = el });
                body.Add(new MAssign { Target = new MCode(count!), Value = new MCode("true") });
                body.Add(new MBreak());
                break;
            case "max":
            case "min":
                body.Add(new MIf
                {
                    Cond = new MBinary { Op = "||", L = new MUnary { Op = "!", E = new MCode(count!) }, R = new MBinary { Op = name == "max" ? ">" : "<", L = el, R = new MCode(res) } },
                    Then = { new MAssign { Target = new MCode(res), Value = el }, new MAssign { Target = new MCode(count!), Value = new MCode("true") } },
                });
                break;
            case "reduce":
                body.Add(new MAssign { Target = new MCode(res), Value = v });
                break;
        }
    }
}
