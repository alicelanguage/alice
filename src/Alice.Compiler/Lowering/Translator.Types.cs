using System.Text;

namespace Alice.Compiler;

public sealed partial class Translator
{
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
}
