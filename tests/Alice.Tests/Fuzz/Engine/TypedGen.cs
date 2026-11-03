using System.Text;

namespace Alice.Fuzz;

/// <summary>
/// Generator of well-typed, terminating Alice programs for the binder/lowering and differential fuzzers. A program is a
/// few kernels (each a `def` returning a value) and a main that prints every kernel's result inside a rescue for the
/// arithmetic exceptions. Kernels cover numeric loops (checked and wrapping arithmetic, floored `/` `%`, shifts,
/// conversions, ternaries), arrays (fill, index, reductions, blocks), lists, hashes, records, sum types (flat and
/// recursive), `affine enum` trees with `borrow`, blocks and `yield`, `par for` over owned arrays with a pure body,
/// `par a, b =` forks, `pure` and `safe` defs, `own` arrays, classes, strings and exceptions. Every loop is bounded by
/// a constant and every index is reduced modulo the array size, so programs terminate and only raise on purpose
/// (checked overflow, `raise`).
/// </summary>
public sealed class TypedGen(Rng r, bool avoidKnown = false)
{
    // avoidKnown: steer clear of triaged bugs (Known.Issues F1-F5) so differential runs compile; the typed fuzzer
    // mixes both settings so the known bugs stay covered.
    int uniq;
    readonly StringBuilder top = new();     // types
    readonly StringBuilder defs = new();
    readonly StringBuilder main = new();
    readonly HashSet<string> features = [];
    int kn;

    /// <summary>Feature tags of the last program (coverage stats).</summary>
    public IReadOnlySet<string> Features => features;

    /// <summary>Whether the program prints only values computed by pure code (no I/O inside kernels).</summary>
    public static string Generate(int seed, bool avoidKnown = false) => new TypedGen(new Rng(seed), avoidKnown).Program();

    public string Program()
    {
        int n = r.Range(2, 5);
        for (int i = 0; i < n; i++) Kernel();
        return top.ToString() + defs + main;
    }

    // ------------------------------------------------------------------ typed expressions

    enum T { I32, I64, F64, Bool }

    sealed class Scope
    {
        public readonly List<(string Name, T Type)> Vars = [];
        public readonly HashSet<string> Loop = [];   // loop variables: not assigned
        public IEnumerable<string> Of(T t) => Vars.Where(v => v.Type == t).Select(v => v.Name);
        public IEnumerable<string> Assignable(T t) => Of(t).Where(v => !Loop.Contains(v));
    }

    static string Name(T t) => t switch { T.I32 => "Int32", T.I64 => "Int64", T.F64 => "Float64", _ => "Bool" };

    string Lit(T t) => t switch
    {
        T.I32 => r.Next(12) switch { 0 => "2147483647", 1 => "(-2147483648)", 2 => r.Pick("65536", "46341", "1_000_000"), < 6 => r.Range(-9, 9).ToString(), _ => r.Range(0, 300).ToString() },
        T.I64 => r.Next(10) switch { 0 => "9223372036854775807_i64", 1 => r.Pick("4294967296_i64", "(-1_i64)"), _ => r.Range(-50, 5000) + "_i64" },
        T.F64 => r.Pick("0.5", "1.5", "2.0", "-3.25", "0.1", "100.0", "1.0e-3", "7.75"),
        _ => r.Pick("true", "false"),
    };

    string Leaf(Scope s, T t)
    {
        var vs = s.Of(t).ToList();
        return vs.Count > 0 && r.Chance(70) ? r.PickOf(vs) : Lit(t);
    }

    static bool HasVar(string e) => System.Text.RegularExpressions.Regex.IsMatch(e, @"\b(v\d+|n|x|i\d*|j[\d_]+)\b");

    static string P(string e) => e.StartsWith('(') && e.EndsWith(')') || !e.Contains(' ') && !e.StartsWith('-') ? e : "(" + e + ")";

    string E(Scope s, T t, int d)
    {
        if (d <= 0 || r.Chance(25)) return Leaf(s, t);
        int dd = d - 1;
        switch (t)
        {
            case T.I32:
            case T.I64:
                {
                    string zero = t == T.I32 ? "0" : "0_i64", one = t == T.I32 ? "1" : "1_i64";
                    int bits = t == T.I32 ? 31 : 63;
                    switch (r.Next(16))
                    {
                        case 0 or 1 or 2: return $"({E(s, t, dd)} {r.Pick("&+", "&-", "&*")} {E(s, t, dd)})";
                        case 3: return $"({E(s, t, dd)} {r.Pick("^", "&", "|")} {E(s, t, dd)})";
                        case 4: return $"({E(s, t, dd)} {r.Pick(">>", "<<")} {r.Range(0, bits)})";
                        case 5:
                            {
                                // checked: overflow raises; with no variable Roslyn folds it, and an overflowing
                                // constant is a compile error (D1, by design): keep a variable in every checked op
                                var x = E(s, t, dd);
                                var y = Leaf(s, t);
                                if (!HasVar(x) && !HasVar(y)) return $"({x} {r.Pick("&+", "&-", "&*")} {y})";
                                features.Add("checked-arith");
                                return $"({x} {r.Pick("+", "-", "*")} {y})";
                            }
                        case 6:
                            {
                                string b = E(s, t, dd);
                                return $"({E(s, t, dd)} {r.Pick("/", "%")} ({b} == {zero} ? {one} : {b}))";
                            }
                        case 7: return $"Math.{r.Pick("Min", "Max")}({E(s, t, dd)}, {E(s, t, dd)})";
                        case 8: return $"({C(s, dd)} ? {E(s, t, dd)} : {E(s, t, dd)})";
                        case 9: return t == T.I32 ? $"{P(E(s, T.I64, dd))}.to_i32!" : $"{P(E(s, T.I32, dd))}.to_i64";
                        case 10: return t == T.I32 ? $"{P(E(s, T.I32, dd))}.{r.Pick("to_u8!", "to_i8!", "to_i16!", "to_u16!")}.to_i32" : $"{P(E(s, T.I64, dd))}.to_u32!.to_i64";
                        case 11: features.Add("float-conv"); return $"{P(E(s, T.F64, dd))}.clamp(-1000.0, 1000.0).{(t == T.I32 ? "to_i32" : "to_i64")}";
                        case 12: return t == T.I64 ? $"{P(E(s, t, dd))}.abs" : $"{P(E(s, t, dd))}.{r.Pick("abs", "popcount", "sign")}";
                        case 13: { var x = E(s, t, dd); return HasVar(x) ? $"(-{P(x)})" : $"(0 &- {P(x)})".Replace("0 &-", t == T.I32 ? "0 &-" : "0_i64 &-"); }
                        default: return Leaf(s, t);
                    }
                }
            case T.F64:
                return r.Next(9) switch
                {
                    0 or 1 => $"({E(s, T.F64, dd)} {r.Pick("+", "-", "*")} {E(s, T.F64, dd)})",
                    2 => $"({E(s, T.F64, dd)} / {r.Pick("2.0", "3.0", "0.5", "7.0")})",
                    3 => $"{P(E(s, r.Pick(T.I32, T.I64), dd))}.to_f64",
                    4 => $"{P(E(s, T.F64, dd))}.abs.sqrt",
                    5 => $"Math.{r.Pick("Min", "Max")}({E(s, T.F64, dd)}, {E(s, T.F64, dd)})",
                    6 => $"({C(s, dd)} ? {E(s, T.F64, dd)} : {E(s, T.F64, dd)})",
                    7 => $"{P(E(s, T.F64, dd))}.{r.Pick("floor", "ceil", "round")}",
                    _ => Leaf(s, T.F64),
                };
            default:
                return C(s, d);
        }
    }

    string C(Scope s, int d)
    {
        if (d <= 0) return r.Chance(50) ? $"({Leaf(s, T.I32)} {r.Pick("<", ">", "==", "!=")} {Leaf(s, T.I32)})" : Leaf(s, T.Bool);
        int dd = d - 1;
        return r.Next(7) switch
        {
            0 or 1 => $"({E(s, T.I32, dd)} {r.Pick("<", "<=", ">", ">=", "==", "!=")} {E(s, T.I32, dd)})",
            2 => $"({E(s, T.I64, dd)} {r.Pick("<", ">", "==")} {E(s, T.I64, dd)})",
            3 => $"({E(s, T.F64, dd)} {r.Pick("<", ">")} {E(s, T.F64, dd)})",
            4 => $"({C(s, dd)} {r.Pick("&&", "||")} {C(s, dd)})",
            5 => $"{P(E(s, T.I32, dd))}.{r.Pick("even?", "odd?", "zero?", "positive?")}",
            _ => $"!{P(C(s, dd))}",
        };
    }

    // ------------------------------------------------------------------ statements

    sealed class Body(int indent)
    {
        readonly StringBuilder sb = new();
        public int Indent = indent;
        public void Line(string s) => sb.Append(new string(' ', Indent * 2)).Append(s).Append('\n');
        public void Open(string s) { Line(s); Indent++; }
        public void Close(string s = "end") { Indent--; Line(s); }
        public override string ToString() => sb.ToString();
    }

    /// <summary>Declares typed locals (all of the scalar types) at the top of a kernel.</summary>
    void Locals(Body b, Scope s, int count)
    {
        for (int i = 0; i < count; i++)
        {
            var t = i < 4 ? (T)i : r.Pick(T.I32, T.I32, T.I64, T.F64, T.Bool);
            string v = "v" + s.Vars.Count;
            b.Line($"{v} = {E(s, t, 2)}");
            s.Vars.Add((v, t));
        }
    }

    void Assign(Body b, Scope s, int d)
    {
        var t = r.Pick(T.I32, T.I64, T.F64, T.Bool);
        var vs = s.Assignable(t).ToList();
        if (vs.Count == 0)
        {
            // never declare inside a nested block: pick any assignable local instead
            var any = s.Vars.Where(x => !s.Loop.Contains(x.Name)).ToList();
            if (any.Count == 0) return;
            (_, t) = r.PickOf(any);
            vs = s.Assignable(t).ToList();
        }
        string v = r.PickOf(vs);
        if (t is T.I32 or T.I64 && r.Chance(25)) b.Line($"{v} = {v} {r.Pick("&+", "&-", "^", "|")} {P(E(s, t, d))}");
        else b.Line($"{v} = {E(s, t, d)}");
    }

    void Stmts(Body b, Scope s, int depth, int n, bool inLoop)
    {
        for (int i = 0; i < n; i++)
        {
            int k = depth <= 0 ? r.Next(4) : r.Next(9);
            switch (k)
            {
                case 0 or 1 or 2: Assign(b, s, 3); break;
                case 3:
                    if (inLoop && r.Chance(40)) b.Line($"{r.Pick("next", "break")} if {C(s, 1)}");
                    else Assign(b, s, 2);
                    break;
                case 4 or 5:
                    b.Open($"if {C(s, 2)}");
                    Stmts(b, s, depth - 1, r.Range(1, 2), inLoop);
                    if (r.Chance(50)) { b.Indent--; b.Line(r.Chance(30) ? $"elsif {C(s, 1)}" : "else"); b.Indent++; Stmts(b, s, depth - 1, 1, inLoop); }
                    b.Close();
                    break;
                case 6:
                    {
                        string iv = "i" + b.Indent;
                        if (s.Of(T.I32).Contains(iv)) { Assign(b, s, 3); break; }
                        features.Add("for");
                        b.Open($"for {iv} in {r.Pick("0...", "1..")}{r.Range(0, 12)}");
                        s.Vars.Add((iv, T.I32)); s.Loop.Add(iv);
                        Stmts(b, s, depth - 1, r.Range(1, 3), true);
                        s.Vars.Remove((iv, T.I32)); s.Loop.Remove(iv);
                        b.Close();
                        break;
                    }
                case 7:
                    {
                        string jv = avoidKnown ? "j" + b.Indent + "_" + uniq++ : "j" + b.Indent;
                        if (s.Of(T.I32).Contains(jv)) { Assign(b, s, 3); break; }
                        features.Add("while");
                        b.Line($"{jv} = 0");
                        b.Open(r.Chance(50) ? $"while {jv} < {r.Range(0, 9)}" : $"until {jv} >= {r.Range(0, 9)}");
                        s.Vars.Add((jv, T.I32)); s.Loop.Add(jv);
                        Stmts(b, s, depth - 1, r.Range(1, 2), false);
                        b.Line($"{jv} += 1");
                        s.Vars.Remove((jv, T.I32)); s.Loop.Remove(jv);
                        b.Close();
                        break;
                    }
                default:
                    {
                        features.Add("case");
                        var e = E(s, T.I32, 1);
                        b.Open($"case {e}");
                        int arms = r.Range(1, 3);
                        for (int a = 0; a < arms; a++)
                        {
                            string pat = r.Next(avoidKnown ? 1 : 3) switch { 0 => $"{r.Range(-3, 3)}, {r.Range(4, 9)}", 1 => $"{r.Range(0, 5)}..{r.Range(5, 50)}", _ => r.Range(-2, 20).ToString() };
                            b.Indent--; b.Line($"when {pat}"); b.Indent++;
                            Stmts(b, s, depth - 1, 1, inLoop);
                        }
                        b.Indent--; b.Line("else"); b.Indent++;
                        Stmts(b, s, 0, 1, inLoop);
                        b.Close();
                        break;
                    }
            }
        }
    }

    /// <summary>A checksum over every scalar local (Int64, wrapping), with floats and bools folded in exactly.</summary>
    static string Checksum(Scope s)
    {
        var parts = new List<string> { "0_i64" };
        foreach (var (n, t) in s.Vars.Where(v => !s.Loop.Contains(v.Name)))
            parts.Add(t switch
            {
                T.I32 => $"{n}.to_i64",
                T.I64 => n,
                T.F64 => $"({n}.nan? ? 7_i64 : {n}.clamp(-1.0e15, 1.0e15).to_i64)",
                _ => $"({n} ? 1_i64 : 2_i64)",
            });
        return string.Join(" &+ ", parts);
    }

    // ------------------------------------------------------------------ kernels

    void Call(string expr, bool wrap = true)
    {
        if (!wrap) { main.Append("puts ").Append(expr).Append('\n'); return; }
        main.Append("begin\n  puts ").Append(expr).Append('\n')
            .Append("rescue e : OverflowException\n  puts \"overflow\"\n")
            .Append("rescue e : DivideByZeroException\n  puts \"div0\"\n")
            .Append("rescue e : IndexOutOfRangeException\n  puts \"index\"\nend\n");
    }

    void Kernel()
    {
        int id = kn++;
        switch (r.Next(15))
        {
            case 0 or 1 or 2: Numeric(id); break;
            case 3: ArrayKernel(id); break;
            case 4: ListKernel(id); break;
            case 5: HashKernel(id); break;
            case 6: RecordKernel(id); break;
            case 7: SumKernel(id); break;
            case 8: TreeKernel(id, r.Chance(50)); break;
            case 9: BlockKernel(id); break;
            case 10: ParForKernel(id); break;
            case 11: ForkKernel(id); break;
            case 12: OwnKernel(id); break;
            case 13: ClassKernel(id); break;
            default: StringRescueKernel(id); break;
        }
    }

    void Numeric(int id)
    {
        features.Add("numeric");
        var s = new Scope();
        s.Vars.Add(("n", T.I32));
        s.Loop.Add("n");
        var b = new Body(1);
        Locals(b, s, r.Range(4, 7));
        Stmts(b, s, 3, r.Range(2, 6), false);
        b.Line(Checksum(s));
        string mod = r.Pick("", "", "pure ", "safe ", "safe pure ");
        if (mod.Length > 0) features.Add(mod.Trim());
        defs.Append($"{mod}def k{id}(n : Int32) : Int64\n").Append(b).Append("end\n\n");
        Call($"k{id}({r.Range(0, 20)})");
    }

    string ElemT(out T t)
    {
        t = r.Pick(T.I32, T.I64, T.F64);
        return Name(t);
    }

    void ArrayKernel(int id)
    {
        features.Add("array");
        var s = new Scope();
        s.Vars.Add(("n", T.I32)); s.Loop.Add("n");
        var b = new Body(1);
        string et = ElemT(out var t);
        int m = r.Range(1, 16);
        b.Line($"xs = Array({et}).new({m})");
        Locals(b, s, r.Range(1, 3));
        b.Open($"for i in 0...{m}");
        s.Vars.Add(("i", T.I32)); s.Loop.Add("i");
        b.Line($"xs[i] = {E(s, t, 2)}");
        if (r.Chance(50)) b.Line($"xs[{P(E(s, T.I32, 2))} % {m}] = {E(s, t, 1)}");
        s.Vars.Remove(("i", T.I32)); s.Loop.Remove("i");
        b.Close();
        string acc = t == T.F64 ? "0.0" : t == T.I64 ? "0_i64" : "0";
        string plus = t == T.F64 ? "+" : "&+";
        string red = r.Next(9) switch
        {
            0 => "xs.sum",
            1 => "xs.max",
            2 => "xs.min",
            3 => $"xs.map {{ |x| x {(t == T.F64 ? "*" : "&*")} {Lit(t)} }}.{(t == T.F64 ? "max" : "min")}",
            4 => "xs.select { |x| x > " + Lit(t) + " }.size",
            5 => $"xs.count {{ |x| x < {Lit(t)} }}",
            6 => "xs.sort[" + r.Range(0, m - 1) + "]",
            7 => "xs.reverse[0]",
            _ => "xs.includes?(" + Lit(t) + ")",
        };
        if (r.Chance(40))
        {
            features.Add("block-each");
            b.Line($"acc = {acc}");
            b.Line(r.Chance(50) ? $"xs.each {{ |x| acc = acc {plus} x }}" : $"xs.each_with_index {{ |x, j| acc = acc {plus} x {(t == T.F64 ? "* j.to_f64" : "&* j" + (t == T.I64 ? ".to_i64" : ""))} }}");
            b.Line("puts acc");
        }
        b.Line(red);
        defs.Append($"def k{id}(n : Int32)\n").Append(b).Append("end\n\n");
        Call($"k{id}({r.Range(0, 9)})");
    }

    void ListKernel(int id)
    {
        features.Add("list");
        var s = new Scope();
        s.Vars.Add(("n", T.I32)); s.Loop.Add("n");
        var b = new Body(1);
        b.Line("l = List(Int64).new");
        b.Open($"for i in 0...n");
        s.Vars.Add(("i", T.I32)); s.Loop.Add("i");
        b.Line($"l << {E(s, T.I64, 2)}");
        b.Line($"l << {E(s, T.I32, 1)}.to_i64 if {C(s, 1)}");
        s.Vars.Remove(("i", T.I32)); s.Loop.Remove("i");
        b.Close();
        b.Line("t = 0_i64");
        b.Open("for e in l");
        b.Line("t = t &+ e");
        b.Close();
        b.Line("puts l.size");
        b.Line("t &+ (l.size > 0 ? l[l.size - 1] : 0_i64)");
        defs.Append($"def k{id}(n : Int32) : Int64\n").Append(b).Append("end\n\n");
        Call($"k{id}({r.Range(0, 12)})");
    }

    void HashKernel(int id)
    {
        features.Add("hash");
        var s = new Scope();
        s.Vars.Add(("n", T.I32)); s.Loop.Add("n");
        var b = new Body(1);
        b.Line("h = Hash(Int32, Int64).new");
        b.Open("for i in 0...n");
        s.Vars.Add(("i", T.I32)); s.Loop.Add("i");
        b.Line($"h[{P(E(s, T.I32, 1))} % 7] = {E(s, T.I64, 2)}");
        s.Vars.Remove(("i", T.I32)); s.Loop.Remove("i");
        b.Close();
        b.Line("t = 0_i64");
        if (r.Chance(50)) { b.Open("for k, v in h"); b.Line("t = t &+ v &* k.to_i64"); b.Close(); }
        else b.Line("h.each { |k, v| t = t ^ (v &+ k.to_i64) }");
        b.Line($"t &+ h.fetch({r.Range(0, 7)}, 3_i64) &+ (h.has_key?({r.Range(0, 7)}) ? 1_i64 : 0_i64) &+ h.size.to_i64");
        defs.Append($"def k{id}(n : Int32) : Int64\n").Append(b).Append("end\n\n");
        Call($"k{id}({r.Range(0, 14)})");
    }

    void RecordKernel(int id)
    {
        features.Add("record");
        string rn = "R" + id;
        bool st = r.Chance(50);
        top.Append($"record {(st ? "struct " : "")}{rn}(a : Int32, b : Int64, c : Float64)\n\n");
        var s = new Scope();
        s.Vars.Add(("n", T.I32)); s.Loop.Add("n");
        var b = new Body(1);
        b.Line($"p = {rn}.new({E(s, T.I32, 1)}, {E(s, T.I64, 1)}, {E(s, T.F64, 1)})");
        b.Line($"q = {rn}.new(p.a &+ n, p.b &* 3_i64, p.c * 0.5)");
        b.Line("puts p == q");
        b.Line("puts q.c");
        b.Line("p.a.to_i64 &+ q.b &+ q.a.to_i64");
        defs.Append($"def k{id}(n : Int32) : Int64\n").Append(b).Append("end\n\n");
        Call($"k{id}({r.Range(0, 9)})");
    }

    void SumKernel(int id)
    {
        features.Add("sum-type");
        string en = "S" + id;
        top.Append($"enum {en}\n  Circle(r : Int32)\n  Rect(w : Int32, h : Int64)\n  Empty\nend\n\n");
        var s = new Scope();
        s.Vars.Add(("x", T.I32)); s.Loop.Add("x");
        string area = $"def area{id}(s : {en}) : Int64\n  case s\n  when {en}::Circle(r) then r.to_i64 &* r.to_i64 &* 3_i64\n  when {en}::Rect(w, h) then w.to_i64 &* h\n  when {en}::Empty then {r.Range(0, 5)}_i64\n  end\nend\n\n";
        defs.Append(area);
        var b = new Body(1);
        b.Line($"l = List({en}).new");
        b.Open("for x in 0...n");
        b.Open($"if {C(s, 1)}");
        b.Line($"l << {en}::Circle({E(s, T.I32, 1)})");
        b.Indent--; b.Line($"elsif {C(s, 1)}"); b.Indent++;
        b.Line($"l << {en}::Rect({E(s, T.I32, 1)}, {E(s, T.I64, 1)})");
        b.Indent--; b.Line("else"); b.Indent++;
        b.Line($"l << {en}::Empty");
        b.Close();
        b.Close();
        b.Line("t = 0_i64");
        b.Line($"l.each {{ |sh| t = t &+ area{id}(sh) }}");
        b.Line("t");
        defs.Append($"def k{id}(n : Int32) : Int64\n").Append(b).Append("end\n\n");
        Call($"k{id}({r.Range(0, 12)})");
    }

    void TreeKernel(int id, bool affine)
    {
        features.Add(affine ? "affine-enum" : "recursive-enum");
        string tn = "Tr" + id;
        string lt = affine ? "UInt32" : "Int32";
        string one = affine ? "_u32" : "";
        string add = "&+";
        top.Append($"{(affine ? "affine " : "")}enum {tn}\n  Leaf(v : {lt})\n  Node(l : {tn}, r : {tn})\nend\n\n");
        defs.Append($"def build{id}(d : Int32, s : Int32) : {tn}\n  if d == 0\n    {tn}::Leaf(((s &* {r.Range(3, 97)}) & 1023).{(affine ? "to_u32" : "to_i32")})\n  else\n    {tn}::Node(build{id}(d - 1, s &* 2), build{id}(d - 1, s &* 2 &+ 1))\n  end\nend\n\n");
        defs.Append($"def total{id}(t : {tn}) : {lt}\n  case t\n  when {tn}::Leaf(v) then v\n  when {tn}::Node(l, r) then total{id}(l) {add} total{id}(r)\n  end\nend\n\n");
        defs.Append($"def size{id}({(affine || r.Chance(50) ? "borrow " : "")}t : {tn}) : {lt}\n  case t\n  when {tn}::Leaf(v) then 1{one}\n  when {tn}::Node(l, r) then size{id}(l) {add} size{id}(r)\n  end\nend\n\n");
        var b = new Body(1);
        b.Line($"t = build{id}(n, {r.Range(1, 50)})");
        b.Line($"c = size{id}(t)");
        b.Line($"total{id}(t) {add} c");
        defs.Append($"def k{id}(n : Int32) : {lt}\n").Append(b).Append("end\n\n");
        Call($"k{id}({r.Range(0, 8)})");
    }

    void BlockKernel(int id)
    {
        features.Add("yield");
        var s = new Scope();
        s.Vars.Add(("x", T.I32)); s.Loop.Add("x");
        defs.Append($"def app{id}(n : Int32, &f : Int32 -> Int32) : Int32\n  t = 0\n  for i in 0...n\n    t = t &+ yield i\n  end\n  t\nend\n\n");
        var b = new Body(1);
        b.Line($"a = app{id}(n) {{ |x| {E(s, T.I32, 3)} }}");
        b.Line($"b = 0");
        b.Line($"n.times {{ |x| b = b ^ {E(s, T.I32, 2)} }}");
        b.Line($"c = (0...n).map {{ |x| {E(s, T.I32, 2)} }}.sum");
        b.Line($"sq = ->(x : Int32) {{ {E(s, T.I32, 2)} }}");
        b.Line("a &+ b &+ c &+ sq.call(n)");
        defs.Append($"def k{id}(n : Int32) : Int32\n").Append(b).Append("end\n\n");
        Call($"k{id}({r.Range(0, 12)})");
    }

    void ParForKernel(int id)
    {
        features.Add("par-for");
        features.Add("pure");
        var s = new Scope();
        s.Vars.Add(("i", T.I32)); s.Loop.Add("i");
        var pb = new Body(1);
        Locals(pb, s, r.Range(4, 5));
        Stmts(pb, s, 2, r.Range(1, 3), false);
        pb.Line(Checksum(s));
        defs.Append($"pure def pk{id}(i : Int32) : Int64\n").Append(pb).Append("end\n\n");
        int m = r.Pick(4, 17, 100, 1000, 70000);
        var b = new Body(1);
        b.Line($"{(r.Chance(50) ? "own " : "")}out = Array(Int64).new(n)");
        b.Open($"{(r.Chance(80) ? "par " : "")}for i in 0...n");
        b.Line($"out[i] = pk{id}(i)");
        b.Close();
        b.Line("t = 0_i64");
        b.Open("for v in out");
        b.Line("t = t &+ v");
        b.Close();
        b.Line("t");
        defs.Append($"def k{id}(n : Int32) : Int64\n").Append(b).Append("end\n\n");
        Call($"k{id}({m})");
    }

    void ForkKernel(int id)
    {
        features.Add("par-let");
        features.Add("pure");
        defs.Append($"pure def fw{id}(n : Int64) : Int64\n  s = 0_i64\n  for i in 0...n\n    s = s &+ (i &* {r.Range(1, 99)}_i64 ^ (s >> {r.Range(1, 9)}))\n  end\n  s\nend\n\n");
        defs.Append($"pure def fib{id}(n : Int32) : Int64\n  n < 2 ? n.to_i64 : fib{id}(n - 1) &+ fib{id}(n - 2)\nend\n\n");
        defs.Append($"def k{id}(n : Int32) : Int64\n  par a, b = fw{id}(n.to_i64), fw{id}(n.to_i64 &+ {r.Range(1, 9)}_i64)\n  a &+ b &+ fib{id}({r.Range(5, 20)})\nend\n\n");
        Call($"k{id}({r.Range(0, 3000)})");
    }

    void OwnKernel(int id)
    {
        features.Add("own");
        defs.Append($"def k{id}(n : Int32) : Int32\n  own prev = Array(Int32).new(n + 1)\n  for i in 0..n\n    prev[i] = i\n  end\n  for i in 1..n\n    own row = Array(Int32).new(n + 1)\n    row[0] = i\n    for j in 1..n\n      row[j] = Math.Min(prev[j] &+ {r.Range(1, 3)}, Math.Min(row[j - 1] &+ 1, prev[j - 1] &+ ((i &* {r.Range(1, 7)} ^ j) & 1)))\n    end\n    prev = row\n  end\n  prev[n]\nend\n\n");
        if (r.Chance(50))
        {
            features.Add("own-param");
            defs.Append($"def scale{id}(own xs : Array(Float64), k : Float64) : Array(Float64)\n  xs = xs.map {{ |v| v * k }}\n  xs\nend\n\n");
            defs.Append($"def tot{id}(borrow xs : Array(Float64)) : Float64\n  s = 0.0\n  for v in xs\n    s += v\n  end\n  s\nend\n\n");
            Call($"tot{id}(scale{id}(Array(Float64).new({r.Range(0, 9)}, {Lit(T.F64)}), {Lit(T.F64)}))");
        }
        Call($"k{id}({r.Range(0, 12)})");
    }

    void ClassKernel(int id)
    {
        features.Add("class");
        string cn = "C" + id;
        top.Append($"class {cn}\n  getter count : Int64\n  def initialize(@start : Int32)\n    @count = @start.to_i64\n  end\n  def bump(n : Int32) : Int64\n    @count = @count &* {r.Range(2, 5)}_i64 &+ n.to_i64\n    @count\n  end\nend\n\n");
        if (!avoidKnown && r.Chance(50))
        {
            features.Add("inheritance");
            top.Append($"class D{id} < {cn}\n  def bump(n : Int32) : Int64\n    @count = @count ^ n.to_i64\n    @count\n  end\nend\n\n");
        }
        var b = new Body(1);
        b.Line($"c = {(top.ToString().Contains($"class D{id}") && r.Chance(50) ? "D" + id : cn)}.new(n)");
        b.Open($"for i in 0...{r.Range(0, 9)}");
        b.Line("c.bump(i)");
        b.Close();
        b.Line("c.count");
        defs.Append($"def k{id}(n : Int32) : Int64\n").Append(b).Append("end\n\n");
        Call($"k{id}({r.Range(0, 9)})");
    }

    void StringRescueKernel(int id)
    {
        features.Add("string");
        features.Add("rescue");
        var s = new Scope();
        s.Vars.Add(("n", T.I32)); s.Loop.Add("n");
        var b = new Body(1);
        b.Line($"x = {E(s, T.I32, 2)}");
        s.Vars.Add(("x", T.I32));
        b.Line("t = \"k#{x}:#{n}\"");
        b.Open("begin");
        b.Line($"raise ArgumentException, \"neg #{{x}}\" if x < {r.Range(-5, 5)}");
        b.Line($"t = t + {(r.Chance(50) ? "x.to_s" : "\"pos\"")}");
        b.Indent--; b.Line("rescue e : ArgumentException"); b.Indent++;
        b.Line("t = t + e.message");
        b.Indent--; b.Line("ensure"); b.Indent++;
        b.Line("t = t + \"!\"");
        b.Close();
        b.Line($"y = (x {r.Pick("+", "*")} 2147483647 rescue -1)");
        b.Line("t + \" \" + t.size.to_s + \" \" + y.to_s + \" \" + t.upcase");
        defs.Append($"def k{id}(n : Int32) : String\n").Append(b).Append("end\n\n");
        Call($"k{id}({r.Range(-9, 9)})");
    }
}
