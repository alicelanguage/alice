using Alice.Compiler.Syntax;

namespace Alice.Compiler.Semantics;

public sealed partial class Binder
{
    static readonly HashSet<string> Special = new() { "to_s", "class", "respond_to?", "send" };

    AType BindCall(CallExpr c)
    {
        if (c.Recv == null) return BindSelfCall(c);
        if (c.Name == "new" && GenericPathNew(c) is NewExpr ne)
        {
            c.Res = "asnew";
            c.Sym = ne;
            return BindExpr(ne);
        }
        AType recv = BindExpr(c.Recv);
        if (c.Name is "instance_eval" or "instance_exec" && c.Block != null && recv.Sym is TypeSym its) return BindInstanceEval(c, its);
        bool conc = IsConcurrentCall(c, recv);
        if (conc) MarkConcurrentArgs(c.Args);
        bool savedRef = refAllowed;
        refAllowed = c.Recv is ConstExpr { Sym: AType { Clr: Type ic } } && (ic == typeof(Interlocked) || ic == typeof(Volatile) && false);
        try { return BindCallCore(c, recv, conc); }
        finally { refAllowed = savedRef; }
    }

    int ievalN;

    /// <summary>
    /// `obj.instance_eval { ... }` / `obj.instance_exec(a) { |x| ... }`: the block runs once with obj as the
    /// implicit receiver — inlined as `begin __self = obj; x = a; body end` with receiverless calls on obj's
    /// methods redirected to __self (static resolution; no runtime self swap).
    /// </summary>
    AType BindInstanceEval(CallExpr c, TypeSym ts)
    {
        if (c.Sym is StmtExpr done) return BindExpr(done);   // re-bind passes reuse the rewrite
        var blk = c.Block!;
        string self = "__self" + ievalN++;
        var shadow = blk.Params.Select(p => p.Name).ToHashSet();
        bool Owns(string name) => ts.FindMethod(name, 0) != null || ts.FindField(name) != null || ts.FindMethod(name + "=", 1) != null;
        AstRewrite.Each(blk.Body, n => n switch
        {
            CallExpr { Recv: null } call when !shadow.Contains(call.Name) && Owns(call.Name) && FindLocal(call.Name) == null =>
                Redirect(call),
            // Ruby: @ivars inside instance_eval belong to the receiver (via its accessor, or reflection otherwise)
            IvarExpr { Name: [not '@', ..] } iv when ts.FindField(iv.Name) is { } fld && ts != curType =>
                fld.Kind is "getter" or "property"
                    ? new CallExpr { Recv = new NameExpr { Name = self, Line = iv.Line, Col = iv.Col }, Name = iv.Name, Line = iv.Line, Col = iv.Col }
                    : new TypeTestExpr { Kind = "as", E = new CallExpr { Recv = new NameExpr { Name = self, Line = iv.Line }, Name = "instance_variable_get", Args = { new Arg { Value = new LitExpr { Kind = "str", Text = iv.Name } } }, Parens = true, Line = iv.Line }, Ref = TypeRefOf(fld.Type), Line = iv.Line },
            NameExpr ne when !shadow.Contains(ne.Name) && FindLocal(ne.Name) == null && Owns(ne.Name) =>
                new CallExpr { Recv = new NameExpr { Name = self, Line = ne.Line, Col = ne.Col }, Name = ne.Name, Line = ne.Line, Col = ne.Col },
            _ => null,
        });
        CallExpr Redirect(CallExpr call)
        {
            AstRewrite.Each(call.Args.Select(a => (Stmt)new ExprStmt { Expr = a.Value! }).ToList(), _ => null);
            call.Recv = new NameExpr { Name = self, Line = call.Line, Col = call.Col };
            return call;
        }
        var body = new List<Stmt> { new Assign { Target = new NameExpr { Name = self, Line = c.Line }, Value = c.Recv!, Line = c.Line } };
        for (int i = 0; i < blk.Params.Count; i++)
            body.Add(new Assign { Target = new NameExpr { Name = blk.Params[i].Name, Line = c.Line }, Value = i == 0 && c.Name == "instance_eval" ? c.Recv! : c.Args[i].Value!, Line = c.Line });
        body.AddRange(blk.Body);
        var inline = new StmtExpr { Stmt = new BeginStmt { Body = body, Line = c.Line }, Line = c.Line };
        c.Res = "inline";
        c.Sym = inline;
        return BindExpr(inline);
    }

    static TypeRef TypeRefOf(AType t) => new() { Name = t.Clr?.FullName ?? t.Cs };

    AType BindCallCore(CallExpr c, AType recv, bool conc)
    {
        if (c.TypeArgs.Count > 0)
        {
            // explicit generic method arguments: `Channel.CreateUnbounded<Int32>()`
            c.TypeArgTypes = c.TypeArgs.Select(ResolveType).ToList();
            var targs = c.TypeArgTypes.Select(t => t.Clr).ToList();
            if (recv.Clr is { } rc && targs.All(t => t != null))
            {
                BindArgs(c.Args);
                var gm = rc.GetMethods().FirstOrDefault(m => m.Name == c.Name && m.IsGenericMethodDefinition
                    && m.GetGenericArguments().Length == targs.Count && m.GetParameters().Count(p => !p.IsOptional) <= c.Args.Count && m.GetParameters().Length >= c.Args.Count)
                    ?? throw Err(c, $"{rc.Name} has no generic method {c.Name}<{targs.Count}> taking {c.Args.Count} argument(s)");
                c.Res = "clr";
                return ClrTypes.FromClr(gm.MakeGenericMethod(targs.ToArray()!).ReturnType);
            }
        }
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
                    pendingConcurrent = conc && c.Block != null;
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
                // Alice spelling of a .NET static: Math.sqrt / Math.atan2 / Console.write_line -> Sqrt / Atan2 / WriteLine
                if (mem == null && ClrTypes.Pascal(c.Name) is { } pascal
                    && ClrTypes.Lookup(owner.Clr, pascal, argc + (c.Block != null ? 1 : 0), hasArgs || c.Block != null, true, staticArgs) is { } pm)
                {
                    c.Name = pascal;
                    mem = pm;
                }
                if (mem != null)
                {
                    c.Res = mem.Kind == "method" ? "clr" : "field";
                    c.Sym = mem.Method;   // the purity gate checks [Pure] on Alice.Runtime methods
                    pendingConcurrent = conc && c.Block != null;
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
            CheckMutatingCall(c, recv, xm);
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
                CheckMutatingCall(c, recv, m);
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
            // Ruby method_missing: unknown calls on such a class are routed to it (statically, no reflection)
            if (recv.Sym.FindMethod("method_missing", 2) is MethodSym mm && (recv.Clr == null || ClrTypes.Lookup(recv.Clr, c.Name, argc, hasArgs, false) == null)
                && BaseClrOf(recv.Sym) is not Type bcl || (BaseClrOf(recv.Sym) is Type bcl2 && ClrTypes.Lookup(bcl2, c.Name, argc, hasArgs, false) == null && recv.Sym.FindMethod("method_missing", 2) != null))
            {
                c.Res = "missing";
                c.Sym = recv.Sym.FindMethod("method_missing", 2);
                return ((MethodSym)c.Sym!).Ret;
            }
        }

        // a constrained type parameter: members come from its `where T : I` constraints
        if (recv.Kind == "param" && ConstraintTypes(recv.Cs) is { Count: > 0 } cts)
        {
            foreach (var ct in cts)
            {
                if (ct.Sym?.FindMethod(c.Name, argc) is MethodSym cm) { c.Res = "alice"; c.Sym = cm; BindBlockFor(c, cm); return cm.Ret; }
                if (ct.Clr != null && ClrTypes.Lookup(ct.Clr, c.Name, argc, hasArgs, false, argTypes) is { } mem)
                {
                    c.Res = mem.Kind == "method" ? "clr" : "field";
                    if (c.Block != null) BindLambda(c.Block, new List<AType>());
                    return mem.Type;
                }
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
                if (mem.Kind == "method") CheckMutatingCall(c, recv, null);
                pendingConcurrent = conc && c.Block != null;
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
            CheckMutatingCall(c, recv, null);
            AType? br = null;
            if (c.Block != null)
            {
                pendingConcurrent = conc;
                BindLambda(c.Block, Builtins.BlockParams(cs, recv, argTypes));
                br = c.Block.ReturnsValue ? c.Block.Result : AType.Void;
            }
            return Builtins.ExtReturn(cs, recv, argTypes, br) ?? AType.Unknown;
        }

        if (c.Block != null) BindLambda(c.Block, new List<AType>());
        c.Res = hasArgs || c.Block != null ? "call" : "unknown-member";
        return AType.Unknown;
    }

    /// <summary>
    /// `System.Collections.Concurrent.ConcurrentBag(Int32).new` parses as a call with constant arguments; when the
    /// dotted path names a generic type and the arguments name types, it is a generic construction.
    /// </summary>
    NewExpr? GenericPathNew(CallExpr c)
    {
        if (c.Recv is not CallExpr { Recv: ConstExpr owner, Block: null } g || g.Args.Count == 0) return null;
        if (!g.Args.All(a => a.Value is ConstExpr { Ref.Args.Count: 0 } || a.Value is ConstExpr)) return null;
        var targs = g.Args.Select(a => ((ConstExpr)a.Value!).Ref).ToList();
        if (targs.Any(t => TypeByName(t.Name, t.Args) == null)) return null;
        string path = owner.Ref.Name + "." + g.Name;
        if (TypeByName(path, targs) == null) return null;
        return new NewExpr { TypeRef = new TypeRef { Name = path, Args = targs, Line = c.Line }, Args = c.Args, Block = c.Block, Line = c.Line };
    }

    static Type? BaseClrOf(TypeSym t) => BaseClr(t);

    /// <summary>Constraint types of a type parameter in the current method/type (`where T : IArea`).</summary>
    List<AType> ConstraintTypes(string param)
    {
        var r = new List<AType>();
        var cs = new List<Constraint>();
        if (curMethod?.Decl != null) cs.AddRange(curMethod.Decl.Constraints);
        if (curType.Decl != null) cs.AddRange(curType.Decl.Constraints);
        foreach (var c in cs.Where(c => c.Param == param))
            foreach (var t in c.Types) r.Add(ResolveType(t));
        return r;
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
        if (c.Name == "define_method" && c.Block != null && c.Args.Count == 1 && curType != Program)
        {
            // runtime define_method (e.g. in a class-body loop): registered per class, reached via send/respond_to?
            BindArgs(c.Args);
            BindLambda(c.Block, c.Block.Params.Select(_ => AType.Object).ToList());
            c.Res = "defmethod";
            return AType.Void;
        }
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
        if (c.Block != null)
        {
            BindLambda(c.Block, new List<AType>());
            // Prelude function taking Action: the block's value is discarded (`expect { 1 + 1 }`, `it "x" do ... end`)
            var slots = typeof(Alice.Runtime.Prelude).GetMethods().Where(m => m.Name == c.Name && m.GetParameters().Length > c.Args.Count)
                .Select(m => m.GetParameters()[c.Args.Count].ParameterType).ToList();
            if (slots.Count > 0 && slots.All(t => typeof(Delegate).IsAssignableFrom(t) && t.GetMethod("Invoke")?.ReturnType == typeof(void)))
                c.Block.DiscardValue = true;
        }
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
