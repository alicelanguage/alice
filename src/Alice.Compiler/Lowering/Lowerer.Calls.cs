using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Lowering;

public sealed partial class Lowerer
{
    MExpr Call(CallExpr c)
    {
        if (c.Res == "fused") return new MCode((string)c.Sym!);
        if (c.Res == "asnew") return Expr((NewExpr)c.Sym!);
        if (c.Res == "inline") return Expr((StmtExpr)c.Sym!);
        if (c.Res == "defmethod")
            return new MCall { Target = new MCode("global::Alice.Runtime.Prelude"), Name = "define_method", Args = { new MCode($"typeof({Type.Type.Cs})"), Expr(c.Args[0].Value!), new MCast { TypeCs = "global::System.Delegate", E = Expr(c.Block!) } } };
        if (c.Res == "missing")
        {
            var argv = new MNewArray { ElemCs = "object", Items = Args(c.Args).Select(a => (MExpr)new MCast { TypeCs = "object", E = a }).ToList() };
            return new MCall { Target = Expr(c.Recv!), Name = "method_missing", Args = { new MCode("\"" + c.Name + "\""), argv } };
        }
        if (c.Res == "ext" && c.Block != null && TryInline(c) is MExpr inlined) return inlined;
        if (c.Res == "ext" && c.Block == null && TryFuseSink(c) is MExpr fused) return fused;
        if (c.Res == "ext" && c.Block == null && c.Args.Count == 0 && Binder.Mangle(c.Name) is "size" or "length" && TryFlatSize(c) is MExpr rows) return rows;
        if (c.Res == "ext" && c.Block == null && c.Args.Count == 0 && c.Recv != null && WideningCast(c.Name, c.Recv.Type.Cs) is string to)
            return new MCast { TypeCs = to, E = Expr(c.Recv) };
        // xs.size / s.length -> the Length/Count member itself (no call; the JIT sees the array length)
        if (c.Res == "ext" && c.Block == null && c.Args.Count == 0 && c.Recv != null && Binder.Mangle(c.Name) is "size" or "length" or "count")
        {
            var rt = c.Recv.Type;
            if (rt.Kind == "array" || rt.IsString) return new MMember { Target = Expr(c.Recv), Name = "Length" };
            if (rt.Clr is { IsGenericType: true } lc && lc.GetGenericTypeDefinition() == typeof(List<>)) return new MMember { Target = Expr(c.Recv), Name = "Count" };
        }

        var args = Args(c.Args);
        if (c.Block != null) args.Add(Lambda(c.Block));
        MExpr? recv = c.Recv != null ? Expr(c.Recv) : null;
        string cs = Binder.Mangle(c.Name);

        switch (c.Res)
        {
            case "special":
                switch (c.Name)
                {
                    case "to_s": return new MCall { Target = new MCode("Prelude"), Name = "str", Args = { recv! } };
                    case "class": return new MCall { Target = recv, Name = "GetType" };
                    case "respond_to?": return new MCall { Target = new MCode("Prelude"), Name = "respond_to_q", Args = args.Prepend(recv!).ToList() };
                    case "send": return new MCall { Target = new MCode("Prelude"), Name = "send", Args = args.Prepend(recv!).ToList() };
                }
                break;
            case "alice":
                {
                    var m = (MethodSym)c.Sym!;
                    if (m.IsStatic && recv == null && m.Owner != Type && m.Owner.Kind != "program") recv = new MCode(m.Owner.Type.Cs);
                    return new MCall { Target = recv, Name = CallName(m), Args = args, Safe = c.Safe };
                }
            case "field" when soa != null && c.Recv is NameExpr { Sym: LocalSym ps } && soa.Ps.Contains(ps):
                return new MIndex { Target = new MMember { Target = new MCode(soa.Store), Name = ((FieldSym)c.Sym!).CsName }, Args = { new MCode(soa.Idx) } };
            case "field":
                return new MMember { Target = recv!, Name = c.Sym is FieldSym f ? f.CsName : c.Name, Safe = c.Safe };
            case "clr":
            case "dyncall":
                return new MCall { Target = recv, Name = Binder.Mangle(c.Name) + TypeArgList(c), Args = args, Safe = c.Safe };
            case "ext":
            case "free":
                return new MCall { Target = recv, Name = cs, Args = args, Safe = c.Safe };
            case "unknown-member":
                if (zeroArg.Contains(cs) && !props.Contains(cs)) return new MCall { Target = recv, Name = cs, Safe = c.Safe };
                return new MMember { Target = recv!, Name = cs, Safe = c.Safe };
        }
        return new MCall { Target = recv, Name = cs + TypeArgList(c), Args = args, Safe = c.Safe };
    }

    static string TypeArgList(CallExpr c) => c.TypeArgTypes.Count == 0 ? "" : "<" + string.Join(", ", c.TypeArgTypes.Select(TypeCs)) + ">";

    static readonly HashSet<string> SignedNarrowToLong = ["sbyte", "short", "int", "long", "byte", "ushort", "uint"];
    static readonly HashSet<string> IntoInt = ["sbyte", "short", "int", "byte", "ushort"];

    /// <summary>
    /// A conversion that can never fail is a plain cast: `x.to_i64` on Int32, any number `.to_f`. The checked
    /// generic helper (CreateChecked) is a call the JIT does not inline; narrowing conversions keep it.
    /// </summary>
    static string? WideningCast(string name, string from) => (name, from) switch
    {
        ("to_f" or "to_f64", "sbyte" or "short" or "int" or "long" or "byte" or "ushort" or "uint" or "ulong" or "float" or "double") => "double",
        ("to_f32", "sbyte" or "short" or "int" or "long" or "byte" or "ushort" or "uint" or "ulong" or "float" or "double") => "float",
        ("to_i64", _) when SignedNarrowToLong.Contains(from) => "long",
        ("to_i" or "to_i32", _) when IntoInt.Contains(from) => "int",
        _ => null,
    };
}
