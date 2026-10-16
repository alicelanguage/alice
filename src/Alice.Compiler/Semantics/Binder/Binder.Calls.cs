using Alice.Compiler.Syntax;

namespace Alice.Compiler.Semantics;

public sealed partial class Binder
{
    static readonly HashSet<string> Special = new() { "to_s", "class", "respond_to?", "send" };

    AType BindCall(CallExpr c)
    {
        if (c.Recv == null) return BindSelfCall(c);
        AType recv = BindExpr(c.Recv);
        int argc = c.Args.Count;
        bool hasArgs = c.Parens || argc > 0;
        bool dynSpecial = c.Name is "send" or "respond_to?";
        if (Special.Contains(c.Name) && !(c.Name == "to_s" && c.Parens) && !(dynSpecial && HasMember(recv, c.Name, argc)))
        {
            BindArgs(c.Args);
            c.Res = "special";
            return c.Name switch
            {
                "to_s" => AType.String,
                "class" => ClrTypes.FromClr(typeof(Type)),
                "respond_to?" => AType.Bool,
                _ => AType.Dynamic,
            };
        }

        // static access on a type: Util.twice, Simd.Dot, LibM.cos, Color.Green
        if (c.Recv is ConstExpr ce && ce.Res == "type" && ce.Sym is AType owner)
        {
            BindArgs(c.Args);
            if (owner.Sym != null)
            {
                var m = owner.Sym.FindMethod(c.Name, argc);
                if (m != null)
                {
                    c.Res = "alice";
                    c.Sym = m;
                    BindBlockFor(c, m);
                    return m.Ret;
                }
                var f = owner.Sym.FindField(c.Name);
                if (f != null && !hasArgs)
                {
                    c.Res = "field";
                    c.Sym = f;
                    return f.Type;
                }
            }
            if (owner.Clr != null)
            {
                var mem = ClrTypes.Lookup(owner.Clr, c.Name, argc + (c.Block != null ? 1 : 0), hasArgs || c.Block != null, true);
                if (mem != null)
                {
                    c.Res = mem.Kind == "method" ? "clr" : "field";
                    if (c.Block != null) BindLambda(c.Block, new List<AType>());
                    return mem.Type;
                }
            }
            if (c.Block != null) BindLambda(c.Block, new List<AType>());
            c.Res = hasArgs ? "clr" : "field";
            return AType.Unknown;
        }

        var argTypes = BindArgTypes(c.Args);

        // Alice instance members
        if (recv.Sym != null && recv.Sym.Kind is "class" or "struct" or "interface")
        {
            var m = recv.Sym.FindMethod(c.Name, argc);
            if (m != null)
            {
                c.Res = "alice";
                c.Sym = m;
                BindBlockFor(c, m);
                return m.Ret;
            }
            var f = recv.Sym.FindField(c.Name);
            if (f != null && !hasArgs && c.Block == null)
            {
                c.Res = "field";
                c.Sym = f;
                return f.Type;
            }
        }

        if (recv.Kind == "dynamic")
        {
            if (c.Block != null) BindLambda(c.Block, new List<AType>());
            c.Res = hasArgs ? "dyncall" : "field";
            return AType.Dynamic;
        }

        // .NET members (also inherited by Alice classes from CLR bases)
        Type? clr = recv.Clr ?? recv.Sym?.Bases.Select(b => b.Clr).FirstOrDefault(b => b != null && !b.IsInterface);
        if (clr != null)
        {
            var mem = ClrTypes.Lookup(clr, c.Name, argc + (c.Block != null ? 1 : 0), hasArgs || c.Block != null, false);
            if (mem != null)
            {
                c.Res = mem.Kind == "method" ? "clr" : "field";
                if (c.Block != null) BindLambda(c.Block, new List<AType>());
                return mem.Type;
            }
        }

        // Prelude extension methods (Ruby names)
        string cs = Mangle(c.Name);
        if (Builtins.ExtNames.Contains(cs))
        {
            c.Res = "ext";
            AType? br = null;
            if (c.Block != null)
            {
                BindLambda(c.Block, Builtins.BlockParams(cs, recv, argTypes));
                br = c.Block.ReturnsValue ? c.Block.Result : AType.Void;
            }
            return Builtins.ExtReturn(cs, recv, argTypes, br) ?? AType.Unknown;
        }

        if (c.Block != null) BindLambda(c.Block, new List<AType>());
        c.Res = hasArgs || c.Block != null ? "call" : "unknown-member";
        return AType.Unknown;
    }

    static bool HasMember(AType recv, string name, int argc)
    {
        if (recv.Sym != null && recv.Sym.FindMethod(name, argc) != null) return true;
        return recv.Clr != null && ClrTypes.Lookup(recv.Clr, name, argc, true, false) != null;
    }

    List<AType> BindArgTypes(List<Arg> args)
    {
        BindArgs(args);
        return args.Select(a => a.Value?.Type ?? AType.Unknown).ToList();
    }

    AType BindSelfCall(CallExpr c)
    {
        var argTypes = BindArgTypes(c.Args);
        var m = curType.FindMethod(c.Name, c.Args.Count) ?? Program.FindMethod(c.Name, c.Args.Count);
        if (m != null)
        {
            if (!m.IsStatic) UseThis();
            c.Res = "alice";
            c.Sym = m;
            BindBlockFor(c, m);
            return m.Ret;
        }
        if (c.Block != null) BindLambda(c.Block, new List<AType>());
        if (Builtins.FreeFunctions.Contains(c.Name))
        {
            c.Res = "free";
            return Builtins.FreeReturn(c.Name, argTypes);
        }
        c.Res = "call";
        return AType.Unknown;
    }

    /// <summary>A block passed to an Alice method binds with the parameter types of its `&block : A -> R`.</summary>
    void BindBlockFor(CallExpr c, MethodSym m)
    {
        if (c.Block == null) return;
        var bp = m.Params.FirstOrDefault(p => p.Block);
        var expected = bp != null && bp.Type.Kind == "func" ? bp.Type.Args.Take(bp.Type.Args.Count - 1).ToList() : new List<AType>();
        BindLambda(c.Block, expected);
    }
}
