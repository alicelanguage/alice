using System.Runtime.InteropServices;
using Alice.Compiler;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;
using Microsoft.Z3;
using Expr = Microsoft.Z3.Expr;
using Syntax = Alice.Compiler.Syntax;

namespace Alice.Cli.Proof;

/// <summary>
/// `alice prove --smt` (ADR 0022): a rule holds for every input of its domain, the full Int32/Int64 range included,
/// when Z3 finds no input that makes it fail. Failing means returning false or raising: integer overflow (Alice traps,
/// ADR 0013), division by zero, `Int::MIN / -1`. The rule's body is executed symbolically over bit vectors with the
/// semantics the compiler gives it (floored `/` and `%`, checked `+ - *`, wrapping `&+ &- &*`); branches and early
/// returns become if-then-else terms. A construct outside that subset makes the rule untranslatable, never half-proven.
/// </summary>
public static class SmtProver
{
    public sealed record Outcome(string Rule, bool Proved, string? Counterexample, string? Untranslatable);

    static bool resolverSet;

    /// <summary>Z3's native library: the system one (`pacman -S z3`, `apt install libz3-4`) or ALICE_Z3=/path/libz3.so.</summary>
    public static string? Unavailable()
    {
        if (!resolverSet)
        {
            resolverSet = true;
            NativeLibrary.SetDllImportResolver(typeof(Context).Assembly, (name, asm, path) =>
            {
                if (!name.Contains("z3", StringComparison.OrdinalIgnoreCase)) return IntPtr.Zero;
                if (Environment.GetEnvironmentVariable("ALICE_Z3") is { Length: > 0 } custom && NativeLibrary.TryLoad(custom, out var h)) return h;
                foreach (var candidate in new[] { "libz3", "libz3.so", "libz3.so.4", "z3" })
                    if (NativeLibrary.TryLoad(candidate, asm, path, out var l)) return l;
                return IntPtr.Zero;
            });
        }
        try { using var ctx = new Context(); return null; }
        catch (Exception e) when (e is DllNotFoundException or TypeInitializationException or EntryPointNotFoundException or BadImageFormatException)
        {
            return "Z3 is not installed: install it (pacman -S z3, apt install libz3-4, brew install z3) or set ALICE_Z3=/path/to/libz3";
        }
    }

    public static List<Outcome> Prove(List<SourceFile> files, IEnumerable<string> refs)
    {
        var asts = files.Select(f => Parser.ParseFile(f.Text, f.Path)).ToList();
        new Binder(asts, new ClrTypes(refs)).Bind();
        var rules = asts.SelectMany(a => Methods(a.Items)).Where(m => m.RuleName != null).ToList();
        var methods = asts.SelectMany(a => Methods(a.Items)).Where(m => m.Sym != null).ToDictionary(m => (MethodSym)m.Sym!, m => m);
        var r = new List<Outcome>();
        foreach (var rule in rules)
        {
            using var ctx = new Context(new Dictionary<string, string> { ["model"] = "true" });
            try
            {
                var ex = new Exec(ctx, methods);
                r.Add(ex.Check(rule));
            }
            catch (Untranslatable u) { r.Add(new Outcome(rule.RuleName!, false, null, u.Message)); }
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

    sealed class Untranslatable(string why) : Exception(why);

    /// <summary>Symbolic execution of one rule. Values are Z3 terms; `err` accumulates the inputs that raise.</summary>
    sealed class Exec(Context z, Dictionary<MethodSym, MethodDecl> methods)
    {
        BoolExpr err = z.MkFalse();
        int depth;

        public Outcome Check(MethodDecl rule)
        {
            var env = new Dictionary<LocalSym, Expr>();
            var domain = new List<BoolExpr>();
            var inputs = new List<(string Name, Expr Var)>();
            var syms = ((MethodSym)rule.Sym!).Params;
            for (int i = 0; i < rule.Domains.Count; i++)
            {
                var d = rule.Domains[i];
                var local = syms[i].Local ?? throw new Untranslatable($"parameter {d.Name} has no local");
                Expr v;
                if (d.IsBool) v = z.MkBoolConst(d.Name);
                else
                {
                    uint w = Width(local.Type);
                    var bv = z.MkBVConst(d.Name, w);
                    v = bv;
                    domain.Add(z.MkBVSGE(bv, z.MkBV(d.Lo, w)));
                    domain.Add(z.MkBVSLE(bv, z.MkBV(d.Hi, w)));
                }
                env[local] = v;
                inputs.Add((d.Name, v));
            }
            var result = Body(rule.Body, env);
            var s = z.MkSolver();
            var prm = z.MkParams();
            prm.Add("timeout", 20000u);   // ms; a nonlinear 64-bit query can run for hours: give up, enumerate instead
            s.Parameters = prm;
            s.Assert(domain.ToArray());
            s.Assert(z.MkOr(err, z.MkNot((BoolExpr)result)));   // an input that raises or returns false
            var status = s.Check();
            if (status == Status.UNSATISFIABLE) return new Outcome(rule.RuleName!, true, null, null);
            if (status == Status.UNKNOWN) throw new Untranslatable("Z3 could not decide the rule: " + s.ReasonUnknown);
            var m = s.Model;
            string ce = string.Join(", ", inputs.Select(x => $"{x.Name} = {Show(m.Eval(x.Var, true), x.Var)}"));
            return new Outcome(rule.RuleName!, false, ce, null);
        }

        static string Show(Expr value, Expr var)
        {
            if (value is BitVecNum n)
            {
                var u = n.BigInteger;
                uint w = ((BitVecSort)var.Sort).Size;
                if (u >= System.Numerics.BigInteger.One << (int)(w - 1)) u -= System.Numerics.BigInteger.One << (int)w;   // two's complement
                return u.ToString();
            }
            return value.ToString();
        }

        static uint Width(AType t) => t.Cs switch
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
            public Dictionary<LocalSym, Expr> Env = new();
            public BoolExpr Returned = null!;
            public Expr? Value;
        }

        Expr Body(List<Stmt> body, Dictionary<LocalSym, Expr> env)
        {
            var f = new Frame { Env = new(env), Returned = z.MkFalse() };
            Block(body, f, z.MkTrue(), tail: true);
            return f.Value ?? throw new Untranslatable("the rule has no result value");
        }

        void Return(Frame f, BoolExpr pc, Expr v)
        {
            var live = z.MkAnd(pc, z.MkNot(f.Returned));
            f.Value = f.Value == null ? v : z.MkITE(live, v, f.Value);
            f.Returned = z.MkOr(f.Returned, live);
        }

        void Block(List<Stmt> body, Frame f, BoolExpr pc, bool tail)
        {
            for (int i = 0; i < body.Count; i++)
            {
                var s = body[i];
                bool last = tail && i == body.Count - 1;
                var live = z.MkAnd(pc, z.MkNot(f.Returned));
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
                            f.Env[l] = f.Env.TryGetValue(l, out var old) ? z.MkITE(live, v, old) : v;
                            break;
                        }
                    case MultiAssign { Values: [CallExpr { Res: "ext", Recv: { } dr, Args: [{ Value: { } dd }] } dc] } ma
                        when Binder.Mangle(dc.Name) == "divmod" && ma.Locals.Count == 2:
                        {
                            var (q, r) = FlooredDivMod(Eval(dr, f.Env, live), Eval(dd, f.Env, live), dr.Type, live);
                            f.Env[ma.Locals[0]] = q;
                            f.Env[ma.Locals[1]] = r;
                            break;
                        }
                    case IfStmt ifs:
                        {
                            BoolExpr rest = live;
                            for (int k = 0; k < ifs.Conds.Count; k++)
                            {
                                var c = Truth(Eval(ifs.Conds[k], f.Env, rest));
                                Block(ifs.Bodies[k], f, z.MkAnd(rest, c), last);
                                rest = z.MkAnd(rest, z.MkNot(c));
                            }
                            if (ifs.Else != null) Block(ifs.Else, f, rest, last);
                            break;
                        }
                    case ExprStmt:
                        break;   // a value not used: pure, nothing to do
                    default:
                        throw new Untranslatable($"`{s.GetType().Name.Replace("Stmt", "").ToLowerInvariant()}` statements are not supported by --smt");
                }
            }
        }

        BoolExpr Truth(Expr e) => e as BoolExpr ?? throw new Untranslatable("a condition is not a Bool");

        void Fails(BoolExpr live, BoolExpr cond) => err = z.MkOr(err, z.MkAnd(live, cond));

        // ---- expressions ----

        Expr Eval(Syntax.Expr e, Dictionary<LocalSym, Expr> env, BoolExpr live)
        {
            switch (e)
            {
                case LitExpr { Kind: "bool" } b: return z.MkBool(b.Text == "true");
                case LitExpr { Kind: "int" } li:
                    {
                        string t = li.Text.Replace("_", "");
                        foreach (var suf in new[] { "i64", "i32", "i16", "i8", "u64", "u32", "u16", "u8", "L" })
                            if (t.EndsWith(suf, StringComparison.OrdinalIgnoreCase)) { t = t[..^suf.Length]; break; }
                        return z.MkBV(long.Parse(t, System.Globalization.CultureInfo.InvariantCulture), Width(li.Type));
                    }
                case NameExpr { Res: "local", Sym: LocalSym l } when env.TryGetValue(l, out var v): return v;
                case ConstExpr ce when ce.Ref.Name.Split('.', ':').LastOrDefault(x => x.Length > 0) is "MIN" or "MAX" && ce.Type.IsInteger:
                    {
                        // Int32::MIN, UInt8::MAX...: the limits of the constant's own type
                        uint w = Width(ce.Type);
                        bool max = ce.Ref.Name.EndsWith("MAX");
                        if (Signed(ce.Type)) return max ? z.MkBVSub(MinOf(w), z.MkBV(1, w)) : MinOf(w);   // MAX = MIN - 1 (wraps)
                        return max ? z.MkBVNot(z.MkBV(0, w)) : z.MkBV(0, w);
                    }
                case UnaryExpr { Op: "!" } u: return z.MkNot(Truth(Eval(u.E, env, live)));
                case UnaryExpr { Op: "-" } neg:
                    {
                        var x = (BitVecExpr)Eval(neg.E, env, live);
                        if (Signed(neg.E.Type)) Fails(live, z.MkEq(x, MinOf(x.SortSize)));   // -MIN overflows
                        return z.MkBVNeg(x);
                    }
                case TernaryExpr t:
                    {
                        var c = Truth(Eval(t.C, env, live));
                        return z.MkITE(c, Eval(t.A, env, z.MkAnd(live, c)), Eval(t.B, env, z.MkAnd(live, z.MkNot(c))));
                    }
                case BinaryExpr { Op: "&&" or "and" } and:
                    {
                        var l = Truth(Eval(and.L, env, live));
                        return z.MkAnd(l, Truth(Eval(and.R, env, z.MkAnd(live, l))));   // the right side runs only when l
                    }
                case BinaryExpr { Op: "||" or "or" } or:
                    {
                        var l = Truth(Eval(or.L, env, live));
                        return z.MkOr(l, Truth(Eval(or.R, env, z.MkAnd(live, z.MkNot(l)))));
                    }
                case BinaryExpr bin: return Binary(bin, env, live);
                case CallExpr call: return Call(call, env, live);
                case StmtExpr se:
                    {
                        var f = new Frame { Env = new(env), Returned = z.MkFalse() };
                        Block([se.Stmt], f, live, tail: true);
                        return f.Value ?? throw new Untranslatable("an if/case used as a value has no value");
                    }
            }
            throw new Untranslatable($"`{e.GetType().Name.Replace("Expr", "").ToLowerInvariant()}` expressions are not supported by --smt");
        }

        BitVecExpr MinOf(uint w) => z.MkBV(-(1L << (int)Math.Min(63, w - 1)), w);

        Expr Binary(BinaryExpr b, Dictionary<LocalSym, Expr> env, BoolExpr live)
        {
            var l = Eval(b.L, env, live);
            var r = Eval(b.R, env, live);
            if (l is BoolExpr lb && r is BoolExpr rb)
                return b.Op switch { "==" => z.MkEq(lb, rb), "!=" => z.MkNot(z.MkEq(lb, rb)), _ => throw new Untranslatable($"`{b.Op}` on Bool") };
            var (x, y) = Same((BitVecExpr)l, (BitVecExpr)r, b.L.Type, b.R.Type);
            bool sg = Signed(b.L.Type);
            switch (b.Op)
            {
                case "==": return z.MkEq(x, y);
                case "!=": return z.MkNot(z.MkEq(x, y));
                case "<": return sg ? z.MkBVSLT(x, y) : z.MkBVULT(x, y);
                case "<=": return sg ? z.MkBVSLE(x, y) : z.MkBVULE(x, y);
                case ">": return sg ? z.MkBVSGT(x, y) : z.MkBVUGT(x, y);
                case ">=": return sg ? z.MkBVSGE(x, y) : z.MkBVUGE(x, y);
                case "+":
                    Fails(live, z.MkNot(z.MkAnd(z.MkBVAddNoOverflow(x, y, sg), sg ? z.MkBVAddNoUnderflow(x, y) : z.MkTrue())));
                    return z.MkBVAdd(x, y);
                case "-":
                    Fails(live, z.MkNot(z.MkAnd(sg ? z.MkBVSubNoOverflow(x, y) : z.MkTrue(), z.MkBVSubNoUnderflow(x, y, sg))));
                    return z.MkBVSub(x, y);
                case "*":
                    Fails(live, z.MkNot(z.MkAnd(z.MkBVMulNoOverflow(x, y, sg), sg ? z.MkBVMulNoUnderflow(x, y) : z.MkTrue())));
                    return z.MkBVMul(x, y);
                case "&+": return z.MkBVAdd(x, y);
                case "&-": return z.MkBVSub(x, y);
                case "&*": return z.MkBVMul(x, y);
                case "&": return z.MkBVAND(x, y);
                case "|": return z.MkBVOR(x, y);
                case "^": return z.MkBVXOR(x, y);
                case "/": return FlooredDivMod(x, y, b.L.Type, live).Q;
                case "%": return FlooredDivMod(x, y, b.L.Type, live).R;
            }
            throw new Untranslatable($"operator `{b.Op}` is not supported by --smt");
        }

        /// <summary>Operands of different widths (an Int32 next to an Int64) widen like C#: sign- or zero-extension.</summary>
        (BitVecExpr, BitVecExpr) Same(BitVecExpr x, BitVecExpr y, AType tx, AType ty)
        {
            if (x.SortSize == y.SortSize) return (x, y);
            if (x.SortSize < y.SortSize) return (Signed(tx) ? z.MkSignExt(y.SortSize - x.SortSize, x) : z.MkZeroExt(y.SortSize - x.SortSize, x), y);
            return (x, Signed(ty) ? z.MkSignExt(x.SortSize - y.SortSize, y) : z.MkZeroExt(x.SortSize - y.SortSize, y));
        }

        /// <summary>Alice's `/` and `%` round toward negative infinity (ADR 0013); zero divisors and MIN / -1 raise.</summary>
        (BitVecExpr Q, BitVecExpr R) FlooredDivMod(Expr a, Expr b, AType t, BoolExpr live)
        {
            var (x, y) = Same((BitVecExpr)a, (BitVecExpr)b, t, t);
            var zero = z.MkBV(0, x.SortSize);
            Fails(live, z.MkEq(y, zero));
            if (!Signed(t)) return (z.MkBVUDiv(x, y), z.MkBVURem(x, y));
            Fails(live, z.MkAnd(z.MkEq(x, MinOf(x.SortSize)), z.MkEq(y, z.MkBV(-1, x.SortSize))));
            var q = z.MkBVSDiv(x, y);
            var r = z.MkBVSRem(x, y);
            // truncated -> floored: when the remainder is non-zero and its sign differs from the divisor's
            var adjust = z.MkAnd(z.MkNot(z.MkEq(r, zero)), z.MkBVSLT(z.MkBVXOR(r, y), zero));
            var one = z.MkBV(1, x.SortSize);
            return ((BitVecExpr)z.MkITE(adjust, z.MkBVSub(q, one), q), (BitVecExpr)z.MkITE(adjust, z.MkBVAdd(r, y), r));
        }

        Expr Call(CallExpr c, Dictionary<LocalSym, Expr> env, BoolExpr live)
        {
            string name = Binder.Mangle(c.Name);
            // number conversions and helpers the compiler maps to arithmetic
            if (c.Res == "ext" && c.Recv != null && c.Args.Count == 0)
            {
                var x = (BitVecExpr)Eval(c.Recv, env, live);
                switch (name)
                {
                    case "abs":
                        if (Signed(c.Recv.Type)) Fails(live, z.MkEq(x, MinOf(x.SortSize)));
                        return z.MkITE(z.MkBVSLT(x, z.MkBV(0, x.SortSize)), z.MkBVNeg(x), x);
                    case "to_i8" or "to_i16" or "to_i32" or "to_i" or "to_i64" or "to_u8" or "to_u16" or "to_u32" or "to_u64":
                        return Convert(x, c.Recv.Type, c.Type, live, check: true);
                    case "to_i8_b" or "to_i16_b" or "to_i32_b" or "to_i64_b" or "to_u8_b" or "to_u32_b":   // `to_X!`: wrapping
                        return Convert(x, c.Recv.Type, c.Type, live, check: false);
                }
            }
            if (c.Recv is ConstExpr { Ref.Name: "Math" } && c.Args.Count == 2 && name is "Min" or "Max")
            {
                var (x, y) = Same((BitVecExpr)Eval(c.Args[0].Value!, env, live), (BitVecExpr)Eval(c.Args[1].Value!, env, live), c.Args[0].Value!.Type, c.Args[1].Value!.Type);
                bool sg = Signed(c.Args[0].Value!.Type);
                var less = sg ? z.MkBVSLT(x, y) : z.MkBVULT(x, y);
                return name == "Min" ? z.MkITE(less, x, y) : z.MkITE(less, y, x);
            }
            // a call to a pure Alice method: inlined (recursion is not unrolled)
            if (c.Res == "alice" && c.Sym is MethodSym m && methods.TryGetValue(m, out var decl) && decl.HasBody)
            {
                if (++depth > 32) throw new Untranslatable($"`{m.Name}` recurses; --smt inlines non-recursive calls only");
                try
                {
                    var callee = new Dictionary<LocalSym, Expr>();
                    for (int i = 0; i < m.Params.Count; i++)
                    {
                        var p = m.Params[i];
                        if (p.Local == null || i >= c.Args.Count) throw new Untranslatable($"call to `{m.Name}` with default arguments");
                        callee[p.Local] = Eval(c.Args[i].Value!, env, live);
                    }
                    var f = new Frame { Env = callee, Returned = z.MkFalse() };
                    Block(decl.Body, f, live, tail: true);
                    return f.Value ?? throw new Untranslatable($"`{m.Name}` has no result value");
                }
                finally { depth--; }
            }
            throw new Untranslatable($"`{c.Name}` is not supported by --smt (arithmetic, comparisons, Math.Min/Max, abs, conversions and pure Alice methods are)");
        }

        Expr Convert(BitVecExpr x, AType from, AType to, BoolExpr live, bool check)
        {
            uint wf = x.SortSize, wt = Width(to);
            BitVecExpr r = wt == wf ? x : wt < wf ? z.MkExtract(wt - 1, 0, x) : Signed(from) ? z.MkSignExt(wt - wf, x) : z.MkZeroExt(wt - wf, x);
            if (check)   // checked: the value must survive the round trip (`to_X` raises on out-of-range)
            {
                BitVecExpr back = wt == wf ? r : wt < wf ? (Signed(to) ? z.MkSignExt(wf - wt, r) : z.MkZeroExt(wf - wt, r)) : z.MkExtract(wf - 1, 0, r);
                Fails(live, z.MkNot(z.MkEq(back, x)));
                // a sign change: signed -> unsigned raises on negatives, unsigned -> signed when the result reads negative
                if (Signed(from) && !Signed(to)) Fails(live, z.MkBVSLT(x, z.MkBV(0, wf)));
                if (!Signed(from) && Signed(to)) Fails(live, z.MkBVSLT(r, z.MkBV(0, wt)));
            }
            return r;
        }
    }
}
