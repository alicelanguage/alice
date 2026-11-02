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
        if (RewriteCall(c, recv) is { } rw)
        {
            c.Res = "asexpr";
            c.Sym = rw;
            return BindExpr(rw);
        }
        if (c.Name is "instance_eval" or "instance_exec" && c.Block != null && recv.Sym is TypeSym its) return BindInstanceEval(c, its);
        bool conc = IsConcurrentCall(c, recv);
        if (conc) MarkConcurrentArgs(c.Args);
        if (conc && IsIndexCall(c))
        {
            if (c.Block != null) indexLambdas.Add(c.Block);
            foreach (var a in c.Args) if (a.Value is LambdaExpr lam) indexLambdas.Add(lam);
        }
        bool savedRef = refAllowed;
        refAllowed = c.Recv is ConstExpr { Sym: AType { Clr: Type ic } } && (ic == typeof(Interlocked) || ic == typeof(Volatile) && false);
        try { return BindCallCore(c, recv, conc); }
        finally { refAllowed = savedRef; }
    }

    static bool IsDictType(AType t) => t.Clr is { IsGenericType: true } g && g.GetGenericTypeDefinition() == typeof(Dictionary<,>);

    static Expr Lit(string s, Node at) => new LitExpr { Kind = "str", Text = s, Line = at.Line, Col = at.Col };
    /// <summary>`t[:name]`; of a named tuple literal, its element itself (a one-element literal is no C# tuple).</summary>
    static Expr Element(Expr t, string name, Node at) => t is TupleLit { Names: { } ln } tl && ln.IndexOf(name) is int i and >= 0
        ? tl.Items[i]
        : new IndexExpr { Target = t, Args = [new Arg { Value = Lit(name, at) }], Line = at.Line, Col = at.Col };

    /// <summary>
    /// Calls that are other expressions once the receiver is typed (lowered through Res "asexpr"):
    /// `a === b` (Ruby case equality: a range's include?, a regex match, a type test, else ==), and the names of a
    /// named tuple, which exist only at compile time (`t.keys`, `t.to_h`, `t.merge(u)`, `t.to_s`, `t.inspect`).
    /// </summary>
    Expr? RewriteCall(CallExpr c, AType recv)
    {
        if (c.Name == "===" && c.Args.Count == 1 && c.Args[0].Value is { } rhs)
        {
            if (c.Recv is ConstExpr { Res: "type" } ce)
                return new TypeTestExpr { Kind = "is", E = new TypeTestExpr { Kind = "as", E = rhs, Ref = new TypeRef { Name = "Object", Nullable = true }, Line = c.Line }, Ref = ce.Ref, Line = c.Line, Col = c.Col };
            if (recv.Clr == typeof(System.Text.RegularExpressions.Regex))
                return new CallExpr { Recv = c.Recv, Name = "IsMatch", Args = c.Args, Parens = true, Line = c.Line, Col = c.Col };
            if (c.Recv is RangeExpr || recv.Clr is { IsGenericType: true } g && g.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                return new CallExpr { Recv = c.Recv, Name = "include?", Args = c.Args, Parens = true, Line = c.Line, Col = c.Col };
            return new BinaryExpr { Op = "==", L = c.Recv!, R = rhs, Line = c.Line, Col = c.Col };
        }
        if (recv.TupleNames is not { } names) return null;
        if (c.Args.Count == 0 && c.Block == null && names.Contains(c.Name)) return null;   // an element named `keys` / `values` wins over the helper
        switch (c.Name)
        {
            case "keys" when c.Args.Count == 0:
                return new ArrayLit { Items = names.Select(n => Lit(n, c)).ToList(), Line = c.Line, Col = c.Col };
            case "to_h" when c.Args.Count == 0 && c.Block == null:
                return new HashLit { Keys = names.Select(n => Lit(n, c)).ToList(), Values = names.Select(n => Element(c.Recv!, n, c)).ToList(), Line = c.Line, Col = c.Col };
            case "to_s" or "inspect" when c.Args.Count == 0:
                return NamedTupleText(c.Recv!, names, c);
            case "merge" when c.Args.Count == 1 && c.Args[0].Value is { } other && (other is TupleLit { Names: { } lon } ? lon : BindExpr(other).TupleNames) is { } on:
                {
                    var all = names.Concat(on.Where(n => !names.Contains(n))).ToList();
                    return new TupleLit { Names = all, Items = all.Select(n => Element(on.Contains(n) ? other : c.Recv!, n, c)).ToList(), Line = c.Line, Col = c.Col };
                }
        }
        return null;
    }

    /// <summary>`{name: "ada", age: 36}` as Ruby prints it: the names are compile-time only, so the text is built here.</summary>
    static Expr NamedTupleText(Expr t, List<string> names, Node at)
    {
        var ie = new InterpExpr { Line = at.Line, Col = at.Col };
        for (int i = 0; i < names.Count; i++)
        {
            ie.Chunks.Add((i == 0 ? "{" : ", ") + names[i] + ": ");
            ie.Holes.Add(new CallExpr { Recv = Element(t, names[i], at), Name = "inspect", Line = at.Line, Col = at.Col });
        }
        ie.Chunks.Add("}");
        return ie;
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

    /// <summary>A generic method `M<..T..>` of `t` whose result is one of its type parameters: that position, and
    /// whether the result is annotated nil-able (`T?`).</summary>
    static (int Pos, bool Nil)? GenericReturnArg(Type t, string name, int arity, int argc)
    {
        foreach (var m in t.GetMethods())
        {
            if (m.Name != name || !m.IsGenericMethodDefinition || m.GetGenericArguments().Length != arity) continue;
            var ps = m.GetParameters();
            if (ps.Count(p => !p.IsOptional) > argc || ps.Length < argc || !m.ReturnType.IsGenericParameter || m.ReturnType.DeclaringMethod == null) continue;
            var state = new System.Reflection.NullabilityInfoContext().Create(m.ReturnParameter).ReadState;
            return (m.ReturnType.GenericParameterPosition, state == System.Reflection.NullabilityState.Nullable);
        }
        return null;
    }

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
                    && m.GetGenericArguments().Length == targs.Count && m.GetParameters().Count(p => !p.IsOptional) <= c.Args.Count && m.GetParameters().Length >= c.Args.Count);
                c.Res = "clr";
                if (gm != null) return ClrTypes.FromClr(gm.MakeGenericMethod(targs.ToArray()!).ReturnType);
                // a generic extension method of a referenced library (ILGPU's LoadAutoGroupedStreamKernel<...>, FindControl<T>)
                if (Clr.IsExtensionName(c.Name) || Builtins.IsClrExtension(c.Name)) return Clr.ExtensionReturn(c.Name, recv, c.Args.Select(a => a.Value?.Type ?? AType.Unknown).ToList(), targs!);
                throw Err(c, $"{rc.Name} has no generic method {c.Name}<{targs.Count}> taking {c.Args.Count} argument(s)");
            }
            // an Alice type argument (no CLR type yet): `JsonSerializer.Deserialize<Array(Tea)>(s)` returns the argument
            // itself, nil-able when the method says so (`TValue?`)
            if (recv.Clr is { } rc2 && GenericReturnArg(rc2, c.Name, targs.Count, c.Args.Count) is ({ } pos, bool nil))
            {
                BindArgs(c.Args);
                c.Res = "clr";
                var at = c.TypeArgTypes[pos];
                return nil && at.IsKnown && at.Clr is not { IsValueType: true } && at.Kind is not ("prim" or "struct" or "enum") ? at.AsNullable() : at;
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
                // Ruby class discovery: Controller.subclasses / Controller.descendants (unless the class defines them)
                if (m == null && !hasArgs && c.Block == null && c.Name is "subclasses" or "descendants")
                {
                    c.Res = "classq";
                    c.Sym = owner;
                    return AType.Array(ClrTypes.FromClr(typeof(Type)));
                }
                if (m != null)
                {
                    c.Res = "alice";
                    c.Sym = m;
                    // `LibC.clock_gettime(0, out ts)`: the out local takes the declared parameter type
                    for (int i = 0; i < c.Args.Count && i < m.Params.Count; i++)
                        if (c.Args[i].OutLocal is { } ol && !ol.Type.IsKnown) ol.Type = m.Params[i].Type;
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
                // Alice spelling of a .NET static: Math.sqrt / Math.atan2 / Console.write_line -> Sqrt / Atan2 / WriteLine;
                // on Alice.Core types also `valid?` -> IsValid and `parse!` -> Parse (ADR 0036, Names.Dotnet)
                if (mem == null)
                    foreach (var alt in IsCoreType(owner.Clr) ? CoreSpellings(c.Name) : ClrTypes.Pascal(c.Name) is { } pascal ? [pascal] : [])
                        if (ClrTypes.Lookup(owner.Clr, alt, argc + (c.Block != null ? 1 : 0), hasArgs || c.Block != null, true, staticArgs) is { } pm
                            && (!c.Name.EndsWith('?') || pm.Type == AType.Bool))
                        {
                            c.Name = alt;
                            mem = pm;
                            break;
                        }
                if (mem != null)
                {
                    c.Res = mem.Kind == "method" ? "clr" : "field";
                    c.Sym = mem.Method;   // the purity gate checks [Pure] on Alice.Runtime methods
                    pendingConcurrent = conc && c.Block != null;
                    // the block's parameters take the delegate parameter's types (generic ones inferred from the arguments)
                    if (c.Block != null) BindLambda(c.Block, ClrTypes.BlockParamTypes(mem.Method, staticArgs));
                    return WithBlockResult(mem, c, staticArgs, t => ClrTypes.Lookup(owner.Clr, c.Name, argc + 1, true, true, t));
                }
            }
            // static extension members (`String.build { |io| ... }`): typed from Alice.Runtime.StaticExtensions
            if (owner.Clr == typeof(string) && ClrTypes.Lookup(typeof(Alice.Runtime.StaticExtensions), c.Name, argc + (c.Block != null ? 1 : 0), true, true, staticArgs) is { Kind: "method" } se)
            {
                c.Res = "clr";
                if (c.Block != null) BindLambda(c.Block, ClrTypes.BlockParamTypes(se.Method, staticArgs));
                return se.Type;
            }
            if (c.Block != null) BindLambda(c.Block, new List<AType>());
            c.Res = hasArgs || c.Block != null ? "clr" : "field";
            return AType.Unknown;
        }

        var argTypes = BindArgTypes(c.Args);

        // monkey patches of .NET types
        foreach (var x in Extensions)
        {
            if (x.ExtTarget == null || x.ExtTarget.Cs != recv.Cs.TrimEnd('?')) continue;
            if (x.RefineScope != null && !fileUsings.Contains(x.RefineScope) && curType != x) continue;   // a refinement: only under `using`
            var xm = x.FindMethod(c.Name, argc);
            if (xm == null) continue;
            CheckMutatingCall(c, recv, xm);
            c.Res = "alice";
            c.Sym = xm;
            BindBlockFor(c, xm);
            return CallRet(xm, argTypes);
        }

        // named tuple element: t.a
        if (recv.TupleNames is { } names && !hasArgs && c.Block == null && names.IndexOf(c.Name) is int ti and >= 0)
        {
            c.Res = "field";
            c.Name = Mangle(c.Name);
            return recv.Args[ti];
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
            return recv;   // AType.Any when the receiver was declared `Any` (strict mode accepts it)
        }

        // .NET members (also inherited by Alice classes from CLR bases)
        Type? clr = recv.Clr ?? recv.Sym?.Bases.Select(b => b.Clr).FirstOrDefault(b => b != null && !b.IsInterface);
        // an array of an Alice type has no CLR type at bind time; its System.Array members (`bricks.Length`) still bind
        if (clr == null && recv.Kind == "array" && !hasArgs && c.Block == null && ClrTypes.Lookup(typeof(Array), c.Name, 0, false, false) is { Kind: not "method" } am0)
        {
            c.Res = "field";
            return am0.Type;
        }
        if (clr != null)
        {
            var mem = ClrTypes.Lookup(clr, c.Name, argc + (c.Block != null ? 1 : 0), hasArgs || c.Block != null, false, argTypes);
            // Alice spelling of a .NET member: sw.elapsed / sb.append_line / d.contains_key?(k) / uri.absolute_uri?.
            // Prelude names (split, sum, empty?...) keep their Ruby meaning on the types Prelude extends; an Alice.Core
            // type's own members win over Prelude (it is the Ruby surface, written with .NET names, ADR 0036)
            bool core = IsCoreType(clr);
            bool preludeOwns = !core && Builtins.ExtNames.Contains(Mangle(c.Name)) && Builtins.ExtAccepts(Mangle(c.Name), clr);
            if (mem == null && !preludeOwns)
                foreach (var alt in core ? CoreSpellings(c.Name) : ClrTypes.ClrSpellings(c.Name))
                    if (ClrTypes.Lookup(clr, alt, argc + (c.Block != null ? 1 : 0), hasArgs || c.Block != null, false, argTypes) is { } am
                        && (!c.Name.EndsWith('?') || am.Type == AType.Bool))
                    {
                        c.Name = alt;
                        mem = am;
                        break;
                    }
            if (mem != null)
            {
                c.Res = mem.Kind == "method" ? "clr" : "field";
                if (mem.Kind == "method") CheckMutatingCall(c, recv, null);
                pendingConcurrent = conc && c.Block != null;
                if (c.Block != null) BindLambda(c.Block, ClrTypes.BlockParamTypes(mem.Method, argTypes));
                return WithBlockResult(mem, c, argTypes, t => ClrTypes.Lookup(clr, c.Name, argc + 1, true, false, t));
            }
        }

        // .NET extension methods (LINQ, MemoryExtensions): `xs.Sum`, `xs.Max` without parens are calls
        if (char.IsUpper(c.Name[0]) && (Builtins.IsClrExtension(c.Name) || Clr.IsExtensionName(c.Name)))
        {
            if (c.Block != null) BindLambda(c.Block, new List<AType>());
            c.Res = "clr";
            var known = Builtins.ClrExtReturn(c.Name, recv);
            // otherwise from the extension method's signature (generic arguments inferred as C# does)
            if (!known.IsUnknown || c.Block != null) return known;
            // explicit type arguments (`FindControl<TextBlock>("x")`) close the method; otherwise they are inferred
            var explicitArgs = c.TypeArgTypes.Select(t => t.Clr ?? ClrTypes.Proxy(t)).ToList();
            if (explicitArgs.Any(t => t == null)) return known;
            return Clr.ExtensionReturn(c.Name, recv, argTypes, explicitArgs.Count > 0 ? explicitArgs.OfType<Type>().ToList() : null);
        }

        // Prelude extension methods (Ruby names)
        string cs = Mangle(c.Name);
        // only the numeric conversions on text: the binder's receiver types are not exact enough to reject others
        if (Builtins.ExtNames.Contains(cs) && recv.IsString && cs.StartsWith("to_") && recv.Clr is { } rclr && !Builtins.ExtFits(cs, rclr))
            throw new CompileException([new Diagnostic("error", "AL0302", file, c.Line, Math.Max(1, c.Col) + (c.Recv != null ? 1 : 0),
                $"undefined method '{c.Name}' for {Alice.Compiler.Diagnostics.AliceNames.Type(recv.Cs)}" + (cs.StartsWith("to_") && recv.IsString ? $" (parse the text first: `s.to_i.{c.Name}`)" : ""), c.Name.Length)]);
        if (Builtins.ExtNames.Contains(cs))
        {
            c.Res = "ext";
            CheckMutatingCall(c, recv, null);
            AType? br = null;
            if (c.Block != null && cs == "tap") c.Block.DiscardValue = true;   // an Action: the block's value is dropped
            if (c.Block != null)
            {
                pendingConcurrent = conc;
                BindLambda(c.Block, Builtins.BlockParams(cs, recv, argTypes));
                br = c.Block.ReturnsValue ? c.Block.Result : AType.Void;
                if (cs == "filter_map" && br is { Nullable: true, Kind: "prim" } && Builtins.ElemOf(recv) is { IsKnown: true } fe && !IsDictType(recv))
                {
                    var rv = new AType(br.Kind, br.Cs.TrimEnd('?'), br.Clr) { Sym = br.Sym };
                    c.TypeArgTypes = [fe, rv];
                    return AType.Array(rv);
                }
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

    /// <summary>A type of the Alice Core standard library (`Alice.Core` and its sub-namespaces, ADR 0036).</summary>
    public static bool IsCoreType(Type? t) => t?.Namespace is { } ns && (ns == "Alice.Core" || ns.StartsWith("Alice.Core.", StringComparison.Ordinal));

    /// <summary>
    /// .NET names tried for an Alice spelling on an Alice.Core member, most specific first: the CLR spellings
    /// (`read_all` -> ReadAll, `empty?` -> IsEmpty / Empty), then ADR 0019's `Names.Dotnet` (`save!` -> Save,
    /// `has_key?` -> HasKey). Core is written once with .NET names and read natively from both languages.
    /// </summary>
    public static IEnumerable<string> CoreSpellings(string name)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in ClrTypes.ClrSpellings(name))
            if (seen.Add(s)) yield return s;
        var dotnet = Alice.Runtime.Names.Dotnet(name);
        if (dotnet != name && seen.Add(dotnet)) yield return dotnet;
    }

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

    /// <summary>A generic .NET method whose result depends on its block's (`Par.Map(xs) do |y| ... end`): looked up
    /// again with the bound block as a Func argument. Otherwise the type found first.</summary>
    AType WithBlockResult(ClrTypes.Member mem, CallExpr c, List<AType> args, Func<List<AType>, ClrTypes.Member?> again)
    {
        if (!mem.Type.IsUnknown || c.Block is not { ReturnsValue: true } b || mem.Method is not { IsGenericMethodDefinition: true }
            || !b.Result.IsKnown || !b.ParamSyms.All(p => p.Type.IsKnown)) return mem.Type;
        return again([.. args, Func(b.ParamSyms.Select(p => p.Type).ToList(), b.Result)])?.Type ?? mem.Type;
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
        if (c.Name is "puts" or "print")
            foreach (var a in c.Args)
                if (a.Value is { } v and not InterpExpr && BindExpr(v).TupleNames is { } tn) a.Value = NamedTupleText(v, tn, v);
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
            SelfCallEffects(c, m);
            c.Res = "alice";
            c.Sym = m;
            BindBlockFor(c, m);
            return CallRet(m, argTypes);
        }
        // a Kernel function (`format`, `puts`...) the patched .NET type does not have stays the Prelude function
        bool kernel = Builtins.FreeFunctions.Contains(c.Name) && c.Name != "to_json"
            && (curType.ExtTarget?.Clr is not { } et || ClrTypes.Lookup(et, c.Name, c.Args.Count, true, false) == null);
        if (curType.Kind == "extension" && !kernel)
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
        // an extension method on a .NET base class, called with implicit self (`FindControl<TextBlock>("x")` in an
        // Avalonia window): C# needs the receiver spelled, so make it `self.FindControl...`
        if (BaseClr(curType) is not null && curMethod is { IsStatic: false } && char.IsUpper(c.Name[0]) && Clr.IsExtensionName(c.Name))
        {
            c.Recv = new SelfExpr { Line = c.Line };
            return BindCall(c);
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
        if (Builtins.PreludeReturn(c.Name, c.Args.Count + (c.Block != null ? 1 : 0)) is { } pr) return pr;
        // the program's @[Native] probe, emitted by the backend (Emitter: `bool __alice_native_loaded()`)
        if (c.Name == "__alice_native_loaded" && c.Args.Count == 0) return AType.Bool;
        return AType.Unknown;
    }

    /// <summary>Crystal-style monomorphization: untyped parameters get one typed specialization per argument-type tuple.</summary>
    /// <summary>An `async def` returning T is seen by callers as Task&lt;T&gt; (Task when it returns nothing).</summary>
    public static AType Async(MethodSym m, AType ret) =>
        !m.IsAsync || m.IsAsyncIterator ? ret : ret.IsVoid || ret.IsUnknown ? ClrTypes.FromClr(typeof(Task)) : Builtins.Generic(typeof(Task<>), ret);

    AType CallRet(MethodSym m, List<AType> argTypes) => Async(m, CallRetCore(m, argTypes));

    AType CallRetCore(MethodSym m, List<AType> argTypes)
    {
        for (int i = 0; i < m.Params.Count && i < argTypes.Count; i++)
            if (m.Params[i] is { Inferred: false } hp)
                hp.Type = FlagHash(hp.Type, ref hp.DeclaredHash, ref hp.SawDefaultHash, ref hp.SawPlainHash, argTypes[i]);
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
