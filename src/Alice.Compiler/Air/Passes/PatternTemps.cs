using Alice.Compiler.Mir;
using Alice.Compiler.Optimizer;

namespace Alice.Compiler.Air.Passes;

/// <summary>
/// Declaration patterns without Roslyn. `e is T x` (destructuring, `in Some(x)` arms) becomes an explicit `isinst`
/// into a typed local: `(e is T) &amp;&amp; { x = (T)e; true }` (with e in a temp first when it is not a local), or
/// `{ x = e as T; x } != null` for a known reference type; `e is var x` becomes `{ x = e; true }`. The bound locals
/// are declared before the statement that tests them, with C#'s scopes: in the enclosing block (an `if` included, so
/// `if (!(o is T x)) return;` keeps x), in a scope block around a while/for whose condition binds; inside an
/// expression-bodied lambda the lambda gets a block body.
/// References to x are left as they are: x now is a plain typed local. Plain `is T` / `is null` tests stay.
/// </summary>
public sealed partial class PatternTemps(IlLowerContext? ctx = null) : IlPass(ctx)
{
    public override string Name => "il-pattern-temps";

    static readonly Alice.Core.Text.Regex VarPattern = new(@"\Avar (@?[A-Za-z_][A-Za-z0-9_]*)\Z");

    static readonly Alice.Core.Text.Regex DeclPattern = new(@"\A(\S.*) (@?[A-Za-z_][A-Za-z0-9_]*)\Z");

    static readonly HashSet<string> NotTypes = ["not", "and", "or", "null", "var", "true", "false"];

    protected override List<MStmt>? Lower(MStmt s)
    {
        var c = new Collector(this, s);
        MapRoots(s, c.Go);
        if (c.Decls.Count == 0) return null;
        var decls = c.Decls.Select(d => (MStmt)d.Decl).ToList();
        // C# scopes a pattern variable of a loop condition to the loop, of anything else (if included) to the block
        return s is MWhile or MFor ? [new MBlock { Body = [.. decls, s] }] : [.. decls, s];
    }

    sealed record Pending(MDecl Decl, MExpr Node);

    /// <summary>Lowers the patterns of one statement's own expressions and collects their declarations.</summary>
    sealed class Collector(PatternTemps pass, MStmt owner) : MirRewriter
    {
        public readonly List<Pending> Decls = new();

        public MExpr Go(MExpr e) => Expr(e);

        protected override MExpr Rewrite(MExpr e)
        {
            if (e is MIs i && pass.Pattern(i, owner, this) is { } lowered) return lowered;
            if (e is MLambda { ExprBody: { } eb } l) Lambda(l, eb);
            return e;
        }

        public void Add(MDecl d, MExpr node) => Decls.Add(new Pending(d, node));

        /// <summary>Declarations made inside an expression-bodied lambda belong to the lambda, not the statement.</summary>
        void Lambda(MLambda l, MExpr body)
        {
            var mine = Decls.Where(p => Contains(body, p.Node)).ToList();
            if (mine.Count == 0) return;
            bool? isVoid = body.Type is AirType.Prim { Kind: PrimKind.Void } ? true
                : l.Type is AirType.ClrRef { Name: var n } ? n == "global::System.Action" ? true : n == "global::System.Func" ? false : null
                : IlTypes.Known(body.Type) ? false : null;
            if (isVoid == null)
            {
                pass.Unsupported(owner, "pattern binding inside an expression-bodied lambda of unknown type");
                return;
            }
            foreach (var p in mine) Decls.Remove(p);
            l.ExprBody = null;
            l.Body = [.. mine.Select(p => (MStmt)p.Decl), isVoid == true ? Do(body) : new MReturn { Value = body }];
        }
    }

    static bool Contains(MExpr root, MExpr node)
    {
        var f = new Finder(node);
        f.Find(root);
        return f.Found;
    }

    sealed class Finder(MExpr node) : MirRewriter
    {
        public bool Found;
        public void Find(MExpr e) => Expr(e);
        protected override MExpr Rewrite(MExpr e)
        {
            if (ReferenceEquals(e, node)) Found = true;
            switch (e)
            {
                case AirSeq q:
                    foreach (var st in q.Pre) MapRoots(st, x => { Find(x); return x; });
                    Find(q.Value);
                    break;
                case AirAs a:
                    Find(a.E);
                    break;
            }
            return e;
        }
    }

    MExpr? Pattern(MIs i, MStmt owner, Collector c)
    {
        string p = i.TypeCs;
        if (VarPattern.Match(p) is { } vm)
        {
            if (!IlTypes.Known(i.E.Type))
            {
                Unsupported(owner, $"`is var {vm[1]}` on a value of unknown type");
                return null;
            }
            string x = vm[1]!;
            var seq = new AirSeq { Pre = [new MAssign { Target = Local(x, i.E.Type), Value = i.E }], Value = Const(true), Type = IlTypes.Bool };
            c.Add(Decl(x, i.E.Type!, null), seq);
            return seq;
        }
        if (DeclPattern.Match(p) is not { } dm) return null;
        string ty = dm[1]!, name = dm[2]!;
        if (NotTypes.Contains(ty) || NotTypes.Contains(name) || ty.EndsWith(" not") || ty.Contains('{')) return null;
        var bound = new AirType.ClrRef(ty, null, []);
        MExpr result;
        if (KnownReference(ty))
        {
            // { x = e as T; x } != null
            var seq = new AirSeq { Pre = [new MAssign { Target = Local(name, bound), Value = new AirAs { TypeCs = ty, E = i.E, Type = bound } }], Value = Local(name, bound), Type = bound };
            result = new MBinary { Op = AirBinOp.Ne, L = seq, R = Const(null), Type = IlTypes.Bool };
        }
        else
        {
            // (e is T) && { x = (T)e; true }, e read once
            MExpr subject = i.E;
            Func<MExpr> again = () => Reread(i.E);
            MDecl? temp = null;
            if (!IsSimple(subject))
            {
                string t = Ctx.Temp("p");
                var tt = IlTypes.Known(i.E.Type) ? i.E.Type! : IlTypes.Obj;
                temp = Decl(t, tt, null);
                subject = new AirSeq { Pre = [new MAssign { Target = Local(t, tt), Value = i.E }], Value = Local(t, tt), Type = tt };
                again = () => Local(t, tt);
            }
            var test = new MIs { E = subject, TypeCs = ty, Type = IlTypes.Bool };
            var bind = new AirSeq { Pre = [new MAssign { Target = Local(name, bound), Value = new MCast { TypeCs = ty, E = again(), Type = bound } }], Value = Const(true), Type = IlTypes.Bool };
            result = new MBinary { Op = AirBinOp.And, L = test, R = bind, Type = IlTypes.Bool };
            if (temp != null) c.Add(temp, result);
        }
        c.Add(new MDecl { Name = name, TypeCs = ty }, result);
        return result;
    }


    static bool KnownReference(string ty) => ty is "string" or "object" || ty.EndsWith("[]");
}
