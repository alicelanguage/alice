using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Lowering;

/// <summary>
/// MLton-style specialization of higher-order functions (ADR 0024). A call to a user function with a lambda literal
/// for a function parameter (a block or a `Func`) that the callee only ever calls is lowered to a clone:
/// <code>
/// apply_n(10) { |v| v * k }   =>   apply_n__spec1(10, k)
/// </code>
/// `apply_n__spec1` is the callee lowered again with every `yield v` / `f.call(v)` replaced by a direct call to a
/// static method holding the lambda body, `__lam_N(v, k)`. No delegate is created or invoked, captures travel as
/// extra parameters (by `ref` when a lambda assigns them, so Ruby closure semantics hold), and the JIT can inline the
/// body into the clone's loop. The program is a closed world: every callee's source is known.
///
/// Scope: static callee and caller in the same type and file, no receiver, positional arguments only, a callee that
/// is not generic, async, an iterator or recursive through the function parameter. Clones are capped per callee and
/// by callee size. Capture-free lambdas with the same text share one clone. Evaluation order is the call's own:
/// arguments left to right, then the body, which calls the lambda method exactly where it invoked the delegate.
/// </summary>
public sealed partial class Lowerer
{
    public const string SpecSuffix = "__spec";
    const int MaxClonesPerCallee = 8;
    const int MaxCalleeNodes = 400;

    /// <summary>A clone to lower after the current method: the callee and, per function parameter, the lambda method.</summary>
    sealed class SpecRequest
    {
        public MethodSym Callee = null!;
        public string Name = "";
        public readonly Dictionary<ParamSym, (string Lam, List<int> Caps)> Fns = new();
        public readonly List<(LocalSym L, bool Ref)> Caps = new();
    }

    readonly List<SpecRequest> pendingSpecs = new();
    readonly Dictionary<string, string> specByKey = new();
    readonly Dictionary<MethodSym, int> specCount = new();
    readonly Dictionary<(MethodSym, ParamSym), bool> specEligible = new();
    SpecRequest? specializing;

    /// <summary>`ALICE_NO_SPECIALIZE=1` turns the pass off (benchmarks, bisecting).</summary>
    static readonly bool SpecializeOff = Environment.GetEnvironmentVariable("ALICE_NO_SPECIALIZE") == "1";

    static bool StaticMethod(MethodSym m) => m.IsStatic || m.Owner.Kind is "module" or "program";

    /// <summary>The call `m(args)` as a call to a specialized clone, or null when it stays as it is.</summary>
    MExpr? TrySpecialize(CallExpr c, MethodSym m, List<MExpr> args, MExpr? recv)
    {
        if (SpecializeOff || specializing != null || soa != null || inParallelGen || recv != null || c.Recv != null || c.Safe) return null;
        if (m.Decl is not { HasBody: true } d || d.File != File || m.Owner != Type || !StaticMethod(m)) return null;
        if (Method != null ? !StaticMethod(Method) || Method.Owner != Type : Type.Kind != "program") return null;
        if (m.TypeParams.Count > 0 || m.HasInferredGenerics || m.Instances.Count > 0 || m.IsAsync || m.IsIterator || m.IsCtor) return null;
        if (!(m.Ret.IsVoid || m.Ret.IsKnown) || m.Params.Any(p => p.Splat || p.Mod != "" || p.Inferred)) return null;
        if (c.Args.Any(a => a.Name != null || a.Mod != "" || a.Value == null)) return null;
        int positional = m.Params.Count(p => !p.Block);
        bool hasBlock = m.Params.Any(p => p.Block);
        if (c.Args.Count != positional || args.Count != positional + (c.Block != null ? 1 : 0) || hasBlock != (c.Block != null)) return null;
        if (hasBlock && !m.Params[^1].Block) return null;

        // which arguments are lambda literals for parameters the callee only calls
        var lams = new List<(int Index, ParamSym P, LambdaExpr Lam, MLambda Ml)>();
        for (int i = 0; i < m.Params.Count; i++)
        {
            var p = m.Params[i];
            var lam = p.Block ? c.Block : c.Args[i].Value as LambdaExpr;
            if (lam == null || args[i] is not MLambda ml || lam.MethodRef != null) continue;
            if (!lam.ParamSyms.All(s => s.Type.IsKnown) || lam.ReturnsValue && !lam.Result.IsKnown) continue;
            if (!OnlyCalled(m, p)) continue;
            lams.Add((i, p, lam, ml));
        }
        if (lams.Count == 0) return null;

        // captured locals, and whether any of them must be shared by reference
        var req = new SpecRequest { Callee = m };
        var keys = new List<string>();
        var lamDefs = new List<(ParamSym P, string Ret, string Ps, string Body, List<int> Caps)>();
        foreach (var (_, p, lam, ml) in lams)
        {
            var caps = lam.Captures ? Captured(lam) : [];
            if (caps == null) return null;
            var idx = new List<int>();
            foreach (var l in caps)
            {
                int k = req.Caps.FindIndex(x => x.L == l);
                bool byRef = AssignedInAnyLambda(l);
                if (k < 0) { k = req.Caps.Count; req.Caps.Add((l, byRef)); }
                idx.Add(k);
            }
            var ps = new List<string>();
            for (int j = 0; j < ml.Params.Count; j++)
                ps.Add(ml.Params[j].Contains(' ') ? ml.Params[j] : TypeCs(lam.ParamSyms[j].Type) + " " + ml.Params[j]);
            ps.AddRange(idx.Select(k => (req.Caps[k].Ref ? "ref " : "") + LocalTypeCs(req.Caps[k].L) + " " + req.Caps[k].L.CsName));
            var ret = DelegateResult(p.Type) ?? (lam.ReturnsValue && !lam.DiscardValue ? lam.Result : AType.Void);
            string body;
            if (ml.ExprBody != null) body = " => " + Backend.Printer.E(ml.ExprBody) + ";\n";
            else
            {
                var hp = new Backend.Printer("", 1);
                foreach (var s in ml.Body!) hp.Root(s);
                body = "\n{\n" + hp + "}\n";
            }
            lamDefs.Add((p, ret.IsVoid ? "void" : TypeCs(ret), string.Join(", ", ps), body, idx));
            // the dedup key spells parameters and captures by position, so `{ |v| v * 2 }` twins match
            var names = lam.ParamSyms.Select(x => x.CsName).Concat(idx.Select(k => req.Caps[k].L.CsName)).ToList();
            string norm = (ret.IsVoid ? "void" : TypeCs(ret)) + "(" + string.Join(", ", ps) + ")" + body;
            for (int j = 0; j < names.Count; j++)
                norm = System.Text.RegularExpressions.Regex.Replace(norm, $@"(?<![\w@]){System.Text.RegularExpressions.Regex.Escape(names[j])}(?!\w)", "\u0001" + j);
            keys.Add(p.CsName + ":" + norm + ":" + string.Join(",", idx));
        }
        if (req.Caps.Any(x => x.Ref) && (ctx.Contains("lambda") || Method is { IsAsync: true } || Method is { IsIterator: true } || HasLambda(m.Decl.Body))) return null;

        // one clone per distinct lambda text: capture-free twins share it
        string key = m.CsName + "|" + string.Join("|", keys) + "|" + string.Join(",", req.Caps.Select(x => (x.Ref ? "ref " : "") + LocalTypeCs(x.L)));
        if (!specByKey.TryGetValue(key, out var name))
        {
            int n = specCount.GetValueOrDefault(m);
            if (n >= MaxClonesPerCallee) return null;
            specCount[m] = ++n;
            name = m.CsName.TrimStart('@') + SpecSuffix + n;
            specByKey[key] = name;
            req.Name = name;
            foreach (var (p, ret, ps, body, idx) in lamDefs)
            {
                string lamName = $"__lam_{name}_{p.CsName.TrimStart('@')}";
                Extra.Add("[global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]\n"
                    + $"private static {ret} {lamName}({ps})" + body);
                req.Fns[p] = (lamName, idx);
            }
            pendingSpecs.Add(req);
        }
        var call = new MCall { Name = name };
        for (int i = 0; i < args.Count; i++)
            if (!lams.Any(l => l.Index == i)) call.Args.Add(args[i]);
        foreach (var (l, byRef) in req.Caps) call.Args.Add(byRef ? new MArg { Prefix = "ref ", E = new MCode(l.CsName) } : new MCode(l.CsName));
        return call;
    }

    static AType? DelegateResult(AType t) => t.Kind == "func" && t.Args.Count > 0 ? t.Args[^1] : null;

    /// <summary>Every mention of <paramref name="p"/> in the callee is a call of it: `yield`, `p.call(..)`, `p.(..)`.</summary>
    bool OnlyCalled(MethodSym m, ParamSym p)
    {
        if (specEligible.TryGetValue((m, p), out var ok)) return ok;
        int nodes = 0, calls = 0;
        var asTarget = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        bool bad = false;
        AstWalk.Each(m.Decl!.Body, n =>
        {
            nodes++;
            switch (n)
            {
                case InvokeExpr { Target: NameExpr { Res: "local" } t } when t.Sym == p.Local: asTarget.Add(t); calls++; break;
                case YieldExpr y when y.Sym == p: calls++; break;
                case CallExpr ce when ce.Name is "block_given?" || ce.Block?.MethodRef != null: bad = true; break;
                case Syntax.Expr e when e.Sym is ParamSym ps && ps == p && e is not YieldExpr: bad = true; break;
            }
        });
        AstWalk.Each(m.Decl!.Body, n =>
        {
            if (n is NameExpr ne && p.Local != null && ne.Sym == p.Local && !asTarget.Contains(ne)) bad = true;
            if (n is Assign { Declares: { } dl } && dl == p.Local) bad = true;
        });
        ok = !bad && calls > 0 && nodes <= MaxCalleeNodes && p.Local != null;
        specEligible[(m, p)] = ok;
        return ok;
    }

    static bool HasLambda(List<Stmt> body)
    {
        bool any = false;
        AstWalk.Each(body, n => any |= n is LambdaExpr or SpawnExpr);
        return any;
    }

    /// <summary>The caller's locals the lambda reads, or null when it reaches anything else it cannot be passed.</summary>
    List<LocalSym>? Captured(LambdaExpr lam)
    {
        int depth = LambdaDepthOf(lam);
        if (depth < 0) return null;
        var caps = new List<LocalSym>();
        bool bad = false;
        AstWalk.Each(lam.Body, n =>
        {
            switch (n)
            {
                case SelfExpr or IvarExpr or SuperExpr: bad = true; break;
                case NameExpr { Res: "local", Sym: LocalSym l } when l.LambdaDepth < depth:
                    if (!caps.Contains(l)) caps.Add(l);
                    if (!l.Type.IsKnown || stackArrays.ContainsKey(l)) bad = true;
                    break;
            }
        });
        return bad ? null : caps;
    }

    /// <summary>The binder's lambda depth of locals declared in <paramref name="lam"/>: one more than its enclosing lambdas.</summary>
    int LambdaDepthOf(LambdaExpr lam)
    {
        if (lam.ParamSyms.Count > 0) return lam.ParamSyms[0].LambdaDepth;
        List<Stmt>? root = Method?.Decl?.Body ?? currentMain;
        if (root == null) return -1;
        int found = -1;
        void Visit(List<Stmt> body, int depth)
        {
            AstWalk.Each(body, n =>
            {
                if (found >= 0 || n is not LambdaExpr l) return;
                if (ReferenceEquals(l, lam)) found = depth + 1;
            });
        }
        // count enclosing lambdas: a lambda is enclosing when the target is found inside its body
        int enclosing = 0;
        AstWalk.Each(root, n =>
        {
            if (n is LambdaExpr l && !ReferenceEquals(l, lam))
            {
                bool inside = false;
                AstWalk.Each(l.Body, x => inside |= ReferenceEquals(x, lam));
                if (inside) enclosing++;
            }
        });
        Visit(root, enclosing);
        return found;
    }

    /// <summary>Top-level statements being lowered (Main): the root to search when there is no method.</summary>
    List<Stmt>? currentMain;

    /// <summary>A local assigned inside any lambda of the current body: a by-value copy could miss the write.</summary>
    bool AssignedInAnyLambda(LocalSym l)
    {
        List<Stmt>? root = Method?.Decl?.Body ?? currentMain;
        if (root == null) return true;
        bool hit = false;
        AstWalk.Each(root, n =>
        {
            if (n is not LambdaExpr lam) return;
            AstWalk.Each(lam.Body, x =>
            {
                if (x is Assign { Target: NameExpr { Sym: LocalSym t } } && t == l) hit = true;
                if (x is MultiAssign ma && ma.Locals.Contains(l)) hit = true;
            });
        });
        return hit;
    }

    /// <summary>`yield v` / `f.call(v)` inside a clone: a direct call of the lambda method, captures appended.</summary>
    MExpr? SpecializedInvoke(ParamSym? p, List<Arg> args)
    {
        if (specializing == null || p == null || !specializing.Fns.TryGetValue(p, out var fn)) return null;
        var call = new MCall { Name = fn.Lam, Args = Args(args) };
        foreach (int k in fn.Caps)
        {
            var (_, byRef) = specializing.Caps[k];
            MExpr v = new MCode("__cap" + k);
            call.Args.Add(byRef ? new MArg { Prefix = "ref ", E = v } : v);
        }
        return call;
    }

    ParamSym? ParamOf(Syntax.Expr target) =>
        specializing != null && target is NameExpr { Res: "local", Sym: LocalSym l } ? specializing.Callee.Params.FirstOrDefault(p => p.Local == l) : null;

    /// <summary>Lowers the clones the method just lowered asked for; returns their C# text.</summary>
    public string FlushSpecializations()
    {
        if (pendingSpecs.Count == 0) return "";
        var sb = new System.Text.StringBuilder();
        var (savedMethod, savedType, savedCloneOk, savedForked) = (Method, Type, CloneOk, Forked);
        var savedExtra = new List<string>(Extra);
        Extra.Clear();
        try
        {
            while (pendingSpecs.Count > 0)
            {
                var req = pendingSpecs[0];
                pendingSpecs.RemoveAt(0);
                var m = req.Callee;
                var d = m.Decl!;
                Method = m;
                Type = m.Owner;
                CloneOk = false;
                specializing = req;
                bool returns = !m.Ret.IsVoid;
                var body = LowerBody(d.Body, returns ? Tail.Return : Tail.None, d.HoistedLocals, d.Line);
                specializing = null;
                Optimizer.Pipeline.Run(body);
                var pr = new Backend.Printer("", 1);
                pr.Stmts(body);
                var ps = m.Params.Where(p => !req.Fns.ContainsKey(p)).Select(p => $"{p.Type.Cs} {p.CsName}").ToList();
                ps.AddRange(req.Caps.Select((x, k) => $"{(x.Ref ? "ref " : "")}{LocalTypeCs(x.L)} __cap{k}"));
                sb.Append(string.Concat(Extra));
                Extra.Clear();
                sb.Append($"private static {(returns ? TypeCs(m.Ret) : "void")} {req.Name}({string.Join(", ", ps)})\n{{\n{pr}}}\n");
            }
        }
        finally
        {
            specializing = null;
            (Method, Type, CloneOk, Forked) = (savedMethod, savedType, savedCloneOk, savedForked);
            Extra.Clear();
            Extra.AddRange(savedExtra);
        }
        return sb.ToString();
    }
}
