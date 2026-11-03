using System.Text;

namespace Alice.Fuzz;

/// <summary>
/// Grammar-based generator of Alice programs for the parser fuzzer (docs/language.md): expressions, statements, defs
/// (endless, modifiers, def-level rescue), classes, records, enums (sum types, affine), case/when and case/in
/// patterns, blocks (brace and do, numbered params, `it`), lambdas, strings (interpolation, single quotes, percent
/// literals, heredocs of every form), rescue forms (begin/rescue/else/ensure, modifier, retry) and loops. Programs are
/// syntactically plausible, not typed: only the parser and formatter see them. Layout is randomized (indentation,
/// blank lines, comments, trailing spaces) so `alice fmt` has work to do.
/// </summary>
public sealed class SyntaxGen(Rng r)
{
    readonly StringBuilder sb = new();
    readonly List<string> vars = ["a", "b", "xs", "s"];
    readonly List<string> pendingHeredocs = [];
    int indent;
    int loops, defs, rescues;
    int heredocN;

    public static string Generate(int seed) => new SyntaxGen(new Rng(seed)).Program();

    public string Program()
    {
        Line("a = 1");
        Line("b = 2.5");
        Line("xs = [1, 2, 3]");
        Line("s = \"str\"");
        int n = r.Range(3, 12);
        for (int i = 0; i < n; i++) Item(2);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ layout

    void Line(string text)
    {
        string pad = r.Chance(8) ? new string(' ', r.Next(7)) : new string(' ', indent * 2);
        sb.Append(pad).Append(text);
        if (r.Chance(5)) sb.Append("   # ").Append(r.Pick("note", "end", "if x", "\"q\"", "#{x}", "do |x|"));
        if (r.Chance(3)) sb.Append("  ");
        sb.Append('\n');
        foreach (var h in pendingHeredocs) sb.Append(h);
        pendingHeredocs.Clear();
        if (r.Chance(4)) sb.Append('\n');
        if (r.Chance(2)) sb.Append(new string(' ', indent * 2)).Append("# comment ").Append(r.Next(100)).Append('\n');
    }

    void Open(string text) { Line(text); indent++; }
    void Mid(string text) { indent--; Line(text); indent++; }
    void Close(string text = "end") { indent--; Line(text); }

    string Var() => r.PickOf(vars);

    string Fresh()
    {
        var v = r.Pick("x", "y", "z", "n", "acc", "tmp", "res", "val", "k", "q") + r.Next(5);
        if (!vars.Contains(v)) vars.Add(v);
        return v;
    }

    // ------------------------------------------------------------------ items

    void Item(int depth)
    {
        switch (r.Next(14))
        {
            case 0 or 1: Def(depth); break;
            case 2: Class(depth); break;
            case 3: Enum(); break;
            case 4: Record(); break;
            case 5: Module(depth); break;
            default: Stmt(depth); break;
        }
    }

    string Type(int d = 1) => r.Next(d > 0 ? 12 : 8) switch
    {
        0 => "Int32", 1 => "Int64", 2 => "Float64", 3 => "String", 4 => "Bool", 5 => "UInt8", 6 => "Char", 7 => "Int32?",
        8 => $"Array({Type(d - 1)})", 9 => $"List({Type(d - 1)})", 10 => $"Hash(String, {Type(d - 1)})",
        _ => $"Tuple({Type(d - 1)}, {Type(d - 1)})",
    };

    string Params()
    {
        int n = r.Next(4);
        var ps = new List<string>();
        for (int i = 0; i < n; i++)
        {
            string p = "p" + i;
            vars.Add(p);
            string mod = r.Chance(10) ? r.Pick("own ", "borrow ") : "";
            ps.Add(r.Chance(15) ? $"{mod}{p} : {Type()} = {Lit()}" : $"{mod}{p} : {Type()}");
        }
        if (r.Chance(10)) ps.Add("&blk : Int32 -> Int32");
        return ps.Count == 0 && r.Chance(50) ? "" : "(" + string.Join(", ", ps) + ")";
    }

    void Def(int depth, bool inType = false)
    {
        string mods = r.Next(10) switch { 0 => "pure ", 1 => "safe ", 2 => "private ", 3 => "inline ", 4 => "safe pure ", _ => "" };
        string name = r.Pick("f", "g", "calc", "ok", "run", "go", "area", "total") + r.Next(9);
        if (r.Chance(10)) name += r.Pick("?", "!");
        if (inType && r.Chance(15)) name = r.Pick("+", "<=>", "==", "[]", "-@", "to_s", "initialize", "each", "name=");
        string ret = r.Chance(60) ? " : " + Type() : "";
        int save = vars.Count;
        string self = inType && r.Chance(10) ? "self." : "";
        if (r.Chance(15) && char.IsLetter(name[0]) && !name.EndsWith('='))
        {
            Line($"{mods}def {self}{name}{Params()}{ret} = {Expr(2)}");
            vars.RemoveRange(save, vars.Count - save);
            return;
        }
        Open($"{mods}def {self}{name}{Params()}{ret}");
        defs++;
        Body(depth - 1, r.Range(1, 4));
        if (r.Chance(20))
        {
            rescues++;
            Mid(r.Pick("rescue e : ArgumentException", "rescue => e", "rescue ArgumentException, IOException => e", "rescue", "rescue e : IOException | OverflowException"));
            Body(0, 1);
            rescues--;
            if (r.Chance(30)) { Mid("ensure"); Body(0, 1); }
        }
        defs--;
        Close();
        vars.RemoveRange(save, vars.Count - save);
    }

    string Cap() => r.Pick("Point", "Shape", "Tree", "Node", "Counter", "Animal", "Dog", "Vec", "Box", "Item") + (r.Chance(50) ? r.Next(9).ToString() : "");

    void Class(int depth)
    {
        string kw = r.Pick("class", "class", "struct", "interface");
        string parent = r.Chance(30) ? " < " + Cap() : "";
        string gen = r.Chance(10) ? "(T)" : "";
        Open($"{kw} {Cap()}{gen}{parent}");
        int n = r.Range(0, 5);
        for (int i = 0; i < n; i++)
        {
            switch (r.Next(8))
            {
                case 0: Line($"{r.Pick("getter", "property", "setter")} {r.Pick("x", "y", "count", "name")} : {Type()}{(r.Chance(30) ? " = " + Lit() : "")}"); break;
                case 1: Line($"@{r.Pick("x", "y", "count")} : {Type()} = {Lit()}"); break;
                case 2: Line(r.Pick("include Comparable", "include Enumerable", "private", "protected", "attr_accessor :x", "attr_reader :x, :y")); break;
                case 3:
                    Open($"def initialize(@{r.Pick("x", "start")} : {Type(0)}{(r.Chance(50) ? ", @y : Int32" : "")})");
                    if (r.Chance(50)) Line("super()");
                    Close();
                    break;
                default: Def(depth - 1, inType: true); break;
            }
        }
        Close();
    }

    void Enum()
    {
        Open($"{(r.Chance(20) ? "affine " : "")}enum {Cap()}");
        int n = r.Range(1, 4);
        for (int i = 0; i < n; i++)
        {
            string v = r.Pick("Leaf", "Node", "Circle", "Rect", "Empty", "Red", "Green", "A", "B") + i;
            if (r.Chance(50)) Line(v);
            else Line($"{v}({string.Join(", ", Enumerable.Range(0, r.Range(1, 3)).Select(k => $"f{k} : {Type(0)}"))})");
        }
        // enums take no methods (the parser rejects them): only variants
        Close();
    }

    void Record()
    {
        string fields = string.Join(", ", Enumerable.Range(0, r.Range(1, 3)).Select(k => $"f{k} : {Type(0)}"));
        if (r.Chance(70)) Line($"record {(r.Chance(40) ? "struct " : "")}{Cap()}({fields})");
        else { Open($"record {Cap()}({fields})"); Def(1, inType: true); Close(); }
    }

    void Module(int depth)
    {
        Open($"{r.Pick("module", "namespace")} {Cap()}");
        Def(depth - 1, inType: true);
        Close();
    }

    // ------------------------------------------------------------------ statements

    void Body(int depth, int n)
    {
        for (int i = 0; i < n; i++) Stmt(depth);
    }

    void Stmt(int depth)
    {
        int k = depth <= 0 ? r.Next(10) : r.Next(36);
        switch (k)
        {
            case 0 or 1: Line($"{Fresh()} = {Expr(3)}"); break;
            case 2: Line($"{Var()} {r.Pick("+=", "-=", "*=", "||=", "&&=", "<<=", "|=")} {Expr(2)}"); break;
            case 3: Line($"puts {Expr(3)}"); break;
            case 4: Line($"{r.Pick("puts", "print", "p")} {Expr(2)}, {Expr(2)}"); break;
            case 5: Line($"{Expr(2)} {r.Pick("if", "unless")} {Cond(2)}"); break;
            case 6: Line(Call(2)); break;
            case 7:
                if (loops > 0) Line(r.Pick("break", "next", $"break if {Cond(1)}", $"next unless {Cond(1)}"));
                else if (defs > 0) Line(r.Pick("return", $"return {Expr(2)}", $"return {Expr(1)} if {Cond(1)}"));
                else Line($"{Var()} = {Expr(1)}");
                break;
            case 8: Line($"{Fresh()} = {Expr(2)} rescue {Expr(1)}"); break;
            case 9: Line($"{Var()}[{Expr(1)}] = {Expr(2)}"); break;
            case 10 or 11: If(depth); break;
            case 12: While(depth); break;
            case 13: For(depth); break;
            case 14 or 15: Case(depth); break;
            case 16: CaseIn(depth); break;
            case 17 or 18: Begin(depth); break;
            case 19 or 20: BlockCall(depth); break;
            case 21: Heredoc(); break;
            case 22:
                Open($"{Fresh()} = loop do");
                loops++;
                Body(depth - 1, r.Range(1, 2));
                Line($"break {Expr(1)} if {Cond(1)}");
                loops--;
                Close();
                break;
            case 23:
                Open("begin");
                loops++;
                Body(depth - 1, r.Range(1, 2));
                loops--;
                Close($"end {r.Pick("while", "until")} {Cond(1)}");
                break;
            case 24: Line($"{Var()} += 1 {r.Pick("while", "until")} {Cond(1)}"); break;
            case 25:
                Open($"par for {Fresh()} in 0...{r.Range(1, 100)}");
                loops++;
                Line($"{Var()}[{Var()}] = {Expr(2)}");
                loops--;
                Close();
                break;
            case 26: Line($"own {Fresh()} = Array({Type(0)}).new({r.Range(0, 9)})"); break;
            case 27: Line($"par {Fresh()}, {Fresh()} = {Call(1)}, {Call(1)}"); break;
            case 28: Line(r.Pick("raise \"bad\"", "raise ArgumentException, \"bad #{" + Var() + "}\"", "raise ArgumentException.new(\"x\")") + (r.Chance(50) ? $" if {Cond(1)}" : "")); break;
            case 29:
                if (rescues > 0) Line(r.Pick("retry", "raise"));
                else Line($"{Fresh()} = ({Expr(2)} rescue {Expr(1)})");
                break;
            case 30: Open($"{r.Pick("until", "unless")} {Cond(2)}"); loops++; Body(depth - 1, 1); loops--; Close(); break;
            case 31: Line($"{Fresh()}, {Fresh()} = {Expr(1)}, {Expr(1)}"); break;
            case 32: Open(r.Pick("scope", "unchecked")); Body(depth - 1, r.Range(1, 2)); Close(); break;
            case 33: Line($"{Fresh()} = ->({Fresh()} : Int32) {{ {Expr(2)} }}"); break;
            case 34: Line($"{Fresh()} = if {Cond(1)} then {Expr(1)} else {Expr(1)} end"); break;
            default: Line($"{Var()} = {Expr(3)}"); break;
        }
    }

    void If(int depth)
    {
        Open($"{r.Pick("if", "if", "unless")} {Cond(2)}{(r.Chance(10) ? " then" : "")}");
        Body(depth - 1, r.Range(1, 3));
        int elsifs = r.Next(3);
        for (int i = 0; i < elsifs; i++) { Mid($"elsif {Cond(2)}"); Body(depth - 1, r.Range(1, 2)); }
        if (r.Chance(50)) { Mid("else"); Body(depth - 1, r.Range(1, 2)); }
        Close();
    }

    void While(int depth)
    {
        Open($"{r.Pick("while", "until")} {Cond(2)}{(r.Chance(10) && loops == 0 ? " do" : "")}");
        loops++;
        Body(depth - 1, r.Range(1, 3));
        loops--;
        Close();
    }

    void For(int depth)
    {
        string head = r.Next(5) switch
        {
            0 => $"for {Fresh()} in 0...{Expr(1)}",
            1 => $"for {Fresh()} in 1..{r.Range(0, 9)}",
            2 => $"for {Fresh()}, {Fresh()} in {Var()}",
            3 => $"for {Fresh()} in {Var()}",
            _ => $"for {Fresh()} in ({Expr(1)}..{Expr(1)}).step({r.Range(1, 3)})",
        };
        Open(head);
        loops++;
        Body(depth - 1, r.Range(1, 3));
        loops--;
        Close();
    }

    void Case(int depth)
    {
        bool asValue = r.Chance(30);
        Open(asValue ? $"{Fresh()} = case {Expr(1)}" : r.Chance(10) ? "case" : $"case {Expr(1)}");
        int n = r.Range(1, 4);
        for (int i = 0; i < n; i++)
        {
            string pat = r.Next(6) switch
            {
                0 => $"{Lit()}, {Lit()}",
                1 => $"{r.Range(0, 5)}..{r.Range(5, 9)}",
                2 => $"{Cap()}::{r.Pick("Circle", "Leaf", "Node")}({Fresh()})",
                3 => $"{r.Range(0, 5)}..",
                4 => r.Pick("String", "Int32", "nil"),
                _ => Lit(),
            };
            if (r.Chance(40)) Mid($"when {pat} then {Expr(1)}");
            else { Mid($"when {pat}"); Body(depth - 1, r.Range(1, 2)); }
        }
        if (r.Chance(60)) { Mid("else"); Body(0, 1); }
        Close();
    }

    void CaseIn(int depth)
    {
        Open($"case {Expr(1)}");
        int n = r.Range(1, 4);
        for (int i = 0; i < n; i++)
        {
            string pat = r.Next(10) switch
            {
                0 => $"[{Fresh()}, {Fresh()}]",
                1 => $"[{Fresh()}, *{Fresh()}]",
                2 => "[]",
                3 => $"^{Var()}",
                4 => $"{Lit()} | {Lit()} | {Lit()}",
                5 => $"{Cap()}::{r.Pick("Circle", "Rect")}({Fresh()}) if {Var()} > 1",
                6 => $"{{\"k\" => {Fresh()}}}",
                7 => $"..{r.Range(0, 9)}",
                8 => "_",
                _ => Fresh(),
            };
            Mid($"in {pat}{(r.Chance(30) ? " then " + Expr(1) : "")}");
            Body(depth - 1, 1);
        }
        if (r.Chance(40)) { Mid("else"); Body(0, 1); }
        Close();
    }

    void Begin(int depth)
    {
        Open("begin");
        Body(depth - 1, r.Range(1, 3));
        int n = r.Range(0, 3);
        rescues++;
        for (int i = 0; i < n; i++)
        {
            Mid(r.Pick("rescue", "rescue e", "rescue e : OverflowException", "rescue => e", "rescue ArgumentException => e",
                "rescue ArgumentException, IndexOutOfRangeException => e", "rescue e : IOException | FormatException"));
            Body(depth - 1, r.Range(1, 2));
        }
        rescues--;
        if (n > 0 && r.Chance(25)) { Mid("else"); Body(0, 1); }
        if (n == 0 || r.Chance(40)) { Mid("ensure"); Body(0, 1); }
        Close();
    }

    void BlockCall(int depth)
    {
        string recv = r.Pick("xs", "(0...5)", "[1, 2]", Var(), "3");
        string meth = r.Pick("each", "map", "select", "each_with_index", "times", "reject", "each_slice(2)", "inject(0)", "sort_by", "group_by");
        string ps = meth switch { "each_with_index" or "inject(0)" => $"|{Fresh()}, {Fresh()}|", _ => r.Chance(15) ? "" : $"|{Fresh()}|" };
        if (r.Chance(50))
        {
            string body = ps == "" ? r.Pick("it * 2", "_1 + 1", "it") : Expr(2);
            Line($"{(r.Chance(40) ? Fresh() + " = " : "")}{recv}.{meth} {{ {ps}{(ps == "" ? "" : " ")}{body} }}");
        }
        else
        {
            Open($"{recv}.{meth} do {ps}");
            loops++;
            Body(depth - 1, r.Range(1, 3));
            loops--;
            if (r.Chance(15)) { rescues++; Mid("rescue e"); Body(0, 1); rescues--; }
            Close();
        }
    }

    void Heredoc()
    {
        string id = r.Pick("EOS", "TEXT", "SQL", "END_") + heredocN++;
        string form = r.Pick("<<~", "<<-", "<<");
        bool raw = r.Chance(25);
        string open = form + (raw ? "'" + id + "'" : id);
        var body = new StringBuilder();
        int n = r.Range(0, 4);
        string pad = form == "<<" ? "" : new string(' ', (indent + 1) * 2);
        for (int i = 0; i < n; i++)
            body.Append(pad).Append(r.Pick("line one", "  indented #{a}", "x = #{a + 1}", "end", "if x", "\"quoted\"", "  # not a comment", "", "\\t tab")).Append('\n');
        body.Append(form == "<<" ? "" : new string(' ', indent * 2)).Append(id).Append('\n');
        pendingHeredocs.Add(body.ToString());
        Line(r.Next(3) switch
        {
            0 => $"{Fresh()} = {open}",
            1 => $"puts {open}{(r.Chance(30) ? ".strip" : "")}",
            _ => $"{Fresh()} = [{open}, {Lit()}]",
        });
    }

    // ------------------------------------------------------------------ expressions

    string Cond(int d) => r.Next(6) switch
    {
        0 => $"{Expr(d - 1)} {r.Pick("==", "!=", "<", "<=", ">", ">=")} {Expr(d - 1)}",
        1 when d > 0 => $"{Cond(d - 1)} {r.Pick("&&", "||", "and", "or")} {Cond(d - 1)}",
        2 => $"!{Atom()}",
        3 => $"{Var()}.{r.Pick("empty?", "nil?", "even?", "includes?(1)", "zero?")}",
        4 => $"s =~ /{r.Pick("a+", "\\d+", "[a-z]*", "x|y")}/{r.Pick("", "i", "m")}",
        _ => r.Pick("true", "false", Var()),
    };

    public string Expr(int d)
    {
        if (d <= 0) return Atom();
        return r.Next(22) switch
        {
            0 or 1 or 2 => $"{Expr(d - 1)} {r.Pick("+", "-", "*", "/", "%", "**", "&+", "&-", "&*", "&", "|", "^", "<<", ">>", "<=>", "==", "..", "...")} {Expr(d - 1)}",
            3 => $"({Expr(d - 1)})",
            4 => $"{r.Pick("-", "!", "~", "+")}{Atom()}",
            5 => $"{Cond(d - 1)} ? {Expr(d - 1)} : {Expr(d - 1)}",
            6 => Call(d - 1),
            7 => $"{Atom()}.{r.Pick("size", "to_s", "abs", "to_i64", "to_f", "first", "upcase", "max", "sum", "reverse", "to_a", "keys", "inspect", "to_i32!")}",
            8 => $"{Atom()}[{Expr(d - 1)}]",
            9 => $"{Var()}[{r.Pick("1..", "..2", "...2", "1, 2", "-1", "1..3")}]",
            10 => r.Chance(15) ? "[] of Int32" : $"[{string.Join(", ", Enumerable.Range(0, r.Range(1, 3)).Select(_ => Expr(d - 1)))}]",
            11 => $"{{{string.Join(", ", Enumerable.Range(0, r.Range(1, 3)).Select(_ => Str() + " => " + Expr(d - 1)))}}}",
            12 => $"{{{string.Join(", ", Enumerable.Range(0, r.Range(1, 3)).Select(i => "k" + i + ": " + Expr(d - 1)))}}}",
            13 => Str(d),
            14 => $"{Var()}.map {{ |{Fresh()}| {Expr(d - 1)} }}",
            15 => $"->({Fresh()} : Int32) {{ {Expr(d - 1)} }}",
            16 => $"{Cap()}.new({Expr(d - 1)})",
            17 => $"{Cap()}::{r.Pick("Circle", "Leaf", "Red")}({Expr(d - 1)})",
            18 => $"{Var()}.{r.Pick("sum", "count", "map", "select")}(&:{r.Pick("to_s", "abs", "even?")})",
            19 => $"{Atom()} {r.Pick("&&", "||", "and", "or")} {Atom()}",
            20 => $"{Var()}.{r.Pick("fetch", "dig", "zip", "push")}({Expr(d - 1)}, {Expr(d - 1)})",
            _ => Atom(),
        };
    }

    string Call(int d) => r.Next(6) switch
    {
        0 => $"f{r.Next(9)}({string.Join(", ", Enumerable.Range(0, r.Range(0, 3)).Select(_ => Expr(d)))})",
        1 => $"f{r.Next(9)} {Expr(d)}",
        2 => $"{Var()}.{r.Pick("push", "includes?", "index", "fetch", "sub")}({Expr(d)})",
        3 => $"Math.{r.Pick("Min", "Max", "sqrt", "Abs")}({Expr(d)}, {Expr(d)})",
        4 => $"format(\"%{r.Pick("d", "s", "05d", ".2f", "x")}\", {Expr(d)})",
        _ => $"{Var()}.{r.Pick("each_slice", "step", "first", "take")}({r.Range(1, 3)})",
    };

    string Atom() => r.Next(12) switch
    {
        0 or 1 or 2 or 3 => Var(),
        4 or 5 => Lit(),
        6 => Str(0),
        7 => r.Pick("nil", "self", "__LINE__", "__method__", "Int32::MAX", "Float64::NAN", "ARGV.size"),
        8 => ":" + r.Pick("sym", "+", "<=>", "[]", "name?"),
        9 => r.Pick("'a'", "'\\n'", "'\\u{41}'", "'\\\\'", "'\\''"),
        10 => Paren("%" + r.Pick("w", "i") + r.Pick("[a b]", "(x y z)", "{p}", "<l m>", "|q r|")),
        _ => "/" + r.Pick("a+b", "\\d{2,3}", "(?<n>x)", "[^a]") + "/" + r.Pick("", "i", "x"),
    };

    string Lit() => r.Next(10) switch
    {
        0 => r.Range(-5, 100).ToString(),
        1 => r.Pick("0", "1", "2147483647", "1_000_000", "0x1F", "0b1010"),
        2 => r.Range(0, 99) + r.Pick("_i64", "_u8", "_u32", "_i16", "_f32", "_i8"),
        3 => r.Pick("1.5", "0.0", "-2.25", "1e10", "3.14_f32", "1.0e-07"),
        4 => r.Pick("true", "false", "nil"),
        5 => r.Pick("'c'", "'\\t'", "'\\0'"),
        _ => r.Range(0, 9).ToString(),
    };

    string Str(int d = 0)
    {
        string inner = d > 0 && r.Chance(60) ? "#{" + Expr(d - 1) + "}" : r.Pick("x", "#{a}", "");
        return r.Next(12) switch
        {
            0 => r.Pick("'plain'", "'it\\'s'", "'back\\\\slash'", "'#{no}'"),
            1 => Paren("%q(" + r.Pick("raw (nested) text", "#{not}", "a\\)b") + ")"),
            2 => Paren("%Q[x=" + inner + "]"),
            3 => Paren("%(" + r.Pick("p", inner) + ")"),
            4 => Paren(r.Pick("%q{text}", "%Q<text>", "%q|text|", "%Q!text #{a}!", "%{brace {nested} }")),
            5 => "\"a\" \"" + inner + "\"",
            6 => "\"" + r.Pick("\\n", "\\t", "\\u{1F600}", "\\x41", "\\101", "\\e", "\\s") + inner + "\"",
            7 => "\"multi\n" + r.Pick("  line", "end", "    if x") + "\"",
            _ => "\"" + r.Pick("hello", "a b", "", "#", "x#y") + inner + "\"",
        };
    }

    /// <summary>A percent literal right after an operator is rejected today (finding P6): mostly parenthesize it, so
    /// programs stay parseable, but keep some bare ones to cover the lexer path.</summary>
    string Paren(string s) => r.Chance(85) ? "(" + s + ")" : s;
}
