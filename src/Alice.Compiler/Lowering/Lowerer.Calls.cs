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
        if (c.Res == "ext" && c.Block != null && TryInline(c) is MExpr inlined) return inlined;
        if (c.Res == "ext" && c.Block == null && TryFuseSink(c) is MExpr fused) return fused;

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
                    return new MCall { Target = recv, Name = m.CsName, Args = args, Safe = c.Safe };
                }
            case "field" when soa != null && c.Recv is NameExpr { Sym: LocalSym ps } && soa.Ps.Contains(ps):
                return new MIndex { Target = new MMember { Target = new MCode(soa.Store), Name = ((FieldSym)c.Sym!).CsName }, Args = { new MCode(soa.Idx) } };
            case "field":
                return new MMember { Target = recv!, Name = c.Sym is FieldSym f ? f.CsName : c.Name, Safe = c.Safe };
            case "clr":
            case "dyncall":
                return new MCall { Target = recv, Name = Binder.Mangle(c.Name), Args = args, Safe = c.Safe };
            case "ext":
            case "free":
                return new MCall { Target = recv, Name = cs, Args = args, Safe = c.Safe };
            case "unknown-member":
                if (zeroArg.Contains(cs) && !props.Contains(cs)) return new MCall { Target = recv, Name = cs, Safe = c.Safe };
                return new MMember { Target = recv!, Name = cs, Safe = c.Safe };
        }
        return new MCall { Target = recv, Name = cs, Args = args, Safe = c.Safe };
    }
}
