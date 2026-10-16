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
            var staticArgs = BindArgTypes(c.Args);
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
                var mem = ClrTypes.Lookup(owner.Clr, c.Name, argc + (c.Block != null ? 1 : 0), hasArgs || c.Block != null, true, staticArgs);
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

        // monkey patches of .NET types
        foreach (var x in Extensions)
        {
            if (x.ExtTarget == null || x.ExtTarget.Cs != recv.Cs.TrimEnd('?')) continue;
            var xm = x.FindMethod(c.Name, argc);
            if (xm == null) continue;
            c.Res = "alice";
            c.Sym = xm;
            BindBlockFor(c, xm);
            return CallRet(xm, argTypes);
        }

        // Alice instance members
        if (recv.Sym != null && recv.Sym.Kind is not ("module" or "program" or "lib" or "enum"))
        {
            var m = recv.Sym.FindMethod(c.Name, argc);
            if (m != null)
            {
                c.Res = "alice";
                c.Sym = m;
                BindBlockFor(c, m);
                return CallRet(m, argTypes);
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
            var mem = ClrTypes.Lookup(clr, c.Name, argc + (c.Block != null ? 1 : 0), hasArgs || c.Block != null, false, argTypes);
            if (mem != null)
            {
                c.Res = mem.Kind == "method" ? "clr" : "field";
                if (c.Block != null) BindLambda(c.Block, new List<AType>());
                return mem.Type;
            }
        }

        // .NET extension methods (LINQ, MemoryExtensions): `xs.Sum`, `xs.Max` without parens are calls
        if (char.IsUpper(c.Name[0]) && Builtins.IsClrExtension(c.Name))
        {
            if (c.Block != null) BindLambda(c.Block, new List<AType>());
            c.Res = "clr";
            return Builtins.ClrExtReturn(c.Name, recv);
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
            return CallRet(m, argTypes);
        }
        if (curType.Kind == "extension")
        {
            // implicit receiver inside a monkey patch is `self`
            c.Recv = new SelfExpr { Line = c.Line };
            return BindCall(c);
        }
        if (BaseClr(curType) is Type bc && ClrTypes.Lookup(bc, c.Name, c.Args.Count + (c.Block != null ? 1 : 0), true, false) is { } inherited)
        {
            UseThis();
            if (c.Block != null) BindLambda(c.Block, new List<AType>());
            c.Res = "clr";
            return inherited.Type;
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

    /// <summary>Crystal-style monomorphization: untyped parameters get one typed specialization per argument-type tuple.</summary>
    /// <summary>An `async def` returning T is seen by callers as Task&lt;T&gt; (Task when it returns nothing).</summary>
    public static AType Async(MethodSym m, AType ret) =>
        !m.IsAsync ? ret : ret.IsVoid || ret.IsUnknown ? ClrTypes.FromClr(typeof(Task)) : Builtins.Generic(typeof(Task<>), ret);

    AType CallRet(MethodSym m, List<AType> argTypes) => Async(m, CallRetCore(m, argTypes));

    AType CallRetCore(MethodSym m, List<AType> argTypes)
    {
        if (!m.HasInferredGenerics) return m.Ret;
        var args = new List<AType>();
        for (int i = 0; i < m.Params.Count; i++)
        {
            var p = m.Params[i];
            if (p.Inferred)
            {
                var a = i < argTypes.Count ? argTypes[i] : AType.Unknown;
                if (!a.IsKnown || a.Kind == "null" || a.Kind == "param") { m.NeedsGeneric = true; return AType.Unknown; }
                args.Add(a);
            }
            else args.Add(p.Type);
        }
        string key = string.Join("|", args.Select(a => a.Cs));
        if (!m.Instances.TryGetValue(key, out var inst))
        {
            inst = new Instance { Args = args };
            m.Instances[key] = inst;
            changed = true;
        }
        inst.Seen = true;
        return inst.Ret;
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
