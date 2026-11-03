using Alice.Compiler;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;
using Syntax = Alice.Compiler.Syntax;
using Term = Alice.Check.Term;
using Op = Alice.Check.Op;
using Goal = Alice.Check.Goal;

namespace Alice.Cli.Proof;

/// <summary>A rule outside the translatable subset: never half-proven (ADR 0022).</summary>
public sealed class Untranslatable(string why) : Exception(why);

/// <summary>One rule as a claim (<see cref="Goal"/>) over its inputs; <c>Signed</c> says how to print an input.</summary>
public sealed record Translation(MethodDecl Rule, List<(RuleDomain Domain, Term Var, bool Signed)> Inputs, Goal Goal);

/// <summary>
/// The symbolic execution of `alice prove --smt` (ADR 0022), shared by the Z3 query and the certificate (ADR 0042):
/// a rule's body run over bit vectors with the semantics the compiler gives it (floored `/` and `%`, checked `+ - *`,
/// wrapping `&+ &- &*`); branches and early returns become if-then-else terms of the proof IR (Alice.Check). A
/// construct outside that subset is <see cref="Untranslatable"/>.
/// </summary>
public static class Symbolic
{
    /// <summary>Binds the sources and translates every rule.</summary>
    public static List<(MethodDecl Rule, Translation? T, string? Untranslatable)> Translate(List<SourceFile> files, IEnumerable<string> refs)
    {
        var asts = files.Select(f => Parser.ParseFile(f.Text, f.Path)).ToList();
        new Binder(asts, new ClrTypes(refs)).Bind();
        var rules = asts.SelectMany(a => Methods(a.Items)).Where(m => m.RuleName != null).ToList();
        var methods = asts.SelectMany(a => Methods(a.Items)).Where(m => m.Sym != null).ToDictionary(m => (MethodSym)m.Sym!, m => m);
        var r = new List<(MethodDecl, Translation?, string?)>();
        foreach (var rule in rules)
        {
            try { r.Add((rule, new Exec(methods).Run(rule), null)); }
            catch (Untranslatable u) { r.Add((rule, null, u.Message)); }
        }
        return r;
    }

    static IEnumerable<MethodDecl> Methods(IEnumerable<Item> items)
    {
        foreach (var it in items)
        {
            if (it is MethodDecl m) yield return m;
            if (it is TypeDecl t) foreach (var x in Methods(t.Members)) yield return x;
            if (it is NamespaceItem n) foreach (var x in Methods(n.Items)) yield return x;
        }
    }

    // ---- term constructors (named as in Z3's binding, which the executor was first written against) ----

    static readonly Term True = new(Op.Bool, 0, [], 1), False = new(Op.Bool, 0, [], 0);
    static Term MkTrue() => True;
    static Term MkFalse() => False;
    static Term MkBool(bool v) => v ? True : False;
    static Term MkBoolConst(string n) => new(Op.Var, 0, [], 0, n);
    static Term MkBVConst(string n, uint w) => new(Op.Var, w, [], 0, n);
    static Term MkBV(long v, uint w) => new(Op.Bv, w, [], w >= 64 ? v : v & ((1L << (int)w) - 1));
    static Term MkNot(Term a) => a.Op == Op.Bool ? MkBool(a.K == 0) : new(Op.Not, 0, [a]);
    static Term MkAnd(params Term[] xs)
    {
        var l = xs.Where(x => x != True).ToArray();
        return l.Any(x => x == False) ? False : l.Length == 0 ? True : l.Length == 1 ? l[0] : new(Op.And, 0, l);
    }
    static Term MkOr(params Term[] xs)
    {
        var l = xs.Where(x => x != False).ToArray();
        return l.Any(x => x == True) ? True : l.Length == 0 ? False : l.Length == 1 ? l[0] : new(Op.Or, 0, l);
    }
    static Term MkEq(Term a, Term b) => new(Op.Eq, 0, [a, b]);
    static Term MkITE(Term c, Term a, Term b) => c == True ? a : c == False ? b : new(Op.Ite, a.W, [c, a, b]);
    static Term Bv(Op op, Term a, Term b, bool signed = false) => new(op, a.W, [a, b], signed ? 1 : 0);
    static Term Cmp(Op op, Term a, Term b, bool signed = false) => new(op, 0, [a, b], signed ? 1 : 0);
    static Term MkBVAdd(Term a, Term b) => Bv(Op.Add, a, b);
    static Term MkBVSub(Term a, Term b) => Bv(Op.Sub, a, b);
    static Term MkBVMul(Term a, Term b) => Bv(Op.Mul, a, b);
    static Term MkBVAND(Term a, Term b) => Bv(Op.BAnd, a, b);
    static Term MkBVOR(Term a, Term b) => Bv(Op.BOr, a, b);
    static Term MkBVXOR(Term a, Term b) => Bv(Op.BXor, a, b);
    static Term MkBVNeg(Term a) => new(Op.Neg, a.W, [a]);
    static Term MkBVNot(Term a) => new(Op.BvNot, a.W, [a]);
    static Term MkBVSLT(Term a, Term b) => Cmp(Op.Slt, a, b);
    static Term MkBVSLE(Term a, Term b) => Cmp(Op.Sle, a, b);
    static Term MkBVSGT(Term a, Term b) => Cmp(Op.Slt, b, a);
    static Term MkBVSGE(Term a, Term b) => Cmp(Op.Sle, b, a);
    static Term MkBVULT(Term a, Term b) => Cmp(Op.Ult, a, b);
    static Term MkBVULE(Term a, Term b) => Cmp(Op.Ule, a, b);
    static Term MkBVUGT(Term a, Term b) => Cmp(Op.Ult, b, a);
    static Term MkBVUGE(Term a, Term b) => Cmp(Op.Ule, b, a);
    static Term MkSignExt(uint i, Term a) => new(Op.SExt, a.W + i, [a]);
    static Term MkZeroExt(uint i, Term a) => new(Op.ZExt, a.W + i, [a]);
    /// <summary>The low <paramref name="w"/> bits (Z3's extract(w-1, 0)): the only extraction the executor needs.</summary>
    static Term MkLow(uint w, Term a) => new(Op.Extract, w, [a]);

    /// <summary>Symbolic execution of one rule. Values are terms; `err` accumulates the inputs that raise.</summary>
    sealed class Exec(Dictionary<MethodSym, MethodDecl> methods)
    {
        Term err = MkFalse();
        int depth;

        public Translation Run(MethodDecl rule)
        {
            var env = new Dictionary<LocalSym, Term>();
            var domain = new List<Term>();
            var inputs = new List<(RuleDomain, Term, bool)>();
            var syms = ((MethodSym)rule.Sym!).Params;
            for (int i = 0; i < rule.Domains.Count; i++)
            {
                var d = rule.Domains[i];
                var local = syms[i].Local ?? throw new Untranslatable($"parameter {d.Name} has no local");
                Term v;
                bool sg = false;
                if (d.IsBool) v = MkBoolConst(d.Name);
                else
                {
                    uint w = Width(local.Type);
                    sg = Signed(local.Type);
                    v = MkBVConst(d.Name, w);
                    domain.Add(MkBVSGE(v, MkBV(d.Lo, w)));
                    domain.Add(MkBVSLE(v, MkBV(d.Hi, w)));
                }
                env[local] = v;
                inputs.Add((d, v, sg));
            }
            var result = Truth(Body(rule.Body, env));
            return new Translation(rule, inputs, new Goal(domain, err, result));
        }

        uint Width(AType t) => t.Cs switch
        {
            "sbyte" or "byte" => 8,
            "short" or "ushort" => 16,
            "int" or "uint" => 32,
            "long" or "ulong" => 64,
            _ => throw new Untranslatable($"type {t.Cs} is not supported by --smt (integers and Bool are)"),
        };

        static bool Signed(AType t) => t.Cs is "sbyte" or "short" or "int" or "long";

        // ---- statements: path condition + "already returned" + the returned value ----

        sealed class Frame
        {
            public Dictionary<LocalSym, Term> Env = new();
            public Term Returned = null!;
            public Term? Value;
        }

        Term Body(List<Stmt> body, Dictionary<LocalSym, Term> env)
        {
            var f = new Frame { Env = new(env), Returned = MkFalse() };
            Block(body, f, MkTrue(), tail: true);
            return f.Value ?? throw new Untranslatable("the rule has no result value");
        }

        static void Return(Frame f, Term pc, Term v)
        {
            var live = MkAnd(pc, MkNot(f.Returned));
            f.Value = f.Value == null ? v : MkITE(live, v, f.Value);
            f.Returned = MkOr(f.Returned, live);
        }

        void Block(List<Stmt> body, Frame f, Term pc, bool tail)
        {
            for (int i = 0; i < body.Count; i++)
            {
                var s = body[i];
                bool last = tail && i == body.Count - 1;
                var live = MkAnd(pc, MkNot(f.Returned));
                switch (s)
                {
                    case ExprStmt es when last:
                        Return(f, pc, Eval(es.Expr, f.Env, live));
                        break;
                    case ReturnStmt { Value: { } rv }:
                        Return(f, pc, Eval(rv, f.Env, live));
                        break;
                    case Assign { Op: "=" } a when (a.Declares ?? (a.Target as NameExpr)?.Sym as LocalSym) is { } l:
                        {
                            var v = Eval(a.Value, f.Env, live);
                            if (f.Env.TryGetValue(l, out var old) && old.W != v.W)
                                throw new Untranslatable($"`{l.Name}` holds values of different widths");
                            f.Env[l] = old != null ? MkITE(live, v, old) : v;
                            break;
                        }
                    case MultiAssign { Values: [CallExpr { Res: "ext", Recv: { } dr, Args: [{ Value: { } dd }] } dc] } ma
                        when Binder.Mangle(dc.Name) == "divmod" && ma.Locals.Count == 2:
                        {
                            var (x, y, sg) = Operands(Bits(Eval(dr, f.Env, live)), Bits(Eval(dd, f.Env, live)), dr, dd);
                            var (q, r) = FlooredDivMod(x, y, sg, live);
                            f.Env[ma.Locals[0]] = q;
                            f.Env[ma.Locals[1]] = r;
                            break;
                        }
                    case IfStmt ifs:
                        {
                            Term rest = live;
                            for (int k = 0; k < ifs.Conds.Count; k++)
                            {
                                var c = Truth(Eval(ifs.Conds[k], f.Env, rest));
                                Block(ifs.Bodies[k], f, MkAnd(rest, c), last);
                                rest = MkAnd(rest, MkNot(c));
                            }
                            if (ifs.Else != null) Block(ifs.Else, f, rest, last);
                            break;
                        }
                    case ExprStmt es:
                        // the value is dropped but the expression still runs: a call that divides by zero or
                        // overflows fails the rule (audit F14); an untranslatable one makes the rule untranslatable
                        Eval(es.Expr, f.Env, live);
                        break;
                    default:
                        throw new Untranslatable($"`{s.GetType().Name.Replace("Stmt", "").ToLowerInvariant()}` statements are not supported by --smt");
                }
            }
        }

        static Term Truth(Term e) => e.IsBool ? e : throw new Untranslatable("a condition is not a Bool");
        static Term Bits(Term e) => !e.IsBool ? e : throw new Untranslatable("a Bool where an integer is expected");

        void Fails(Term live, Term cond) => err = MkOr(err, MkAnd(live, cond));

        // ---- expressions ----

        Term Eval(Syntax.Expr e, Dictionary<LocalSym, Term> env, Term live)
        {
            switch (e)
            {
                case LitExpr { Kind: "bool" } b: return MkBool(b.Text == "true");
                case LitExpr { Kind: "int" } li: return MkBV(IntLiteral(li.Text), Width(li.Type));
                case NameExpr { Res: "local", Sym: LocalSym l } when env.TryGetValue(l, out var v): return v;
                case ConstExpr ce when ce.Ref.Name.Split('.', ':').LastOrDefault(x => x.Length > 0) is "MIN" or "MAX" && ce.Type.IsInteger:
                    {
                        // Int32::MIN, UInt8::MAX...: the limits of the constant's own type
                        uint w = Width(ce.Type);
                        bool max = ce.Ref.Name.EndsWith("MAX");
                        if (Signed(ce.Type)) return max ? MkBVSub(MinOf(w), MkBV(1, w)) : MinOf(w);   // MAX = MIN - 1 (wraps)
                        return max ? MkBVNot(MkBV(0, w)) : MkBV(0, w);
                    }
                case UnaryExpr { Op: "!" } u: return MkNot(Truth(Eval(u.E, env, live)));
                case UnaryExpr { Op: "-" } neg:
                    {
                        var x = Promote(Bits(Eval(neg.E, env, live)), neg.E.Type);   // -Int8 is an Int32 (C#)
                        if (Signed(neg.E.Type)) Fails(live, MkEq(x, MinOf(x.W)));   // -MIN overflows
                        return MkBVNeg(x);
                    }
                case TernaryExpr t:
                    {
                        var c = Truth(Eval(t.C, env, live));
                        return MkITE(c, Eval(t.A, env, MkAnd(live, c)), Eval(t.B, env, MkAnd(live, MkNot(c))));
                    }
                case BinaryExpr { Op: "&&" or "and" } and:
                    {
                        var l = Truth(Eval(and.L, env, live));
                        return MkAnd(l, Truth(Eval(and.R, env, MkAnd(live, l))));   // the right side runs only when l
                    }
                case BinaryExpr { Op: "||" or "or" } or:
                    {
                        var l = Truth(Eval(or.L, env, live));
                        return MkOr(l, Truth(Eval(or.R, env, MkAnd(live, MkNot(l)))));
                    }
                case BinaryExpr bin: return Binary(bin, env, live);
                case CallExpr call: return Call(call, env, live);
                case StmtExpr se:
                    {
                        var f = new Frame { Env = new(env), Returned = MkFalse() };
                        Block([se.Stmt], f, live, tail: true);
                        return f.Value ?? throw new Untranslatable("an if/case used as a value has no value");
                    }
            }
            throw new Untranslatable($"`{e.GetType().Name.Replace("Expr", "").ToLowerInvariant()}` expressions are not supported by --smt");
        }

        /// <summary>`1_000`, `0xFF`, `0b1010`, `0o17`, `7_i64`, `18446744073709551615_u64` as the literal's 64-bit pattern
        /// (MkBV keeps the low bits of the literal's width).</summary>
        long IntLiteral(string text)
        {
            string t = text.Replace("_", "");
            foreach (var suf in new[] { "i64", "i32", "i16", "i8", "u64", "u32", "u16", "u8", "L" })
                if (t.EndsWith(suf, StringComparison.OrdinalIgnoreCase)) { t = t[..^suf.Length]; break; }
            bool neg = t.StartsWith('-');
            if (neg) t = t[1..];
            int radix = t.Length > 2 && t[0] == '0' ? char.ToLowerInvariant(t[1]) switch { 'x' => 16, 'b' => 2, 'o' => 8, _ => 10 } : 10;
            if (radix != 10) t = t[2..];
            ulong u;
            try { u = System.Convert.ToUInt64(t, radix); }
            catch (Exception e) when (e is FormatException or OverflowException or ArgumentException) { throw new Untranslatable($"integer literal `{text}` is not supported by --smt"); }
            return neg ? unchecked(-(long)u) : unchecked((long)u);
        }

        static Term MinOf(uint w) => MkBV(-(1L << (int)Math.Min(63, w - 1)), w);

        Term Binary(BinaryExpr b, Dictionary<LocalSym, Term> env, Term live)
        {
            var l = Eval(b.L, env, live);
            var r = Eval(b.R, env, live);
            if (l.IsBool && r.IsBool)
                return b.Op switch { "==" => MkEq(l, r), "!=" => MkNot(MkEq(l, r)), _ => throw new Untranslatable($"`{b.Op}` on Bool") };
            var (x, y, sg) = Operands(Bits(l), Bits(r), b.L, b.R);
            switch (b.Op)
            {
                case "==": return MkEq(x, y);
                case "!=": return MkNot(MkEq(x, y));
                case "<": return sg ? MkBVSLT(x, y) : MkBVULT(x, y);
                case "<=": return sg ? MkBVSLE(x, y) : MkBVULE(x, y);
                case ">": return sg ? MkBVSGT(x, y) : MkBVUGT(x, y);
                case ">=": return sg ? MkBVSGE(x, y) : MkBVUGE(x, y);
                case "+":
                    Fails(live, Cmp(Op.AddOvf, x, y, sg));
                    return MkBVAdd(x, y);
                case "-":
                    Fails(live, Cmp(Op.SubOvf, x, y, sg));
                    return MkBVSub(x, y);
                case "*":
                    Fails(live, Cmp(Op.MulOvf, x, y, sg));
                    return MkBVMul(x, y);
                case "&+": return MkBVAdd(x, y);
                case "&-": return MkBVSub(x, y);
                case "&*": return MkBVMul(x, y);
                case "&": return MkBVAND(x, y);
                case "|": return MkBVOR(x, y);
                case "^": return MkBVXOR(x, y);
                case "/": return FlooredDivMod(x, y, sg, live).Q;
                case "%": return FlooredDivMod(x, y, sg, live).R;
            }
            throw new Untranslatable($"operator `{b.Op}` is not supported by --smt");
        }

        /// <summary>
        /// The operands of a binary operator at the width and signedness the compiled code computes in: C#'s binary
        /// numeric promotion, which the emitted C# keeps (review-r1 F8). Int8/UInt8/Int16/UInt16 promote to Int32 first
        /// (`&+` on two Int8 wraps at 32 bits; only `to_i8` / `to_i8!` narrow again). Int32 (or a narrow signed type)
        /// next to UInt32 computes in Int64, so `-1 + 4000000000_u32` is 3999999999, not a wrapped UInt32; a
        /// non-negative Int32 literal next to UInt32 stays UInt32 (C#'s constant conversion). A value already wider
        /// than its static type (an Int64 result the binder types UInt32) counts at its real width, signed.
        /// </summary>
        (Term, Term, bool Signed) Operands(Term x, Term y, Syntax.Expr ex, Syntax.Expr ey)
        {
            var (cx, sx) = Category(x, ex);
            var (cy, sy) = Category(y, ey);
            (uint w, bool sg) = (cx, cy) switch
            {
                _ when cx == "u64" || cy == "u64" => (64u, false),
                _ when cx == "i64" || cy == "i64" => (64u, true),
                ("u32", "i32") or ("i32", "u32") => (64u, true),
                ("u32", _) or (_, "u32") => (32u, false),
                _ => (32u, true),
            };
            return (Extend(x, sx, w), Extend(y, sy, w), sg);
        }

        /// <summary>The promotion class of one operand (i32 u32 i64 u64, or `lit` for a non-negative Int32 literal) and
        /// whether its bits extend signed.</summary>
        (string, bool) Category(Term v, Syntax.Expr e)
        {
            var t = e.Type;
            uint tw = Width(t);
            if (v.W > tw) return (v.W > 32 ? "i64" : "i32", true);   // promoted beyond its static type
            if (tw < 32) return ("i32", Signed(t));
            if (e is LitExpr { Kind: "int" } li && t.Cs == "int" && IntLiteral(li.Text) >= 0) return ("lit", true);
            return ((Signed(t) ? "i" : "u") + tw, Signed(t));
        }

        /// <summary>A narrow integer (Int8..UInt16) as the Int32 C# promotes it to; wider values unchanged.</summary>
        static Term Promote(Term x, AType t) => Extend(x, Signed(t), Math.Max(32, x.W));

        static Term Extend(Term x, bool signed, uint w) =>
            x.W >= w ? x : signed ? MkSignExt(w - x.W, x) : MkZeroExt(w - x.W, x);

        /// <summary>Alice's `/` and `%` round toward negative infinity (ADR 0013); zero divisors and MIN / -1 raise.</summary>
        (Term Q, Term R) FlooredDivMod(Term x, Term y, bool signed, Term live)
        {
            Fails(live, MkEq(y, MkBV(0, x.W)));
            if (!signed) return (Bv(Op.UDiv, x, y), Bv(Op.URem, x, y));
            Fails(live, MkAnd(MkEq(x, MinOf(x.W)), MkEq(y, MkBV(-1, x.W))));
            return (Bv(Op.FDiv, x, y, true), Bv(Op.FMod, x, y, true));
        }

        Term Call(CallExpr c, Dictionary<LocalSym, Term> env, Term live)
        {
            string name = Binder.Mangle(c.Name);
            // number conversions and helpers the compiler maps to arithmetic
            if (c.Res == "ext" && c.Recv != null && c.Args.Count == 0)
            {
                var x = Bits(Eval(c.Recv, env, live));
                switch (name)
                {
                    case "abs":
                        if (Signed(c.Recv.Type)) Fails(live, MkEq(x, MinOf(x.W)));
                        return MkITE(MkBVSLT(x, MkBV(0, x.W)), MkBVNeg(x), x);
                    case "to_i8" or "to_i16" or "to_i32" or "to_i" or "to_i64" or "to_u8" or "to_u16" or "to_u32" or "to_u64":
                        return Convert(x, c.Recv.Type, c.Type, live, check: true);
                    // `to_X!`: wrapping (CreateTruncating, Prelude.Primitives.cs); `to_X` above is CreateChecked
                    case "to_i_b" or "to_i8_b" or "to_i16_b" or "to_i32_b" or "to_i64_b" or "to_u8_b" or "to_u16_b" or "to_u32_b" or "to_u64_b":
                        return Convert(x, c.Recv.Type, c.Type, live, check: false);
                }
            }
            if (c.Recv is ConstExpr { Ref.Name: "Math" } && c.Args.Count == 2 && name is "Min" or "Max")
            {
                // Math.Min(Int32, UInt32) resolves to the Int64 overload: the same promotion as an operator
                var (x, y, sg) = Operands(Bits(Eval(c.Args[0].Value!, env, live)), Bits(Eval(c.Args[1].Value!, env, live)), c.Args[0].Value!, c.Args[1].Value!);
                var less = sg ? MkBVSLT(x, y) : MkBVULT(x, y);
                return name == "Min" ? MkITE(less, x, y) : MkITE(less, y, x);
            }
            // a call to a pure Alice method: inlined (recursion is not unrolled)
            if (c.Res == "alice" && c.Sym is MethodSym m && methods.TryGetValue(m, out var decl) && decl.HasBody)
            {
                if (++depth > 32) throw new Untranslatable($"`{m.Name}` recurses; --smt inlines non-recursive calls only");
                try
                {
                    var callee = new Dictionary<LocalSym, Term>();
                    for (int i = 0; i < m.Params.Count; i++)
                    {
                        var p = m.Params[i];
                        if (p.Local == null || i >= c.Args.Count) throw new Untranslatable($"call to `{m.Name}` with default arguments");
                        callee[p.Local] = Eval(c.Args[i].Value!, env, live);
                    }
                    var f = new Frame { Env = callee, Returned = MkFalse() };
                    Block(decl.Body, f, live, tail: true);
                    return f.Value ?? throw new Untranslatable($"`{m.Name}` has no result value");
                }
                finally { depth--; }
            }
            throw new Untranslatable($"`{c.Name}` is not supported by --smt (arithmetic, comparisons, Math.Min/Max, abs, conversions and pure Alice methods are)");
        }

        Term Convert(Term x, AType from, AType to, Term live, bool check)
        {
            uint wf = x.W, wt = Width(to);
            Term r = wt == wf ? x : wt < wf ? MkLow(wt, x) : Signed(from) ? MkSignExt(wt - wf, x) : MkZeroExt(wt - wf, x);
            if (check)   // checked: the value must survive the round trip (`to_X` raises on out-of-range)
            {
                Term back = wt == wf ? r : wt < wf ? (Signed(to) ? MkSignExt(wf - wt, r) : MkZeroExt(wf - wt, r)) : MkLow(wf, r);
                Fails(live, MkNot(MkEq(back, x)));
                // a sign change: signed -> unsigned raises on negatives, unsigned -> signed when the result reads negative
                if (Signed(from) && !Signed(to)) Fails(live, MkBVSLT(x, MkBV(0, wf)));
                if (!Signed(from) && Signed(to)) Fails(live, MkBVSLT(r, MkBV(0, wt)));
            }
            return r;
        }
    }
}
