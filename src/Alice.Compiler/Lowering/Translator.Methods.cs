using System.Text;

namespace Alice.Compiler;

public sealed partial class Translator
{
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
}
