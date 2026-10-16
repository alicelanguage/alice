using System.Text;

namespace Alice.Compiler;

/// <summary>Cross-file facts used to resolve deferred markers after every file is parsed.</summary>
public sealed class Globals
{
    public readonly HashSet<string> ZeroArg = new();
    public readonly HashSet<string> Props = new();
    public readonly Dictionary<string, string> BaseOf = new();
    public readonly Dictionary<string, HashSet<string>> Methods = new();

    const string PreludeNames = "puts print p gets size length count first last empty_q any_q sum max min sort reverse uniq to_a to_list to_i to_f to_i64 to_f32 chars upcase downcase capitalize strip lines abs even_q odd_q zero_q ord chr keys values join succ pred floor ceil round sqrt inspect rand exit pop receive close stop wait";

    public Globals()
    {
        foreach (var n in PreludeNames.Split(' ')) ZeroArg.Add(n);
    }

    public void AddMethod(string cls, string name)
    {
        if (!Methods.ContainsKey(cls)) Methods[cls] = new HashSet<string>();
        Methods[cls].Add(name);
    }

    bool AncestorHas(string cls, string name)
    {
        string b = BaseOf.ContainsKey(cls) ? BaseOf[cls] : "";
        int guard = 0;
        while (b != "" && guard < 64)
        {
            if (Methods.ContainsKey(b) && Methods[b].Contains(name)) return true;
            b = BaseOf.ContainsKey(b) ? BaseOf[b] : "";
            guard++;
        }
        return false;
    }

    bool HasSubclass(string cls)
    {
        foreach (var kv in BaseOf) if (kv.Value == cls) return true;
        return false;
    }

    /// <summary>Replaces \u0001M:name\u0001 (member-or-call) and \u0001V:cls:name\u0001 (virtual/override) markers.</summary>
    public string Resolve(string text)
    {
        var sb = new StringBuilder();
        int i = 0;
        while (i < text.Length)
        {
            int s = text.IndexOf('\u0001', i);
            if (s < 0) { sb.Append(text, i, text.Length - i); break; }
            sb.Append(text, i, s - i);
            int e = text.IndexOf('\u0001', s + 1);
            string body = text.Substring(s + 1, e - s - 1);
            if (body.StartsWith("M:"))
            {
                string n = body.Substring(2);
                string bare = n.StartsWith("@") ? n.Substring(1) : n;
                sb.Append(n);
                if (ZeroArg.Contains(bare) && !Props.Contains(bare)) sb.Append("()");
            }
            else
            {
                int c = body.IndexOf(':', 2);
                string cls = body.Substring(2, c - 2);
                string n = body.Substring(c + 1);
                if (AncestorHas(cls, n)) sb.Append("override ");
                else if (HasSubclass(cls)) sb.Append("virtual ");
            }
            i = e + 1;
        }
        return sb.ToString();
    }
}

/// <summary>A lowered statement. If-chains stay structured so tail position can push `return` into branches.</summary>
public sealed class Stmt
{
    public int Line;
    public string Code = "";
    public string Expr = "";
    public bool HasExpr;
    public string Kind = "stmt";
    public string Pre = "";
    public List<string>? Conds;
    public List<List<Stmt>>? Bodies;
    public List<Stmt>? Else;
}

sealed class ClassCtx
{
    public string Name = "Program";
    public string Kind = "program";
    public string Generic = "";
    public HashSet<string> Members = new();
    public List<string> AutoFields = new();
    public HashSet<string> Ops = new();
    public List<string> Bases = new();
}

/// <summary>Single-pass Alice -> C# lowering. Roslyn then emits IL; C# text is an internal representation.</summary>
public sealed class Translator
{
    readonly Globals g;
    readonly string file;
    List<Token> t = new();
    int p;
    List<Dictionary<string, string>> scopes = new();
    List<string> ctx = new();
    ClassCtx cur = new();
    string method = "";
    string methodArgs = "";
    bool noDo;
    bool lastIsType;
    string kind = "value";
    int tmp;
    readonly List<string> attrs = new();

    const string Keywords = " if unless while until for in do end then else elsif case when begin rescue ensure return break next and or not def class struct module interface enum lib fun namespace using require getter property setter include abstract private protected self nil true false super yield loop raise of forall spawn ";
    const string CsKeywords = " abstract as base bool break byte case catch char checked class const continue decimal default delegate do double else enum event explicit extern false finally fixed float for foreach goto if implicit in int interface internal is lock long namespace new null object operator out override params private protected public readonly ref return sbyte sealed short sizeof stackalloc static string struct switch this throw true try typeof uint ulong unchecked unsafe ushort using virtual void volatile while ";
    const string Levels = "|| ;&& ;== != === ;< > <= >= <=> ;| ;^ ;& ;<< >> ;+ - ;* / % ";

    public Translator(Globals g, string file)
    {
        this.g = g;
        this.file = file;
    }

    // ---------------------------------------------------------------- tokens
    Token Cur() => t[p];
    Token Peek(int k) => t[Math.Min(p + k, t.Count - 1)];
    bool IsOp(string s) => t[p].Kind == "op" && t[p].Text == s;
    bool IsKw(string s) => t[p].Kind == "id" && t[p].Text == s;

    Token Next()
    {
        Token x = t[p];
        if (p < t.Count - 1) p++;
        return x;
    }

    CompileException Err(string msg)
    {
        Token c = t[p];
        string found = c.Kind == "nl" ? "newline" : c.Kind == "eof" ? "end of file" : c.Text;
        return new CompileException($"{file}:{c.Line}:{c.Col}: {msg} (found '{found}')");
    }

    void Expect(string op)
    {
        if (!IsOp(op)) throw Err($"expected '{op}'");
        Next();
    }

    void ExpectKw(string kw)
    {
        if (!IsKw(kw)) throw Err($"expected '{kw}'");
        Next();
    }

    void SkipNl()
    {
        while (t[p].Kind == "nl") Next();
    }

    bool AtStmtEnd()
    {
        Token c = t[p];
        if (c.Kind == "nl" || c.Kind == "eof" || IsOp("}")) return true;
        return c.Kind == "id" && " end else elsif when rescue ensure if unless ".Contains(" " + c.Text + " ");
    }

    void EndStmt()
    {
        if (t[p].Kind == "nl") { SkipNl(); return; }
        if (t[p].Kind == "eof" || IsOp("}")) return;
        if (t[p].Kind == "id" && " end else elsif when rescue ensure ".Contains(" " + t[p].Text + " ")) return;
        throw Err("expected end of statement");
    }

    static bool IsKeyword(string s) => Keywords.Contains(" " + s + " ");

    public static string Mangle(string n)
    {
        if (n.EndsWith("?")) n = n.Substring(0, n.Length - 1) + "_q";
        else if (n.EndsWith("!")) n = n.Substring(0, n.Length - 1) + "_b";
        if (CsKeywords.Contains(" " + n + " ")) return "@" + n;
        return n;
    }

    // ---------------------------------------------------------------- scopes
    void PushScope() => scopes.Add(new Dictionary<string, string>());
    void PopScope() => scopes.RemoveAt(scopes.Count - 1);

    string Declare(string name)
    {
        string cs = Mangle(name);
        if (scopes.Count > 1)
        {
            tmp++;
            cs = cs.TrimStart('@') + "_" + tmp;
        }
        scopes[scopes.Count - 1][name] = cs;
        return cs;
    }

    bool IsLocal(string name)
    {
        for (int i = scopes.Count - 1; i >= 0; i--) if (scopes[i].ContainsKey(name)) return true;
        return false;
    }

    string Local(string name)
    {
        for (int i = scopes.Count - 1; i >= 0; i--) if (scopes[i].ContainsKey(name)) return scopes[i][name];
        return name;
    }

    // ---------------------------------------------------------------- file
    public string TranslateFile(string src)
    {
        t = new Lexer(src, file, 1).Lex();
        p = 0;
        var usings = new StringBuilder();
        var types = new StringBuilder();
        var prog = new StringBuilder();
        var main = new List<Stmt>();
        scopes = new List<Dictionary<string, string>>();
        PushScope();
        scopes[0]["ARGV"] = "ARGV";
        ParseItems(types, prog, main, usings, true);
        if (t[p].Kind != "eof") throw Err("unexpected 'end'");
        var sb = new StringBuilder();
        sb.Append("// <auto-generated> Alice compiler </auto-generated>\n");
        sb.Append("#nullable enable\n#pragma warning disable CS0105, CS0162, CS0168, CS0219, CS0618, CS0660, CS0661, CS8618, CS8632, CS1717\n");
        sb.Append("using System;\nusing System.Collections.Generic;\nusing System.Linq;\nusing System.Text;\nusing System.IO;\nusing System.Threading;\nusing System.Threading.Tasks;\nusing Alice.Runtime;\nusing static Alice.Runtime.Prelude;\nusing static Program;\n");
        sb.Append(usings);
        sb.Append(types);
        sb.Append("public static partial class Program\n{\n");
        sb.Append(prog);
        if (main.Count > 0)
        {
            sb.Append("public static void Main(string[] ARGV)\n{\nPrelude.init();\n");
            sb.Append(Render(main, 0, 1));
            sb.Append("}\n");
        }
        sb.Append("}\n");
        return sb.ToString();
    }

    void ParseItems(StringBuilder types, StringBuilder? prog, List<Stmt>? main, StringBuilder usings, bool top)
    {
        while (true)
        {
            SkipNl();
            if (t[p].Kind == "eof" || IsKw("end")) return;
            if (IsKw("using"))
            {
                Next();
                usings.Append("using " + DottedName() + ";\n");
                EndStmt();
            }
            else if (IsKw("require"))
            {
                Next();
                Next();
                EndStmt();
            }
            else if (IsOp("@[")) ParseAttr();
            else if (IsKw("namespace"))
            {
                Next();
                string name = DottedName();
                var inner = new StringBuilder();
                var innerUsings = new StringBuilder();
                ParseItems(inner, null, null, innerUsings, false);
                ExpectKw("end");
                types.Append($"namespace {name}\n{{\n{innerUsings}{inner}}}\n");
            }
            else if (IsTypeStart()) types.Append(ParseTypeDecl());
            else if (IsKw("def") || ((IsKw("private") || IsKw("protected")) && Peek(1).Text == "def"))
            {
                if (prog == null) throw Err("methods must be declared inside a type in a namespace");
                prog.Append(ParseDef());
            }
            else if (t[p].Kind == "const" && Peek(1).Kind == "op" && (Peek(1).Text == "=" || Peek(1).Text == ":"))
            {
                if (prog == null) throw Err("constants must be declared inside a type in a namespace");
                prog.Append(ParseConst());
            }
            else
            {
                if (main == null) throw Err("statements are not allowed here");
                main.Add(ParseStatement());
            }
        }
    }

    bool IsTypeStart()
    {
        if (IsKw("abstract") && Peek(1).Kind == "id" && Peek(1).Text != "def") return true;
        return t[p].Kind == "id" && " class struct module interface enum lib ".Contains(" " + t[p].Text + " ");
    }

    string DottedName()
    {
        if (t[p].Kind != "const" && t[p].Kind != "id") throw Err("expected name");
        string n = Next().Text;
        while (IsOp(".") || IsOp("::"))
        {
            Next();
            n += "." + Next().Text;
        }
        return n;
    }

    void ParseAttr()
    {
        Next();
        var sb = new StringBuilder("[");
        int d = 1;
        while (true)
        {
            Token c = t[p];
            if (c.Kind == "eof") throw Err("unterminated attribute");
            if (c.Kind == "op" && (c.Text == "[" || c.Text == "(")) d++;
            if (c.Kind == "op" && (c.Text == "]" || c.Text == ")")) d--;
            if (d == 0) { Next(); break; }
            if (c.Space && sb.Length > 1) sb.Append(' ');
            if (c.Kind == "str") sb.Append('"').Append(c.Text).Append('"');
            else if (c.Kind == "op" && c.Text == "::") sb.Append('.');
            else sb.Append(c.Text);
            Next();
        }
        sb.Append(']');
        attrs.Add(sb.ToString());
        SkipNl();
    }

    string TakeAttrs()
    {
        var sb = new StringBuilder();
        foreach (var a in attrs) sb.Append(a).Append('\n');
        attrs.Clear();
        return sb.ToString();
    }

    // ---------------------------------------------------------------- types
    public static string MapType(string n)
    {
        switch (n)
        {
            case "Int8": return "sbyte";
            case "Int16": return "short";
            case "Int32": return "int";
            case "Int": return "int";
            case "Int64": return "long";
            case "UInt8": return "byte";
            case "UInt16": return "ushort";
            case "UInt32": return "uint";
            case "UInt64": return "ulong";
            case "Float32": return "float";
            case "Float64": return "double";
            case "Float": return "double";
            case "Bool": return "bool";
            case "String": return "string";
            case "Char": return "char";
            case "Object": return "object";
            case "Any": return "dynamic";
            case "Nil": return "void";
            case "Void": return "void";
        }
        return n;
    }

    string ParseType()
    {
        if (IsOp("{"))
        {
            Next();
            var parts = new List<string>();
            parts.Add(ParseType());
            while (IsOp(",")) { Next(); parts.Add(ParseType()); }
            Expect("}");
            return "(" + string.Join(", ", parts) + ")";
        }
        if (t[p].Kind != "const") throw Err("expected type");
        string name = Next().Text;
        bool nullable = false;
        if (name.EndsWith("?")) { nullable = true; name = name.Substring(0, name.Length - 1); }
        while (IsOp("::") || (IsOp(".") && Peek(1).Kind == "const"))
        {
            Next();
            string seg = Next().Text;
            if (seg.EndsWith("?")) { nullable = true; seg = seg.Substring(0, seg.Length - 1); }
            name += "." + seg;
        }
        string r = MapType(name);
        if (IsOp("(") && !t[p].Space)
        {
            Next();
            var args = new List<string>();
            args.Add(ParseType());
            while (IsOp(",")) { Next(); args.Add(ParseType()); }
            Expect(")");
            r = GenericType(name, args);
        }
        if (IsOp("?") && !t[p].Space) { Next(); nullable = true; }
        if (IsOp("*") && !t[p].Space) { Next(); r += "*"; }
        if (nullable) r += "?";
        return r;
    }

    static string GenericType(string name, List<string> args)
    {
        if (name == "Array") return args[0] + "[]";
        if (name == "Pointer") return args[0] + "*";
        if (name == "Tuple") return "(" + string.Join(", ", args) + ")";
        if (name == "Proc")
        {
            string ret = args[args.Count - 1];
            args.RemoveAt(args.Count - 1);
            if (ret == "void") return args.Count == 0 ? "Action" : "Action<" + string.Join(", ", args) + ">";
            args.Add(ret);
            return "Func<" + string.Join(", ", args) + ">";
        }
        return MapType(name) + "<" + string.Join(", ", args) + ">";
    }

    string ParseTypeDecl()
    {
        bool isAbstract = false;
        if (IsKw("abstract")) { Next(); isAbstract = true; }
        string kw = Next().Text;
        if (kw == "lib") return ParseLib();
        if (kw == "enum") return ParseEnum();
        if (t[p].Kind != "const") throw Err("expected type name");
        string name = Next().Text;
        string gen = "";
        if (IsOp("(") && !t[p].Space)
        {
            Next();
            var ps = new List<string>();
            ps.Add(Next().Text);
            while (IsOp(",")) { Next(); ps.Add(Next().Text); }
            Expect(")");
            gen = "<" + string.Join(", ", ps) + ">";
        }
        var outer = cur;
        cur = new ClassCtx { Name = name, Kind = kw, Generic = gen };
        if (IsOp("<"))
        {
            Next();
            cur.Bases.Add(ParseType());
            while (IsOp(",")) { Next(); cur.Bases.Add(ParseType()); }
        }
        string attr = TakeAttrs();
        if (kw == "class")
        {
            string b = cur.Bases.Count > 0 ? SimpleName(cur.Bases[0]) : "";
            if (b != "" || !g.BaseOf.ContainsKey(name)) g.BaseOf[name] = b;
        }
        string body = ParseBody();
        ExpectKw("end");
        string head;
        if (kw == "module") head = "public static partial class";
        else if (kw == "interface") head = "public partial interface";
        else if (kw == "struct") head = "public partial struct";
        else if (isAbstract) head = "public abstract partial class";
        else head = "public partial class";
        string bases = cur.Bases.Count > 0 ? " : " + string.Join(", ", cur.Bases) : "";
        cur = outer;
        return $"{attr}{head} {name}{gen}{bases}\n{{\n{body}}}\n";
    }

    static string SimpleName(string type)
    {
        int lt = type.IndexOf('<');
        if (lt >= 0) type = type.Substring(0, lt);
        int dot = type.LastIndexOf('.');
        return dot >= 0 ? type.Substring(dot + 1) : type;
    }

    string ParseBody()
    {
        var sb = new StringBuilder();
        bool st = cur.Kind == "module";
        string stat = st ? "static " : "";
        while (true)
        {
            SkipNl();
            if (IsKw("end") || t[p].Kind == "eof") break;
            if (IsOp("@[")) ParseAttr();
            else if (IsKw("include"))
            {
                Next();
                cur.Bases.Add(ParseType());
                EndStmt();
            }
            else if (IsKw("getter") || IsKw("property") || IsKw("setter"))
            {
                string acc = Next().Text;
                string name = Next().Text;
                string type;
                string init = "";
                if (IsOp(":")) { Next(); type = ParseType(); }
                else type = "";
                if (IsOp("="))
                {
                    Next();
                    Token lit = t[p];
                    init = " = " + FreshExpr() + ";";
                    if (type == "") type = LiteralType(lit);
                }
                if (type == "") throw Err("type required for " + acc + " " + name);
                string a = acc == "getter" ? "{ get; private set; }" : acc == "setter" ? "{ private get; set; }" : "{ get; set; }";
                if (cur.Kind == "interface") a = acc == "getter" ? "{ get; }" : "{ get; set; }";
                g.Props.Add(name);
                cur.Members.Add(name);
                string pub = cur.Kind == "interface" ? "" : "public ";
                sb.Append($"{TakeAttrs()}{pub}{stat}{type} {Mangle(name)} {a}{init}\n");
                EndStmt();
            }
            else if (t[p].Kind == "ivar")
            {
                string name = Next().Text;
                Expect(":");
                string type = ParseType();
                string init = "";
                if (IsOp("=")) { Next(); init = " = " + FreshExpr(); }
                cur.Members.Add(name);
                sb.Append($"{TakeAttrs()}private {stat}{type} {Mangle(name)}{init};\n");
                EndStmt();
            }
            else if (t[p].Kind == "const" && Peek(1).Kind == "op" && (Peek(1).Text == "=" || Peek(1).Text == ":"))
                sb.Append(ParseConst());
            else if (IsTypeStart()) sb.Append(ParseTypeDecl());
            else if (IsKw("def") || IsKw("abstract") || IsKw("private") || IsKw("protected")) sb.Append(ParseDef());
            else throw Err("unexpected token in type body");
        }
        foreach (var f in cur.AutoFields)
        {
            int sp = f.LastIndexOf(' ');
            string n = f.Substring(sp + 1);
            if (!cur.Members.Contains(n))
            {
                cur.Members.Add(n);
                sb.Append($"private {f};\n");
            }
        }
        string self = cur.Name + cur.Generic;
        if (cur.Ops.Contains("==") && !cur.Ops.Contains("!=")) sb.Append($"public static bool operator !=({self} a, {self} b) => !(a == b);\n");
        if (cur.Ops.Contains("<") && !cur.Ops.Contains(">")) sb.Append($"public static bool operator >({self} a, {self} b) => b < a;\n");
        if (cur.Ops.Contains("<=") && !cur.Ops.Contains(">=")) sb.Append($"public static bool operator >=({self} a, {self} b) => b <= a;\n");
        return sb.ToString();
    }

    static string LiteralType(Token lit)
    {
        if (lit.Kind == "int") return lit.Text.EndsWith("L") ? "long" : "int";
        if (lit.Kind == "float") return lit.Text.EndsWith("f") ? "float" : "double";
        if (lit.Kind == "str") return "string";
        if (lit.Kind == "char") return "char";
        if (lit.Kind == "id" && (lit.Text == "true" || lit.Text == "false")) return "bool";
        return "";
    }

    string FreshExpr()
    {
        var saved = scopes;
        scopes = new List<Dictionary<string, string>>();
        PushScope();
        string e = ParseExpr();
        scopes = saved;
        return e;
    }

    string ParseConst()
    {
        int line = t[p].Line;
        string name = Next().Text;
        string type = "";
        if (IsOp(":")) { Next(); type = ParseType(); }
        Expect("=");
        Token lit = t[p];
        string v = FreshExpr();
        if (type == "") type = LiteralType(lit);
        if (type == "") throw new CompileException($"{file}:{line}: constant {name} needs a type: {name} : Type = value");
        EndStmt();
        return $"public static readonly {type} {name} = {v};\n";
    }

    string ParseEnum()
    {
        string name = Next().Text;
        string under = "";
        if (IsOp(":")) { Next(); under = " : " + ParseType(); }
        string attr = TakeAttrs();
        var items = new List<string>();
        while (true)
        {
            SkipNl();
            if (IsKw("end")) break;
            string n = Next().Text;
            if (IsOp("=")) { Next(); n += " = " + FreshExpr(); }
            items.Add(n);
            if (IsOp(",")) Next();
        }
        ExpectKw("end");
        return $"{attr}public enum {name}{under}\n{{\n{string.Join(",\n", items)}\n}}\n";
    }

    string ParseLib()
    {
        string name = Next().Text;
        string libName = "c";
        foreach (var a in attrs)
            if (a.StartsWith("[Link(")) libName = a.Substring(7, a.Length - 10);
        attrs.Clear();
        var sb = new StringBuilder();
        while (true)
        {
            SkipNl();
            if (IsKw("end")) break;
            ExpectKw("fun");
            string fname = Next().Text;
            string entry = fname;
            if (IsOp("=")) { Next(); entry = Next().Text; }
            var ps = new List<string>();
            int count = 0;
            if (IsOp("("))
            {
                Next();
                while (!IsOp(")"))
                {
                    string pn = Next().Text;
                    Expect(":");
                    ps.Add(ParseType() + " " + Mangle(pn));
                    count++;
                    if (IsOp(",")) Next();
                }
                Next();
            }
            string ret = "void";
            if (IsOp(":")) { Next(); ret = ParseType(); }
            if (count == 0) g.ZeroArg.Add(fname);
            sb.Append($"[System.Runtime.InteropServices.DllImport(\"{libName}\", EntryPoint = \"{entry}\")]\npublic static extern {ret} {Mangle(fname)}({string.Join(", ", ps)});\n");
            EndStmt();
        }
        ExpectKw("end");
        return $"public static unsafe partial class {name}\n{{\n{sb}}}\n";
    }

    // ---------------------------------------------------------------- methods
    string ParseDef()
    {
        int line = t[p].Line;
        string vis = "public";
        bool isAbstract = false;
        while (IsKw("private") || IsKw("protected") || IsKw("abstract"))
        {
            string m = Next().Text;
            if (m == "abstract") isAbstract = true; else vis = m;
        }
        ExpectKw("def");
        bool st = cur.Kind == "module" || cur.Kind == "program";
        if (IsKw("self") && Peek(1).Kind == "op" && Peek(1).Text == ".")
        {
            Next();
            Next();
            st = true;
        }
        string raw;
        if (IsOp("[") && Peek(1).Text == "]") { Next(); Next(); raw = "[]"; }
        else raw = Next().Text;
        var gens = new List<string>();
        if (IsOp("<") && !t[p].Space)
        {
            Next();
            gens.Add(Next().Text);
            while (IsOp(",")) { Next(); gens.Add(Next().Text); }
            Expect(">");
        }
        var savedScopes = scopes;
        scopes = new List<Dictionary<string, string>>();
        PushScope();
        var ps = new List<string>();
        var names = new List<string>();
        var autos = new StringBuilder();
        int required = 0;
        if (IsOp("("))
        {
            Next();
            SkipNl();
            while (!IsOp(")"))
            {
                bool splat = false;
                if (IsOp("*")) { Next(); splat = true; }
                bool auto = t[p].Kind == "ivar";
                string pn = Next().Text;
                string type;
                if (IsOp(":")) { Next(); type = ParseType(); }
                else
                {
                    type = "T" + char.ToUpperInvariant(pn[0]) + pn.Substring(1);
                    gens.Add(type);
                }
                string def = "";
                if (IsOp("=")) { Next(); def = " = " + ParseExpr(); }
                else required++;
                string cs = Declare(pn);
                names.Add(cs);
                ps.Add(splat ? $"params {type}[] {cs}" : $"{type} {cs}{def}");
                if (auto)
                {
                    cur.AutoFields.Add(type + " " + Mangle(pn));
                    autos.Append($"this.{Mangle(pn)} = {cs};\n");
                }
                if (IsOp(",")) { Next(); SkipNl(); }
            }
            Next();
        }
        string ret = "void";
        if (IsOp(":")) { Next(); ret = ParseType(); }
        if (IsKw("forall"))
        {
            Next();
            gens.Add(Next().Text);
            while (IsOp(",")) { Next(); gens.Add(Next().Text); }
        }
        string gen = gens.Count > 0 ? "<" + string.Join(", ", gens) + ">" : "";
        string mname = Mangle(raw);
        string plist = string.Join(", ", ps);
        string lineDir = $"#line {line} \"{file}\"\n";
        if (raw != "initialize")
        {
            if (cur.Kind != "interface") g.AddMethod(cur.Name, mname);
            if (required == 0) g.ZeroArg.Add(mname);
        }
        if (isAbstract || cur.Kind == "interface")
        {
            EndStmt();
            scopes = savedScopes;
            if (cur.Kind == "interface") return $"{lineDir}{ret} {mname}{gen}({plist});\n";
            return $"{lineDir}{vis} abstract {ret} {mname}{gen}({plist});\n";
        }
        string baseInit = "";
        SkipNl();
        if (raw == "initialize" && IsKw("super"))
        {
            Next();
            string a = "";
            if (IsOp("(")) { Next(); a = ParseArgs(")"); }
            baseInit = $" : base({a})";
            EndStmt();
        }
        string savedMethod = method;
        string savedArgs = methodArgs;
        var savedCtx = ctx;
        method = raw;
        methodArgs = string.Join(", ", names);
        ctx = new List<string>();
        List<Stmt> body = ParseBlock(" end ");
        ExpectKw("end");
        bool tail = ret != "void" && raw != "initialize";
        string code = Render(body, tail ? 1 : 0, 1);
        method = savedMethod;
        methodArgs = savedArgs;
        ctx = savedCtx;
        scopes = savedScopes;
        string attr = TakeAttrs();
        string stat = st ? " static" : "";
        if (raw == "initialize")
            return $"{attr}{lineDir}{vis} {cur.Name}({plist}){baseInit}\n{{\n{autos}{code}}}\n";
        if (raw == "to_s" && ps.Count == 0)
            return $"{attr}{lineDir}public override string ToString()\n{{\n{code}}}\n";
        if (raw == "[]")
            return $"{attr}{lineDir}public {ret} this[{plist}]\n{{\nget\n{{\n{code}}}\n}}\n";
        string opName = OperatorName(raw);
        if (opName != "")
        {
            cur.Ops.Add(raw);
            string self = cur.Name + cur.Generic;
            return $"{attr}{lineDir}public {ret} {opName}({plist})\n{{\n{code}}}\npublic static {ret} operator {raw}({self} self, {plist}) => self.{opName}({methodArgsOf(names)});\n";
        }
        string virt = "";
        if (!st && cur.Kind == "class" && vis != "private") virt = $"\u0001V:{cur.Name}:{mname}\u0001";
        return $"{attr}{lineDir}{vis}{stat} {virt}{ret} {mname}{gen}({plist})\n{{\n{code}}}\n";
    }

    static string methodArgsOf(List<string> names) => string.Join(", ", names);

    static string OperatorName(string op)
    {
        switch (op)
        {
            case "+": return "op_Add";
            case "-": return "op_Sub";
            case "*": return "op_Mul";
            case "/": return "op_Div";
            case "%": return "op_Mod";
            case "==": return "op_Eq";
            case "!=": return "op_Ne";
            case "<": return "op_Lt";
            case ">": return "op_Gt";
            case "<=": return "op_Le";
            case ">=": return "op_Ge";
        }
        return "";
    }

    // ---------------------------------------------------------------- statements
    bool IsStop(string stops)
    {
        if (t[p].Kind == "eof") return true;
        if (t[p].Kind == "id" && stops.Contains(" " + t[p].Text + " ")) return true;
        return stops.Contains(" } ") && IsOp("}");
    }

    List<Stmt> ParseBlock(string stops)
    {
        var list = new List<Stmt>();
        while (true)
        {
            SkipNl();
            if (t[p].Kind == "eof")
            {
                string want = stops.Trim().Split(' ')[stops.Trim().Split(' ').Length - 1];
                throw Err($"expected '{want}'");
            }
            if (IsStop(stops)) return list;
            list.Add(ParseStatement());
        }
    }

    Stmt ParseStatement()
    {
        int line = t[p].Line;
        Stmt s;
        if (IsKw("if") || IsKw("unless")) s = ParseIf();
        else if (IsKw("while") || IsKw("until")) s = ParseWhile();
        else if (IsKw("for")) s = ParseFor();
        else if (IsKw("case")) s = ParseCase();
        else if (IsKw("begin")) s = ParseBegin();
        else if (IsKw("loop") && (Peek(1).Text == "do" || Peek(1).Text == "{")) s = ParseLoop();
        else s = ParseSimple();
        s.Line = line;
        while (IsKw("if") || IsKw("unless"))
        {
            bool neg = Next().Text == "unless";
            string c = ParseExpr();
            if (neg) c = $"!({c})";
            var w = new Stmt { Line = line, Conds = new List<string>(), Bodies = new List<List<Stmt>>() };
            w.Conds.Add(c);
            var b = new List<Stmt>();
            b.Add(s);
            w.Bodies.Add(b);
            s = w;
        }
        EndStmt();
        return s;
    }

    static Stmt Simple(string code)
    {
        return new Stmt { Code = code };
    }

    Stmt ParseSimple()
    {
        if (IsKw("return"))
        {
            Next();
            if (AtStmtEnd()) return Simple("return;");
            return Simple("return " + ParseExprList() + ";");
        }
        if (IsKw("break")) { Next(); return Simple("break;"); }
        if (IsKw("next"))
        {
            Next();
            bool inBlock = ctx.Count > 0 && ctx[ctx.Count - 1] == "block";
            if (!inBlock) return Simple("continue;");
            if (AtStmtEnd()) return Simple("return;");
            return Simple("return " + ParseExpr() + ";");
        }
        if (IsKw("raise"))
        {
            Next();
            string e = ParseExpr();
            if (e.StartsWith("new ")) return Simple("throw " + e + ";");
            return Simple("throw Prelude.raise(" + e + ");");
        }
        if (IsKw("spawn"))
        {
            Next();
            string body = ParseLambdaBody("", new List<string>());
            return Simple($"Prelude.spawn({body});");
        }
        if (t[p].Kind == "id" && !IsKeyword(t[p].Text) && Peek(1).Kind == "op" && Peek(1).Text == ":" && Peek(1).Space)
        {
            string name = Next().Text;
            Next();
            string type = ParseType();
            string v = "default!";
            if (IsOp("=")) { Next(); v = ParseExpr(); }
            string cs = Declare(name);
            return new Stmt { Code = $"{type} {cs} = {v};", Kind = "assign" };
        }
        if (t[p].Kind == "id" && !IsKeyword(t[p].Text) && Peek(1).Kind == "op" && Peek(1).Text == "=")
        {
            string name = Next().Text;
            Next();
            SkipNl();
            string v = ParseExpr();
            if (IsLocal(name)) return new Stmt { Code = $"{Local(name)} = {v};", Kind = "assign" };
            if (v == "null") throw Err($"cannot infer type of '{name}' from nil; declare it: {name} : Type? = nil");
            string cs = Declare(name);
            return new Stmt { Code = $"var {cs} = {v};", Kind = "assign" };
        }
        if (t[p].Kind == "id" && !IsKeyword(t[p].Text) && Peek(1).Kind == "op" && Peek(1).Text == "," && IsMultiAssign())
            return ParseMultiAssign();
        string ex = ParseExpr();
        string k = kind;
        if (IsOp("="))
        {
            Next();
            SkipNl();
            return new Stmt { Code = $"{ex} = {ParseExpr()};", Kind = "assign" };
        }
        if (t[p].Kind == "op" && " += -= *= /= %= |= &= ^= <<= >>= ".Contains(" " + t[p].Text + " "))
        {
            string op = Next().Text;
            return new Stmt { Code = $"{ex} {op} {ParseExpr()};", Kind = "assign" };
        }
        if (IsOp("**=")) { Next(); return new Stmt { Code = $"{ex} = Prelude.pow({ex}, {ParseExpr()});", Kind = "assign" }; }
        if (IsOp("||=")) { Next(); return new Stmt { Code = $"{ex} ??= {ParseExpr()};", Kind = "assign" }; }
        if (IsOp("&&=")) { Next(); return new Stmt { Code = $"{ex} = {ex} && {ParseExpr()};", Kind = "assign" }; }
        return new Stmt { Code = ex + ";", Expr = ex, HasExpr = true, Kind = k };
    }

    bool IsMultiAssign()
    {
        int k = p;
        while (t[k].Kind == "id" || (t[k].Kind == "op" && t[k].Text == ",")) k++;
        return t[k].Kind == "op" && t[k].Text == "=";
    }

    Stmt ParseMultiAssign()
    {
        var names = new List<string>();
        names.Add(Next().Text);
        while (IsOp(",")) { Next(); names.Add(Next().Text); }
        Expect("=");
        string rhs = ParseExprList();
        if (!rhs.StartsWith("(")) rhs = "(" + rhs + ")";
        bool allDeclared = true;
        foreach (var n in names) if (!IsLocal(n)) allDeclared = false;
        var lhs = new List<string>();
        foreach (var n in names) lhs.Add(allDeclared ? Local(n) : (IsLocal(n) ? throw Err("mixing new and existing variables in multiple assignment") : "var " + Declare(n)));
        return new Stmt { Code = $"({string.Join(", ", lhs)}) = {rhs};", Kind = "assign" };
    }

    string ParseExprList()
    {
        string e = ParseExpr();
        if (!IsOp(",")) return e;
        var parts = new List<string>();
        parts.Add(e);
        while (IsOp(",")) { Next(); parts.Add(ParseExpr()); }
        return "(" + string.Join(", ", parts) + ")";
    }

    Stmt ParseIf()
    {
        bool neg = Next().Text == "unless";
        var s = new Stmt { Conds = new List<string>(), Bodies = new List<List<Stmt>>() };
        string c = ParseExpr();
        if (neg) c = $"!({c})";
        if (IsKw("then")) Next();
        PushScope();
        s.Conds.Add(c);
        s.Bodies.Add(ParseBlock(" elsif else end "));
        PopScope();
        while (IsKw("elsif"))
        {
            Next();
            string c2 = ParseExpr();
            if (IsKw("then")) Next();
            PushScope();
            s.Conds.Add(c2);
            s.Bodies.Add(ParseBlock(" elsif else end "));
            PopScope();
        }
        if (IsKw("else"))
        {
            Next();
            PushScope();
            s.Else = ParseBlock(" end ");
            PopScope();
        }
        ExpectKw("end");
        return s;
    }

    string LoopBody(string stops)
    {
        ctx.Add("loop");
        List<Stmt> body = ParseBlock(stops);
        ctx.RemoveAt(ctx.Count - 1);
        return Render(body, 0, 1);
    }

    Stmt ParseWhile()
    {
        bool neg = Next().Text == "until";
        string c = ParseExpr();
        if (neg) c = $"!({c})";
        if (IsKw("do")) Next();
        PushScope();
        string body = LoopBody(" end ");
        PopScope();
        ExpectKw("end");
        return Simple($"while ({c})\n{{\n{body}}}");
    }

    Stmt ParseLoop()
    {
        Next();
        bool brace = IsOp("{");
        Next();
        PushScope();
        string body = LoopBody(brace ? " } " : " end ");
        PopScope();
        if (brace) Expect("}"); else ExpectKw("end");
        return Simple($"while (true)\n{{\n{body}}}");
    }

    Stmt ParseFor()
    {
        Next();
        var names = new List<string>();
        names.Add(Next().Text);
        while (IsOp(",")) { Next(); names.Add(Next().Text); }
        ExpectKw("in");
        string start = ParseBinary(0);
        PushScope();
        string head;
        if (IsOp("..") || IsOp("..."))
        {
            bool excl = Next().Text == "...";
            string end = ParseBinary(0);
            string v = Declare(names[0]);
            head = $"for (var {v} = {start}; {v} {(excl ? "<" : "<=")} {end}; {v}++)";
        }
        else if (names.Count == 1) head = $"foreach (var {Declare(names[0])} in {start})";
        else
        {
            var vs = new List<string>();
            foreach (var n in names) vs.Add(Declare(n));
            head = $"foreach (var ({string.Join(", ", vs)}) in {start})";
        }
        if (IsKw("do")) Next();
        string body = LoopBody(" end ");
        PopScope();
        ExpectKw("end");
        return Simple($"{head}\n{{\n{body}}}");
    }

    Stmt ParseCase()
    {
        Next();
        var s = new Stmt { Conds = new List<string>(), Bodies = new List<List<Stmt>>() };
        string subject = "";
        if (t[p].Kind != "nl")
        {
            string e = ParseExpr();
            tmp++;
            subject = "__case" + tmp;
            s.Pre = $"var {subject} = {e};\n";
        }
        SkipNl();
        while (IsKw("when"))
        {
            Next();
            var parts = new List<string>();
            while (true)
            {
                parts.Add(WhenCond(subject));
                if (!IsOp(",")) break;
                Next();
                SkipNl();
            }
            if (IsKw("then")) Next();
            PushScope();
            s.Conds.Add(string.Join(" || ", parts));
            s.Bodies.Add(ParseBlock(" when else end "));
            PopScope();
        }
        if (IsKw("else"))
        {
            Next();
            PushScope();
            s.Else = ParseBlock(" end ");
            PopScope();
        }
        ExpectKw("end");
        return s;
    }

    string WhenCond(string subject)
    {
        if (subject == "") return ParseExpr();
        if (t[p].Kind == "const" && (Peek(1).Kind == "nl" || Peek(1).Text == "," || Peek(1).Text == "then") && !Peek(1).Text.StartsWith(":"))
        {
            string ty = Next().Text;
            if (ty.Length > 0 && IsTypeName(ty)) return $"{subject} is {MapType(ty)}";
            return $"{subject} == {ty}";
        }
        string a = ParseBinary(0);
        if (IsOp("..") || IsOp("..."))
        {
            bool excl = Next().Text == "...";
            string b = ParseBinary(0);
            return $"({subject} >= {a} && {subject} {(excl ? "<" : "<=")} {b})";
        }
        return $"{subject} == {a}";
    }

    static bool IsTypeName(string n) => MapType(n) != n || n == "Exception" || n.EndsWith("Exception") || n.EndsWith("Error");

    Stmt ParseBegin()
    {
        Next();
        PushScope();
        string body = Render(ParseBlock(" rescue ensure end "), 0, 1);
        PopScope();
        var sb = new StringBuilder($"try\n{{\n{body}}}");
        while (IsKw("rescue"))
        {
            Next();
            string name = "";
            string type = "Exception";
            PushScope();
            if (t[p].Kind == "id")
            {
                name = Declare(Next().Text);
                if (IsOp(":")) { Next(); type = ParseType(); }
            }
            else if (t[p].Kind == "const") type = ParseType();
            string rb = Render(ParseBlock(" rescue ensure end "), 0, 1);
            PopScope();
            sb.Append(name == "" ? $"\ncatch ({type})\n{{\n{rb}}}" : $"\ncatch ({type} {name})\n{{\n{rb}}}");
        }
        if (IsKw("ensure"))
        {
            Next();
            PushScope();
            string eb = Render(ParseBlock(" end "), 0, 1);
            PopScope();
            sb.Append($"\nfinally\n{{\n{eb}}}");
        }
        ExpectKw("end");
        return Simple(sb.ToString());
    }

    // ---------------------------------------------------------------- rendering
    static string Indent(string code, int ind)
    {
        if (ind == 0) return code;
        string pad = new string(' ', ind * 4);
        var sb = new StringBuilder();
        foreach (var l in code.Split('\n'))
        {
            if (l.Length > 0) sb.Append(l.StartsWith("#line") ? "" : pad);
            sb.Append(l).Append('\n');
        }
        return sb.ToString(0, sb.Length - 1);
    }

    string Render(List<Stmt> body, int tail, int ind)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < body.Count; i++)
            sb.Append(RenderStmt(body[i], i == body.Count - 1 ? tail : 0, ind));
        return sb.ToString();
    }

    string RenderStmt(Stmt s, int tail, int ind)
    {
        string line = $"#line {s.Line} \"{file}\"\n";
        if (s.Conds != null && s.Bodies != null)
        {
            var sb = new StringBuilder();
            sb.Append(line);
            sb.Append(Indent(s.Pre, ind));
            for (int k = 0; k < s.Conds.Count; k++)
            {
                sb.Append(Indent((k == 0 ? "if (" : "else if (") + s.Conds[k] + ")\n{", ind)).Append('\n');
                sb.Append(Render(s.Bodies[k], tail, ind + 1));
                sb.Append(Indent("}", ind)).Append('\n');
            }
            if (s.Else != null)
            {
                sb.Append(Indent("else\n{", ind)).Append('\n');
                sb.Append(Render(s.Else, tail, ind + 1));
                sb.Append(Indent("}", ind)).Append('\n');
            }
            return sb.ToString();
        }
        string code = s.Code;
        if (s.HasExpr && (tail == 1 || (tail == 2 && s.Kind == "value"))) code = "return " + s.Expr + ";";
        return line + Indent(code, ind) + "\n";
    }

    // ---------------------------------------------------------------- expressions
    public string ParseExpr()
    {
        string l = ParseNotOperand();
        while (IsKw("and") || IsKw("or"))
        {
            string op = Next().Text == "and" ? "&&" : "||";
            SkipNl();
            string r = ParseNotOperand();
            l = $"({l} {op} {r})";
            kind = "value";
        }
        return l;
    }

    string ParseNotOperand()
    {
        if (IsKw("not"))
        {
            Next();
            string e = ParseNotOperand();
            kind = "value";
            return $"!({e})";
        }
        return ParseTernary();
    }

    string ParseTernary()
    {
        string c = ParseRange();
        if (IsOp("?"))
        {
            Next();
            SkipNl();
            string a = ParseTernary();
            SkipNl();
            Expect(":");
            SkipNl();
            string b = ParseTernary();
            kind = "value";
            return $"({c} ? {a} : {b})";
        }
        return c;
    }

    string ParseRange()
    {
        string a = ParseBinary(0);
        if (IsOp("..") || IsOp("..."))
        {
            bool excl = Next().Text == "...";
            string b = ParseBinary(0);
            kind = "value";
            return $"Prelude.range({a}, {b}, {(excl ? "true" : "false")})";
        }
        return a;
    }

    string ParseBinary(int level)
    {
        string[] levels = Levels.Split(';');
        if (level == levels.Length) return ParseUnary();
        string ops = " " + levels[level];
        string l = ParseBinary(level + 1);
        while (t[p].Kind == "op" && ops.Contains(" " + t[p].Text + " "))
        {
            string op = Next().Text;
            SkipNl();
            string r = ParseBinary(level + 1);
            if (op == "<=>") l = $"{l}.CompareTo({r})";
            else if (op == "|" || op == "^" || op == "&") l = $"({l} {op} {r})";
            else if (op == "===") l = $"{l} == {r}";
            else l = $"{l} {op} {r}";
            kind = "value";
        }
        return l;
    }

    string ParseUnary()
    {
        if (IsOp("-") || IsOp("!") || IsOp("~") || IsOp("+"))
        {
            string op = Next().Text;
            string e = ParseUnary();
            kind = "value";
            if (op == "+") return e;
            if (e.StartsWith("-") || e.StartsWith("!")) return $"{op}({e})";
            return op + e;
        }
        if (IsOp("&") && Peek(1).Kind == "id")
        {
            Next();
            return "&" + ParseUnary();
        }
        return ParsePower();
    }

    string ParsePower()
    {
        string b = ParsePostfix();
        if (IsOp("**"))
        {
            Next();
            string e = ParseUnary();
            kind = "value";
            return $"Prelude.pow({b}, {e})";
        }
        return b;
    }

    bool BlockFollows() => IsOp("{") || (IsKw("do") && !noDo);

    string WithBlock(string args)
    {
        if (!BlockFollows()) return args;
        string lam = ParseBlockLambda();
        return args == "" ? lam : args + ", " + lam;
    }

    string ParsePostfix()
    {
        string e = ParsePrimary();
        bool isType = lastIsType;
        while (true)
        {
            if (IsOp(".") || IsOp("&."))
            {
                bool safe = Next().Text == "&.";
                string dot = safe ? "?." : ".";
                if (IsOp("("))
                {
                    Next();
                    e = $"{e}({ParseArgs(")")})";
                    kind = "call";
                    isType = false;
                    continue;
                }
                Token nt = Next();
                if (nt.Kind != "id" && nt.Kind != "const") throw Err("expected member name");
                string name = nt.Text;
                if (isType && name == "new")
                {
                    string a = OptArgs();
                    if (e.EndsWith("[]")) e = $"new {e.Substring(0, e.Length - 2)}[{a}]";
                    else e = $"new {e}({a})";
                    kind = "call";
                    isType = false;
                    continue;
                }
                if (name == "call") { e = $"{e}({OptArgs()})"; kind = "call"; isType = false; continue; }
                if (name == "nil?") { e = $"({e} is null)"; kind = "value"; isType = false; continue; }
                if (name == "not_nil!") { e = $"{e}!"; kind = "value"; continue; }
                if (name == "is_a?") { Expect("("); e = $"({e} is {ParseType()})"; Expect(")"); kind = "value"; isType = false; continue; }
                if (name == "as") { Expect("("); e = $"(({ParseType()}){e})"; Expect(")"); kind = "value"; isType = false; continue; }
                if (name == "to_s" && !(IsOp("(") && !t[p].Space)) { e = $"Prelude.str({e})"; kind = "value"; isType = false; continue; }
                if (name == "class") { e = $"{e}.GetType()"; kind = "value"; isType = false; continue; }
                string mn = nt.Kind == "const" ? name : Mangle(name);
                if (IsOp("(") && !t[p].Space)
                {
                    Next();
                    string a = ParseArgs(")");
                    e = $"{e}{dot}{mn}({WithBlock(a)})";
                    kind = "call";
                    isType = false;
                }
                else if (CanStartArg())
                {
                    e = $"{e}{dot}{mn}({WithBlock(CommandArgs())})";
                    kind = "call";
                    isType = false;
                }
                else if (BlockFollows())
                {
                    e = $"{e}{dot}{mn}({ParseBlockLambda()})";
                    kind = "call";
                    isType = false;
                }
                else if (nt.Kind == "const")
                {
                    e = $"{e}{dot}{mn}";
                    kind = "value";
                }
                else
                {
                    e = $"{e}{dot}\u0001M:{mn}\u0001";
                    kind = "member";
                    isType = false;
                }
            }
            else if (IsOp("::"))
            {
                Next();
                e = e + "." + Next().Text;
                if (IsOp("(") && !t[p].Space && isType) e = GenericSuffix(e);
            }
            else if (IsOp("[") && !t[p].Space)
            {
                Next();
                e = $"{e}[{ParseArgs("]")}]";
                kind = "value";
                isType = false;
            }
            else break;
        }
        lastIsType = isType;
        return e;
    }

    string GenericSuffix(string name)
    {
        Next();
        var args = new List<string>();
        args.Add(ParseType());
        while (IsOp(",")) { Next(); args.Add(ParseType()); }
        Expect(")");
        return GenericType(name, args);
    }

    string OptArgs()
    {
        if (IsOp("(") && !t[p].Space)
        {
            Next();
            return WithBlock(ParseArgs(")"));
        }
        if (CanStartArg()) return WithBlock(CommandArgs());
        return WithBlock("");
    }

    bool CanStartArg()
    {
        Token c = t[p];
        if (!c.Space) return false;
        switch (c.Kind)
        {
            case "int":
            case "float":
            case "str":
            case "char":
            case "ivar":
            case "const":
                return true;
            case "id":
                return c.Text == "not" || c.Text == "nil" || c.Text == "true" || c.Text == "false" || c.Text == "self" || !IsKeyword(c.Text);
            case "op":
                if (c.Text == "->" || c.Text == "(" || c.Text == "[" || c.Text == "!") return true;
                if (c.Text == "-" || c.Text == "*" || c.Text == "&") return !Peek(1).Space;
                return false;
        }
        return false;
    }

    string CommandArgs()
    {
        bool saved = noDo;
        noDo = true;
        var parts = new List<string>();
        parts.Add(Arg());
        while (IsOp(","))
        {
            Next();
            SkipNl();
            parts.Add(Arg());
        }
        noDo = saved;
        return string.Join(", ", parts);
    }

    string ParseArgs(string close)
    {
        bool saved = noDo;
        noDo = false;
        var parts = new List<string>();
        SkipNl();
        while (!IsOp(close))
        {
            parts.Add(Arg());
            SkipNl();
            if (IsOp(",")) { Next(); SkipNl(); }
            else if (!IsOp(close)) throw Err($"expected ',' or '{close}'");
        }
        Next();
        noDo = saved;
        return string.Join(", ", parts);
    }

    string Arg()
    {
        if ((IsKw("out") || IsKw("ref")) && Peek(1).Kind == "id")
        {
            string mod = Next().Text;
            string n = Next().Text;
            if (IsLocal(n)) return mod + " " + Local(n);
            if (mod == "ref") throw Err($"'ref {n}' needs an existing variable");
            return "out var " + Declare(n);
        }
        if (t[p].Kind == "id" && Peek(1).Kind == "op" && Peek(1).Text == ":" && !Peek(1).Space)
        {
            string n = Next().Text;
            Next();
            return Mangle(n) + ": " + ParseExpr();
        }
        if (IsOp("*") && !Peek(1).Space) { Next(); return ParseExpr(); }
        return ParseExpr();
    }

    string ParseBlockLambda()
    {
        var ps = new List<string>();
        var names = new List<string>();
        bool brace = IsOp("{");
        Next();
        if (IsOp("||")) Next();
        else if (IsOp("|"))
        {
            Next();
            while (!IsOp("|"))
            {
                string n = Next().Text;
                string ty = "";
                if (IsOp(":")) { Next(); ty = ParseType() + " "; }
                names.Add(n);
                ps.Add(ty + n);
                if (IsOp(",")) Next();
            }
            Next();
        }
        return LambdaRest(ps, names, brace);
    }

    string ParseLambdaBody(string unused, List<string> names)
    {
        bool brace = IsOp("{");
        if (!brace && !IsKw("do")) throw Err("expected '{' or 'do'");
        Next();
        return LambdaRest(new List<string>(), names, brace);
    }

    string LambdaRest(List<string> ps, List<string> names, bool brace)
    {
        PushScope();
        var outs = new List<string>();
        for (int i = 0; i < names.Count; i++)
        {
            string cs = Declare(names[i]);
            string pdecl = ps[i];
            outs.Add(pdecl.Substring(0, pdecl.Length - names[i].Length) + cs);
        }
        ctx.Add("block");
        bool saved = noDo;
        noDo = false;
        List<Stmt> body = ParseBlock(brace ? " } " : " end ");
        noDo = saved;
        ctx.RemoveAt(ctx.Count - 1);
        if (brace) Expect("}"); else ExpectKw("end");
        PopScope();
        string plist = "(" + string.Join(", ", outs) + ")";
        kind = "value";
        if (body.Count == 1 && body[0].HasExpr && body[0].Conds == null) return $"{plist} => {body[0].Expr}";
        if (body.Count == 0) return $"{plist} => {{ }}";
        return $"{plist} =>\n{{\n{Render(body, 2, 1)}}}";
    }

    string ParsePrimary()
    {
        lastIsType = false;
        Token c = t[p];
        kind = "value";
        switch (c.Kind)
        {
            case "int":
            case "float":
            case "char":
                Next();
                return c.Text;
            case "str":
                Next();
                return Interp(c);
            case "ivar":
                Next();
                return "this." + Mangle(c.Text);
            case "const":
                return ParseConstPrimary();
            case "id":
                return ParseIdPrimary();
            case "op":
                if (c.Text == "(")
                {
                    Next();
                    SkipNl();
                    string e = ParseExprList();
                    SkipNl();
                    Expect(")");
                    kind = "value";
                    return e.StartsWith("(") && e.EndsWith(")") && IsBalanced(e) ? e : "(" + e + ")";
                }
                if (c.Text == "[") return ParseArrayLit();
                if (c.Text == "{") return ParseHashLit();
                if (c.Text == "->") return ParseLambdaLit();
                if (c.Text == "::") { Next(); return "global::" + ParseConstPrimary(); }
                break;
        }
        throw Err("unexpected token");
    }

    static bool IsBalanced(string e)
    {
        int d = 0;
        for (int i = 0; i < e.Length; i++)
        {
            if (e[i] == '(') d++;
            if (e[i] == ')') d--;
            if (d == 0 && i < e.Length - 1) return false;
        }
        return true;
    }

    string ParseConstPrimary()
    {
        string name = Next().Text;
        string r = MapType(name.EndsWith("?") ? name.Substring(0, name.Length - 1) : name);
        lastIsType = true;
        if (IsOp("(") && !t[p].Space) r = GenericSuffix(name);
        lastIsType = true;
        return r;
    }

    string ParseIdPrimary()
    {
        Token c = Next();
        string n = c.Text;
        switch (n)
        {
            case "nil": return "null";
            case "true": return "true";
            case "false": return "false";
            case "self": return "this";
            case "super":
            {
                string a = methodArgs;
                if (IsOp("(") && !t[p].Space) { Next(); a = ParseArgs(")"); }
                kind = "call";
                return $"base.{Mangle(method)}({a})";
            }
            case "typeof":
            case "sizeof":
            {
                Expect("(");
                string ty = ParseType();
                Expect(")");
                return $"{n}({ty})";
            }
            case "uninitialized":
                return $"default({ParseType()})";
            case "spawn":
            {
                kind = "call";
                return $"Prelude.spawn({ParseLambdaBody("", new List<string>())})";
            }
        }
        if (IsKeyword(n)) { p--; throw Err("unexpected keyword"); }
        if (IsLocal(n))
        {
            string l = Local(n);
            if (IsOp("(") && !t[p].Space)
            {
                Next();
                kind = "call";
                return $"{l}({ParseArgs(")")})";
            }
            return l;
        }
        string m = Mangle(n);
        if (IsOp("<") && !t[p].Space && Peek(1).Kind == "const" && GenericCallAhead())
        {
            Next();
            var ts = new List<string>();
            ts.Add(ParseType());
            while (IsOp(",")) { Next(); ts.Add(ParseType()); }
            Expect(">");
            m += "<" + string.Join(", ", ts) + ">";
        }
        if (IsOp("(") && !t[p].Space)
        {
            Next();
            string a = ParseArgs(")");
            kind = "call";
            return $"{m}({WithBlock(a)})";
        }
        if (CanStartArg())
        {
            kind = "call";
            return $"{m}({WithBlock(CommandArgs())})";
        }
        if (BlockFollows())
        {
            kind = "call";
            return $"{m}({ParseBlockLambda()})";
        }
        kind = "member";
        return $"\u0001M:{m}\u0001";
    }

    bool GenericCallAhead()
    {
        int k = p + 1;
        int d = 1;
        while (k < t.Count && d > 0)
        {
            Token x = t[k];
            if (x.Kind == "op" && x.Text == "<") d++;
            else if (x.Kind == "op" && x.Text == ">") d--;
            else if (!(x.Kind == "const" || (x.Kind == "op" && (x.Text == "," || x.Text == "(" || x.Text == ")" || x.Text == "::")))) return false;
            k++;
        }
        return d == 0 && k < t.Count && t[k].Kind == "op" && t[k].Text == "(";
    }

    string ParseArrayLit()
    {
        Next();
        var items = new List<string>();
        SkipNl();
        while (!IsOp("]"))
        {
            items.Add(ParseExpr());
            SkipNl();
            if (IsOp(",")) { Next(); SkipNl(); }
        }
        Next();
        kind = "value";
        if (IsKw("of"))
        {
            Next();
            string ty = ParseType();
            if (items.Count == 0) return $"new List<{ty}>()";
            return $"new List<{ty}> {{ {string.Join(", ", items)} }}";
        }
        if (items.Count == 0) throw Err("empty array literal needs a type: [] of Type");
        return $"new[] {{ {string.Join(", ", items)} }}";
    }

    string ParseHashLit()
    {
        Next();
        SkipNl();
        kind = "value";
        if (IsOp("}"))
        {
            Next();
            ExpectKw("of");
            string k = ParseType();
            Expect("=>");
            string v = ParseType();
            return $"new Dictionary<{k}, {v}>()";
        }
        string first = ParseExpr();
        SkipNl();
        if (IsOp("=>"))
        {
            var pairs = new List<string>();
            Next();
            SkipNl();
            pairs.Add($"({first}, {ParseExpr()})");
            SkipNl();
            while (IsOp(","))
            {
                Next();
                SkipNl();
                if (IsOp("}")) break;
                string k = ParseExpr();
                Expect("=>");
                SkipNl();
                pairs.Add($"({k}, {ParseExpr()})");
                SkipNl();
            }
            Expect("}");
            return $"Prelude.hash({string.Join(", ", pairs)})";
        }
        var items = new List<string>();
        items.Add(first);
        while (IsOp(",")) { Next(); SkipNl(); items.Add(ParseExpr()); SkipNl(); }
        Expect("}");
        return "(" + string.Join(", ", items) + ")";
    }

    string ParseLambdaLit()
    {
        Next();
        var ps = new List<string>();
        var names = new List<string>();
        if (IsOp("("))
        {
            Next();
            while (!IsOp(")"))
            {
                string n = Next().Text;
                string ty = "";
                if (IsOp(":")) { Next(); ty = ParseType() + " "; }
                names.Add(n);
                ps.Add(ty + n);
                if (IsOp(",")) Next();
            }
            Next();
        }
        bool brace = IsOp("{");
        if (!brace && !IsKw("do")) throw Err("expected '{' or 'do' after lambda parameters");
        Next();
        return LambdaRest(ps, names, brace);
    }

    // ---------------------------------------------------------------- strings
    string Interp(Token tok)
    {
        string raw = tok.Text;
        bool interp = false;
        for (int i = 0; i + 1 < raw.Length; i++)
        {
            if (raw[i] == '\\') { i++; continue; }
            if (raw[i] == '#' && raw[i + 1] == '{') interp = true;
        }
        var sb = new StringBuilder();
        int k = 0;
        while (k < raw.Length)
        {
            char ch = raw[k];
            if (ch == '\\' && k + 1 < raw.Length)
            {
                char nx = raw[k + 1];
                if (nx == '#') sb.Append('#');
                else if (nx == 'e') sb.Append("\\u001b");
                else if (nx == '{' || nx == '}') sb.Append(interp ? new string(nx, 2) : nx.ToString());
                else sb.Append('\\').Append(nx);
                k += 2;
                continue;
            }
            if (ch == '#' && k + 1 < raw.Length && raw[k + 1] == '{')
            {
                int d = 1;
                int j = k + 2;
                bool inStr = false;
                while (j < raw.Length)
                {
                    char x = raw[j];
                    if (inStr)
                    {
                        if (x == '\\') j++;
                        else if (x == '"') inStr = false;
                    }
                    else if (x == '"') inStr = true;
                    else if (x == '{') d++;
                    else if (x == '}')
                    {
                        d--;
                        if (d == 0) break;
                    }
                    j++;
                }
                string code = raw.Substring(k + 2, j - k - 2);
                sb.Append("{Prelude.str(").Append(SubExpr(code, tok.Line)).Append(")}");
                k = j + 1;
                continue;
            }
            if (ch == '\n') sb.Append("\\n");
            else if (ch == '\r') sb.Append("\\r");
            else if (ch == '\t') sb.Append("\\t");
            else if (interp && (ch == '{' || ch == '}')) sb.Append(ch).Append(ch);
            else sb.Append(ch);
            k++;
        }
        kind = "value";
        return (interp ? "$\"" : "\"") + sb.ToString() + "\"";
    }

    string SubExpr(string code, int line)
    {
        var savedT = t;
        int savedP = p;
        string savedKind = kind;
        t = new Lexer(code, file, line).Lex();
        p = 0;
        string e = ParseExpr();
        SkipNl();
        if (t[p].Kind != "eof") throw Err("unexpected token in interpolation");
        t = savedT;
        p = savedP;
        kind = savedKind;
        return e;
    }
}
