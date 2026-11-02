using Alice.Compiler.Lowering;
using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Air.Passes;

/// <summary>
/// Declaration-level pass for the IL backend (backend plan D1.6, ADR 0032/0033): makes explicit everything the C#
/// compiler synthesizes for the C# backend's output, as <see cref="AirTypeDecl"/> members with MIR bodies.
/// <list type="bullet">
/// <item>`record` / `record struct`: backing fields, init-only properties, primary constructor, EqualityContract,
/// Equals(object), Equals(T) (IEquatable&lt;T&gt;), GetHashCode, ToString + PrintMembers, Deconstruct, op_Equality,
/// op_Inequality, copy constructor + `&lt;Clone&gt;$`, and `__With(...)` (the `with` expression as a method). Shapes
/// and formulas are Roslyn's, so ToString text (`Point { x = 1, y = 2 }`) and hash values match the C# backend.</item>
/// <item>sum types (`enum` with fields): the abstract base record with a private constructor, an abstract `Tag`,
/// `Is&lt;Variant&gt;` tests, and one sealed nested record class per variant (`Value` singleton for unit variants).</item>
/// <item>operators derived as Backend/Emitter.cs derives them: `!=` from `==`, `&gt;` from `&lt;`, `&gt;=` from `&lt;=`.</item>
/// <item>the non-generic `IEnumerable.GetEnumerator` bridge, auto-properties (field + get/set), field and constant
/// initializers (<see cref="AirTypeDecl.FieldInits"/>, `.cctor`), default and struct constructors, `def to_s` as the
/// `ToString` override, and the module initializer that runs class bodies.</item>
/// </list>
/// Never called on the C# path: the C# backend keeps relying on Roslyn, and its output does not change.
/// </summary>
public static class Synthesize
{
    /// <summary>Synthesizes the declarations of a bound program; each type's result is also on <see cref="TypeSym.Synth"/>.</summary>
    public static AirModuleDecl Run(Binder binder) => new Pass(binder).Run();

    /// <summary>The CLR name of a C# identifier (`@base` is `base`).</summary>
    public static string ClrName(string cs) => cs.StartsWith('@') ? cs[1..] : cs;

    /// <summary>Roslyn's name for the backing field of an auto-property.</summary>
    public static string BackingField(string property) => "<" + property + ">k__BackingField";

    /// <summary>The multiplier of Roslyn's record GetHashCode.</summary>
    public const int HashFactor = -1521134295;

    sealed class Pass
    {
        readonly Binder binder;
        readonly Lowerer lowerer;
        readonly AirModuleDecl mod = new();
        readonly List<TypeSym> order = new();
        readonly Dictionary<TypeSym, List<TypeDecl>> decls = new();
        readonly Dictionary<TypeSym, EnumDecl> sums = new();
        readonly List<ConstDecl> programConsts = new();
        readonly List<TypeSym> classBodies = new();

        public Pass(Binder binder)
        {
            this.binder = binder;
            lowerer = new Lowerer(binder);
        }

        public AirModuleDecl Run()
        {
            foreach (var f in binder.Files) Collect(f.Items, true);
            foreach (var s in order)
            {
                AirTypeDecl? t = sums.TryGetValue(s, out var e) ? SumType(s, e) : decls.TryGetValue(s, out var ds) ? Type(s, ds) : null;
                if (t != null && (t.Fields.Count + t.Methods.Count + t.Properties.Count + t.Nested.Count > 0)) Add(t);
            }
            if (programConsts.Count > 0)
            {
                var p = Head(binder.Program, AirTypeKind.Module);
                p.Name = "Program";
                TypeInitializer(p, programConsts.Select(c => Assign(Member(TypeRef(p.Self), ClrName(((FieldSym)c.Sym!).CsName), Ty(((FieldSym)c.Sym!).Type)), Init(c.Value, binder.Program))).ToList());
                Add(p);
            }
            if (classBodies.Count > 0)
                mod.ModuleInitializer = new AirMethodDecl
                {
                    Name = ".cctor", Vis = AirVis.Private, Flags = AirMethodFlags.Static | AirMethodFlags.Ctor | AirMethodFlags.SpecialName,
                    Synth = AirSynth.ModuleInitializer,
                    Body = classBodies.Select(t => (MStmt)new MExprStmt { E = Call(TypeRef(t.Type.ToAir()), "__class_body", Void) }).ToList(),
                };
            return mod;
        }

        void Add(AirTypeDecl t)
        {
            mod.Types.Add(t);
            t.Sym.Synth = t;
        }

        /// <summary>Types in declaration order with all their (reopened) declarations; <paramref name="top"/>: file or
        /// namespace level, where constants belong to Program.</summary>
        void Collect(List<Item> items, bool top)
        {
            foreach (var it in items)
                switch (it)
                {
                    case NamespaceItem ns: Collect(ns.Items, top); break;
                    case TypeDecl { Sym: TypeSym s } td when s.Kind != "extension" && td.RefineScope == null:
                        if (!decls.TryGetValue(s, out var list)) { decls[s] = list = new(); order.Add(s); }
                        list.Add(td);
                        if (td.Members.Any(m => m is MethodDecl { Name: "__class_body" })) classBodies.Add(s);
                        Collect(td.Members, false);
                        break;
                    case EnumDecl { IsAdt: true, Sym: TypeSym es } e when !sums.ContainsKey(es):
                        sums[es] = e;
                        order.Add(es);
                        break;
                    case ConstDecl c when top: programConsts.Add(c); break;
                }
        }

        // ---- types ----

        AirTypeDecl Head(TypeSym s, AirTypeKind kind)
        {
            var t = new AirTypeDecl { Sym = s, Name = s.Name, Kind = kind, TypeParams = s.TypeParams.ToList() };
            var self = s.Type.ToAir();
            t.Self = self is AirType.AliceType a && s.TypeParams.Count > 0 ? a with { Args = s.TypeParams.Select(p => (AirType)new AirType.TypeParam(p)).ToList() } : self;
            return t;
        }

        static readonly AirType ObjectT = new AirType.Prim(PrimKind.Object);
        static readonly AirType ValueTypeT = Clr(typeof(ValueType));

        AirTypeDecl Type(TypeSym s, List<TypeDecl> ds)
        {
            var d = ds[0];
            var kind = d.Kind switch
            {
                "module" => AirTypeKind.Module,
                "interface" => AirTypeKind.Interface,
                "struct" or "record struct" => AirTypeKind.Struct,
                _ => AirTypeKind.Class,
            };
            var t = Head(s, kind);
            bool open = s.HasSubclass || ds.Any(x => x.IsAbstract || x.Attrs.Contains("Open"));
            t.IsRecord = d.Kind is "record" or "record struct";
            t.Abstract = kind == AirTypeKind.Module || kind == AirTypeKind.Interface || ds.Any(x => x.IsAbstract);
            t.Sealed = kind is AirTypeKind.Module or AirTypeKind.Struct || kind == AirTypeKind.Class && !open && !t.Abstract;
            t.ReadOnly = d.Kind == "record struct";
            t.BeforeFieldInit = kind != AirTypeKind.Interface;   // C# never marks an interface beforefieldinit
            foreach (var b in s.Bases)
            {
                var bt = b.ToAir();
                bool iface = b.Clr?.IsInterface ?? b.Sym?.Kind == "interface";
                if (!iface && t.Base == null && kind == AirTypeKind.Class) t.Base = bt;
                else t.Interfaces.Add(bt);
            }
            t.Base ??= kind switch { AirTypeKind.Class or AirTypeKind.Module => ObjectT, AirTypeKind.Struct => ValueTypeT, _ => null };
            var members = ds.SelectMany(x => x.Members).ToList();
            var methods = members.OfType<MethodDecl>().ToList();

            // initializers run in declaration order: instance ones in every constructor, static ones in `.cctor`
            var statics = new List<MStmt>();
            foreach (var m in members)
                switch (m)
                {
                    case AccessorDecl a:
                        if (Accessor(t, a) is ({ } target, { } ainit, var isStatic))
                            (isStatic ? statics : t.FieldInits).Add(Assign(target, Init(ainit, s)));
                        break;
                    case FieldDecl { Init: { } init, Sym: FieldSym fs }:
                        (fs.IsStatic ? statics : t.FieldInits).Add(Assign(Member(fs.IsStatic ? TypeRef(t.Self) : This(t), ClrName(fs.CsName), Ty(fs.Type)), Init(init, s)));
                        break;
                    case ConstDecl { Sym: FieldSym cs } c:
                        statics.Add(Assign(Member(TypeRef(t.Self), ClrName(cs.CsName), Ty(cs.Type)), Init(c.Value, s)));
                        break;
                }
            if (t.IsRecord) Record(t, s, null, -1, !open);
            TypeInitializer(t, statics);
            DerivedOperators(t, s, methods);
            EnumerableBridge(t, s, methods);
            Constructors(t, s, d, methods);
            ToStringOverride(t, s, methods);
            return t;
        }

        /// <summary>`.cctor` running static field, accessor and constant initializers (or a unit variant's singleton) in order.</summary>
        static void TypeInitializer(AirTypeDecl t, List<MStmt> body)
        {
            if (body.Count == 0) return;
            body.Add(new MReturn());
            t.Methods.Add(new AirMethodDecl
            {
                Name = ".cctor", Vis = AirVis.Private, Flags = AirMethodFlags.Static | AirMethodFlags.Ctor | AirMethodFlags.SpecialName,
                Synth = AirSynth.TypeInitializer, Body = body,
            });
        }

        /// <summary>The initializer expression lowered as Backend/Emitter.cs lowers it (its pre-statements dropped likewise).</summary>
        MExpr Init(Expr e, TypeSym owner)
        {
            lowerer.Type = owner;
            lowerer.Method = null;
            var tmp = lowerer.LowerBody(new List<Stmt> { new ExprStmt { Expr = e, Line = e.Line } }, Tail.Return, new List<LocalSym>(), e.Line);
            lowerer.Extra.Clear();
            return ((MReturn)tmp[^1]).Value!;
        }

        // ---- auto-properties ----

        /// <summary>An auto-property: its backing field and accessors (abstract ones in an interface). Returns the backing
        /// field and the initializer to run, if any.</summary>
        static (MExpr? Target, Expr? Init, bool Static) Accessor(AirTypeDecl t, AccessorDecl a)
        {
            if (a.Sym is not FieldSym f || a.Kind is not ("getter" or "setter" or "property")) return (null, null, false);   // events stay with D0.8
            {
                string name = ClrName(f.CsName);
                var ty = Ty(f.Type);
                var flags = f.IsStatic ? AirMethodFlags.Static : AirMethodFlags.None;
                var p = new AirPropertyDecl { Name = name, Type = ty, Static = f.IsStatic, Synth = AirSynth.Getter };
                if (t.Kind == AirTypeKind.Interface)
                {
                    p.Get = Accessor("get_" + name, AirVis.Public, flags | AirMethodFlags.Virtual | AirMethodFlags.Abstract, ty, null, null, AirSynth.Getter);
                    if (a.Kind != "getter")
                        p.Set = Accessor("set_" + name, AirVis.Public, flags | AirMethodFlags.Virtual | AirMethodFlags.Abstract, Void, ty, null, AirSynth.Setter);
                }
                else
                {
                    string bf = BackingField(name);
                    t.Fields.Add(new AirFieldDecl { Name = bf, Type = ty, Static = f.IsStatic, Synth = AirSynth.BackingField, Field = f });
                    MExpr store = f.IsStatic ? TypeRef(t.Self) : This(t);
                    p.Get = Accessor("get_" + name, a.Kind == "setter" ? AirVis.Private : AirVis.Public, flags, ty, null,
                        [new MReturn { Value = Member(store, bf, ty) }], AirSynth.Getter);
                    p.Set = Accessor("set_" + name, a.Kind == "getter" ? AirVis.Private : AirVis.Public, flags, Void, ty,
                        [Assign(Member(store, bf, ty), Param("value", ty)), new MReturn()], AirSynth.Setter);
                }
                t.Methods.Add(p.Get);
                if (p.Set != null) t.Methods.Add(p.Set);
                t.Properties.Add(p);
                return t.Kind == AirTypeKind.Interface || a.Init == null ? (null, null, false)
                    : (Member(f.IsStatic ? TypeRef(t.Self) : This(t), BackingField(name), ty), a.Init, f.IsStatic);
            }
        }

        static AirMethodDecl Accessor(string name, AirVis vis, AirMethodFlags flags, AirType ret, AirType? value, List<MStmt>? body, AirSynth synth) => new()
        {
            Name = name, Vis = vis, Flags = flags | AirMethodFlags.SpecialName, Ret = ret,
            Params = value != null ? [new AirParamDecl("value", value)] : [], Body = body, Synth = synth,
        };

        // ---- records ----

        /// <summary>
        /// Roslyn's record members for a record class or record struct (<paramref name="baseRec"/>: the sum-type base of
        /// a variant, whose members are called as `base.X`). <paramref name="tag"/> is the variant index (-1: none).
        /// </summary>
        void Record(AirTypeDecl t, TypeSym s, AirTypeDecl? baseRec, int tag, bool isSealed)
        {
            bool isStruct = t.Kind == AirTypeKind.Struct;
            var self = t.Self;
            var selfQ = isStruct ? self : self with { Nullable = true };
            var fields = s.FieldOrder.Select(n => s.Fields[n]).Select(f => (Prop: ClrName(f.CsName), Field: BackingField(ClrName(f.CsName)), Type: Ty(f.Type), Sym: f)).ToList();
            var userMethods = s.Methods.Keys.ToHashSet();
            t.Interfaces.Add(Clr(typeof(IEquatable<>), self));

            // backing fields + init-only properties
            foreach (var f in fields)
            {
                t.Fields.Add(new AirFieldDecl { Name = f.Field, Type = f.Type, ReadOnly = true, Synth = AirSynth.BackingField, Field = f.Sym });
                var get = Accessor("get_" + f.Prop, AirVis.Public, AirMethodFlags.None, f.Type, null, [new MReturn { Value = Member(This(t), f.Field, f.Type) }], AirSynth.Getter);
                var set = Accessor("set_" + f.Prop, AirVis.Public, AirMethodFlags.InitOnly, Void, f.Type,
                    [Assign(Member(This(t), f.Field, f.Type), Param("value", f.Type)), new MReturn()], AirSynth.Setter);
                t.Methods.Add(get);
                t.Methods.Add(set);
                t.Properties.Add(new AirPropertyDecl { Name = f.Prop, Type = f.Type, Get = get, Set = set, Synth = AirSynth.Getter });
            }

            // primary constructor: fields, then the base constructor
            if (fields.Count > 0 || baseRec != null)
            {
                var body = fields.Select(f => (MStmt)Assign(Member(This(t), f.Field, f.Type), Param(f.Prop, f.Type))).ToList();
                body.AddRange(t.FieldInits);
                if (!isStruct) body.Add(BaseCtor(t, baseRec?.Self ?? ObjectT));
                body.Add(new MReturn());
                t.Methods.Add(Ctor(AirVis.Public, fields.Select(f => new AirParamDecl(f.Prop, f.Type)), body, AirSynth.PrimaryCtor));
            }
            else if (!isStruct) t.Methods.Add(Ctor(AirVis.Public, [], [.. t.FieldInits, BaseCtor(t, ObjectT), new MReturn()], AirSynth.DefaultCtor));

            var typeT = Clr(typeof(Type));
            var sb = Clr(typeof(System.Text.StringBuilder));

            // EqualityContract
            if (!isStruct)
            {
                var flags = baseRec != null ? AirMethodFlags.Virtual | AirMethodFlags.Override | (isSealed ? AirMethodFlags.Final : 0)
                    : isSealed ? AirMethodFlags.None : AirMethodFlags.Virtual;
                var get = Accessor("get_EqualityContract", isSealed && baseRec == null ? AirVis.Private : AirVis.Protected, flags, typeT, null,
                    [new MReturn { Value = TypeOf(self) }], AirSynth.EqualityContract);
                t.Methods.Add(get);
                t.Properties.Add(new AirPropertyDecl { Name = "EqualityContract", Type = typeT, Get = get, Synth = AirSynth.EqualityContract });
            }

            // ToString + PrintMembers
            var builder = Param("builder", sb);
            var pm = new List<MStmt> { new MExprStmt { E = Call(TypeRef(Clr(typeof(System.Runtime.CompilerServices.RuntimeHelpers))), "EnsureSufficientExecutionStack", Void) } };
            if (fields.Count == 0)
                pm.Add(new MReturn { Value = baseRec != null ? Call(Base(baseRec.Self), "PrintMembers", Bool, builder) : Const(false) });
            else
            {
                if (baseRec != null)
                    pm.Add(new MIf { Cond = Call(Base(baseRec.Self), "PrintMembers", Bool, builder), Then = [Append(builder, Const(", "))] });
                for (int i = 0; i < fields.Count; i++)
                {
                    var f = fields[i];
                    pm.Add(Append(builder, Const((i > 0 ? ", " : "") + f.Prop + " = ")));
                    var v = Member(This(t), f.Prop, f.Type);
                    // Roslyn: value types append their ToString(), everything else appends as object (null appends nothing)
                    pm.Add(Append(builder, IsValueType(f.Type) ? Call(v, "ToString", Str) : Cast(ObjectT, v)));
                }
                pm.Add(new MReturn { Value = Const(true) });
            }
            t.Methods.Add(new AirMethodDecl
            {
                Name = "PrintMembers", Vis = isSealed && baseRec == null ? AirVis.Private : AirVis.Protected,
                Flags = baseRec != null ? AirMethodFlags.Virtual | AirMethodFlags.Override : isSealed ? AirMethodFlags.None : AirMethodFlags.Virtual,
                Params = [new AirParamDecl("builder", sb)], Ret = Bool, Body = pm, Synth = AirSynth.PrintMembers,
            });
            if (!userMethods.Contains("to_s"))
            {
                var sbl = Local("builder", sb);
                t.Methods.Add(new AirMethodDecl
                {
                    Name = "ToString", Flags = AirMethodFlags.Virtual | AirMethodFlags.Override, Ret = Str, Synth = AirSynth.ToString,
                    Body =
                    [
                        new MDecl { Name = "builder", TypeCs = sb.ToCs(), Init = New(sb) },
                        Append(sbl, Const(s.Name)),
                        Append(sbl, Const(" { ")),
                        new MIf { Cond = Call(This(t), "PrintMembers", Bool, sbl), Then = [Append(sbl, Const(' '))] },
                        Append(sbl, Const('}')),
                        new MReturn { Value = Call(sbl, "ToString", Str) },
                    ],
                });
            }

            // equality
            var other = Param("other", selfQ);
            var cmp = new List<MExpr>();
            foreach (var f in fields)
                cmp.Add(Call(Comparer(f.Type), "Equals", Bool, Member(This(t), f.Field, f.Type), Member(other, f.Field, f.Type)));
            MExpr eqT;
            if (isStruct) eqT = cmp.Count == 0 ? Const(true) : cmp.Aggregate((a, b) => Bin(AirBinOp.And, a, b));
            else
            {
                MExpr head = baseRec != null
                    ? Call(Base(baseRec.Self), "Equals", Bool, Cast(baseRec.Self with { Nullable = true }, other))
                    : Bin(AirBinOp.And, Bin(AirBinOp.Ne, Cast(ObjectT, other), Const(null)),
                        Call(TypeRef(typeT), "op_Equality", Bool, Member(This(t), "EqualityContract", typeT), Member(other, "EqualityContract", typeT)));
                eqT = Bin(AirBinOp.Or, Bin(AirBinOp.Eq, Cast(ObjectT, This(t)), Cast(ObjectT, other)), cmp.Aggregate(head, (a, b) => Bin(AirBinOp.And, a, b)));
            }
            var eqFlags = isSealed ? AirMethodFlags.Virtual | AirMethodFlags.Final : AirMethodFlags.Virtual;   // IEquatable<T>.Equals
            t.Methods.Add(new AirMethodDecl { Name = "Equals", Flags = eqFlags, Params = [new("other", selfQ)], Ret = Bool, Body = [new MReturn { Value = eqT }], Synth = AirSynth.EqualsTyped });
            var obj = Param("obj", ObjectT with { Nullable = true });
            MExpr eqObj = isStruct
                ? Bin(AirBinOp.And, Is(obj, self), Call(This(t), "Equals", Bool, Cast(self, obj)))
                : Call(This(t), "Equals", Bool, As(obj, self));
            t.Methods.Add(new AirMethodDecl { Name = "Equals", Flags = AirMethodFlags.Virtual | AirMethodFlags.Override, Params = [new("obj", ObjectT with { Nullable = true })], Ret = Bool, Body = [new MReturn { Value = eqObj }], Synth = AirSynth.EqualsObject });
            if (baseRec != null)
            {
                var bq = baseRec.Self with { Nullable = true };
                t.Methods.Add(new AirMethodDecl
                {
                    Name = "Equals", Flags = AirMethodFlags.Virtual | AirMethodFlags.Override | AirMethodFlags.Final, Params = [new("other", bq)], Ret = Bool,
                    Body = [new MReturn { Value = Call(This(t), "Equals", Bool, Cast(ObjectT with { Nullable = true }, Param("other", bq))) }], Synth = AirSynth.EqualsBase,
                });
            }

            // GetHashCode: Roslyn's formula, unchecked
            MExpr? hash = isStruct ? null
                : baseRec != null ? Call(Base(baseRec.Self), "GetHashCode", Int)
                : Call(Comparer(typeT), "GetHashCode", Int, Member(This(t), "EqualityContract", typeT));
            foreach (var f in fields)
            {
                var h = Call(Comparer(f.Type), "GetHashCode", Int, Member(This(t), f.Field, f.Type));
                hash = hash == null ? h : Bin(AirBinOp.Add, Bin(AirBinOp.Mul, hash, Const(HashFactor), false), h, false);
            }
            t.Methods.Add(new AirMethodDecl { Name = "GetHashCode", Flags = AirMethodFlags.Virtual | AirMethodFlags.Override, Ret = Int, Body = [new MReturn { Value = hash ?? Const(0) }], Synth = AirSynth.GetHashCode });

            // operators
            var l = Param("left", selfQ);
            var r = Param("right", selfQ);
            MExpr opEq = isStruct ? Call(l, "Equals", Bool, r)
                : Bin(AirBinOp.Or, Bin(AirBinOp.Eq, Cast(ObjectT, l), Cast(ObjectT, r)), Bin(AirBinOp.And, Bin(AirBinOp.Ne, Cast(ObjectT, l), Const(null)), Call(l, "Equals", Bool, r)));
            t.Methods.Add(Operator("op_Equality", selfQ, opEq, AirSynth.OpEquality));
            t.Methods.Add(Operator("op_Inequality", selfQ, Not(Call(TypeRef(self), "op_Equality", Bool, l, r)), AirSynth.OpInequality));

            // Deconstruct
            if (fields.Count > 0)
                t.Methods.Add(new AirMethodDecl
                {
                    Name = "Deconstruct", Params = fields.Select(f => new AirParamDecl(f.Prop, new AirType.ByRef(f.Type, RefKind.Out))).ToList(),
                    Body = [.. fields.Select(f => (MStmt)Assign(Param(f.Prop, new AirType.ByRef(f.Type, RefKind.Out)), Member(This(t), f.Prop, f.Type))), new MReturn()],
                    Synth = AirSynth.Deconstruct,
                });

            // copy constructor + <Clone>$ (classes); `with` as __With(all fields): a copy with the fields replaced
            if (!isStruct)
            {
                var orig = Param("original", self);
                var copy = new List<MStmt> { baseRec != null ? BaseCtor(t, baseRec.Self, Cast(baseRec.Self, orig)) : BaseCtor(t, ObjectT) };
                foreach (var f in t.Fields.Where(f => !f.Static))
                    copy.Add(Assign(Member(This(t), f.Name, f.Type), Member(orig, f.Name, f.Type)));
                copy.Add(new MReturn());
                t.Methods.Add(Ctor(isSealed ? AirVis.Private : AirVis.Protected, [new("original", self)], copy, AirSynth.CopyCtor));
                var cloneRet = baseRec?.Self ?? self;
                t.Methods.Add(new AirMethodDecl
                {
                    Name = "<Clone>$", Ret = cloneRet, Synth = AirSynth.Clone,
                    Flags = baseRec != null ? AirMethodFlags.Virtual | AirMethodFlags.Override : isSealed ? AirMethodFlags.None : AirMethodFlags.Virtual,
                    Body = [new MReturn { Value = New(self, This(t)) }],
                });
            }
            if (fields.Count > 0)
            {
                MExpr start = isStruct ? This(t) : baseRec != null ? Cast(self, Call(This(t), "<Clone>$", baseRec.Self)) : Call(This(t), "<Clone>$", self);
                var c = Local("__c", self);
                t.Methods.Add(new AirMethodDecl
                {
                    Name = "__With", Ret = self, Synth = AirSynth.With,
                    Params = fields.Select(f => new AirParamDecl(f.Prop, f.Type)).ToList(),
                    Body =
                    [
                        new MDecl { Name = "__c", TypeCs = self.ToCs(), Init = start },
                        .. fields.Select(f => (MStmt)Assign(Member(c, f.Prop, f.Type), Param(f.Prop, f.Type))),
                        new MReturn { Value = c },
                    ],
                });
            }

            if (tag >= 0)
            {
                var get = Accessor("get_Tag", AirVis.Public, AirMethodFlags.Virtual | AirMethodFlags.Override, Int, null, [new MReturn { Value = Const(tag) }], AirSynth.Tag);
                t.Methods.Add(get);
                t.Properties.Add(new AirPropertyDecl { Name = "Tag", Type = Int, Get = get, Synth = AirSynth.Tag });
            }
        }

        static AirMethodDecl Operator(string name, AirType operand, MExpr value, AirSynth synth, string l = "left", string r = "right") => new()
        {
            Name = name, Flags = AirMethodFlags.Static | AirMethodFlags.SpecialName, Ret = Bool, Synth = synth,
            Params = [new(l, operand), new(r, operand)], Body = [new MReturn { Value = value }],
        };

        static AirMethodDecl Ctor(AirVis vis, IEnumerable<AirParamDecl> ps, List<MStmt> body, AirSynth synth) => new()
        {
            Name = ".ctor", Vis = vis, Flags = AirMethodFlags.Ctor | AirMethodFlags.SpecialName, Params = ps.ToList(), Body = body, Synth = synth,
        };

        static MStmt BaseCtor(AirTypeDecl t, AirType baseType, params MExpr[] args) => new MExprStmt { E = Call(Base(baseType), ".ctor", Void, args) };

        // ---- sum types ----

        AirTypeDecl SumType(TypeSym es, EnumDecl e)
        {
            var t = Head(es, AirTypeKind.Class);
            t.IsRecord = true;
            t.Abstract = true;
            t.Base = ObjectT;
            // the base: private constructor, an abstract record with no fields, `Tag`, `Is<Variant>`
            Record(t, es, null, -1, false);
            t.Methods.RemoveAll(m => m.Synth == AirSynth.DefaultCtor || m.Synth == AirSynth.Clone);
            t.Methods.Insert(0, Ctor(AirVis.Private, [], [BaseCtor(t, ObjectT), new MReturn()], AirSynth.PrivateCtor));
            t.Methods.Add(new AirMethodDecl { Name = "<Clone>$", Ret = t.Self, Flags = AirMethodFlags.Virtual | AirMethodFlags.Abstract, Synth = AirSynth.Clone });
            var tagGet = Accessor("get_Tag", AirVis.Public, AirMethodFlags.Virtual | AirMethodFlags.Abstract, Int, null, null, AirSynth.Tag);
            t.Methods.Add(tagGet);
            t.Properties.Add(new AirPropertyDecl { Name = "Tag", Type = Int, Get = tagGet, Synth = AirSynth.Tag });
            for (int i = 0; i < es.Variants.Count; i++)
            {
                var vs = es.Variants[i];
                var isGet = Accessor("get_Is" + vs.Name, AirVis.Public, AirMethodFlags.None, Bool, null,
                    [new MReturn { Value = Bin(AirBinOp.Eq, Member(This(t), "Tag", Int), Const(i)) }], AirSynth.IsVariant);
                t.Methods.Add(isGet);
                t.Properties.Add(new AirPropertyDecl { Name = "Is" + vs.Name, Type = Bool, Get = isGet, Synth = AirSynth.IsVariant });
            }
            foreach (var (vs, i) in es.Variants.Select((v, i) => (v, i)))
            {
                var v = Head(vs, AirTypeKind.Class);
                v.IsRecord = true;
                v.Sealed = true;
                v.Base = t.Self;
                v.Enclosing = t;
                Record(v, vs, t, i, true);
                if (vs.FieldOrder.Count == 0)
                {
                    // `public static readonly Empty Value = new();`
                    v.Fields.Add(new AirFieldDecl { Name = "Value", Type = v.Self, Vis = AirVis.Public, Static = true, ReadOnly = true, Synth = AirSynth.UnitValue });
                    v.Methods.Add(new AirMethodDecl
                    {
                        Name = ".cctor", Vis = AirVis.Private, Flags = AirMethodFlags.Static | AirMethodFlags.Ctor | AirMethodFlags.SpecialName, Synth = AirSynth.TypeInitializer,
                        Body = [Assign(Member(TypeRef(v.Self), "Value", v.Self), New(v.Self)), new MReturn()],
                    });
                }
                vs.Synth = v;
                t.Nested.Add(v);
            }
            return t;
        }

        // ---- operators, enumerable bridge, constructors, to_s ----

        void DerivedOperators(AirTypeDecl t, TypeSym s, List<MethodDecl> methods)
        {
            if (t.IsRecord || t.Kind is AirTypeKind.Interface or AirTypeKind.Module) return;
            var ops = methods.Select(m => m.Name).ToHashSet();
            var a = Param("a", t.Self);
            var b = Param("b", t.Self);
            void Derive(string name, MExpr value) => t.Methods.Add(Operator(name, t.Self, value, AirSynth.DerivedOperator, "a", "b"));
            // Backend/Emitter.cs: `!(a == b)`, `b < a`, `b <= a`, calling the user's operator methods
            if (ops.Contains("==") && !ops.Contains("!=")) Derive("op_Inequality", Not(Call(a, "op_Eq", Bool, b)));
            if (ops.Contains("<") && !ops.Contains(">")) Derive("op_GreaterThan", Call(b, "op_Lt", Bool, a));
            if (ops.Contains("<=") && !ops.Contains(">=")) Derive("op_GreaterThanOrEqual", Call(b, "op_Le", Bool, a));
        }

        void EnumerableBridge(AirTypeDecl t, TypeSym s, List<MethodDecl> methods)
        {
            bool implementsEnumerable = s.EnumerableElem != null
                || s.Bases.Any(b => b.Clr is { IsGenericType: true } c && c.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            if (!implementsEnumerable || !methods.Any(m => m.Name == "GetEnumerator") || methods.Any(m => m.Name.Contains("IEnumerable.GetEnumerator"))) return;
            var user = s.Methods.TryGetValue("GetEnumerator", out var list) ? list.FirstOrDefault(m => m.Params.Count == 0) : null;
            var ret = Clr(typeof(System.Collections.IEnumerator));
            t.Methods.Add(new AirMethodDecl
            {
                Name = "System.Collections.IEnumerable.GetEnumerator", Vis = AirVis.Private, Flags = AirMethodFlags.Virtual | AirMethodFlags.Final,
                Ret = ret, Implements = (Clr(typeof(System.Collections.IEnumerable)), "GetEnumerator"), Synth = AirSynth.EnumerableBridge,
                Body = [new MReturn { Value = Call(This(t), "GetEnumerator", user != null ? Ty(user.Ret) : ret) }],
            });
        }

        void Constructors(AirTypeDecl t, TypeSym s, TypeDecl d, List<MethodDecl> methods)
        {
            if (t.IsRecord || t.Kind is AirTypeKind.Interface or AirTypeKind.Module) return;
            bool userCtor = methods.Any(m => m.Name == "initialize");
            if (t.Kind == AirTypeKind.Struct)
            {
                // C# runs struct field initializers only through a declared constructor: Emitter.cs declares `S() { }`
                if (!userCtor && t.FieldInits.Count > 0)
                    t.Methods.Add(Ctor(AirVis.Public, [], [Assign(This(t), new AirDefault(t.Self)), .. t.FieldInits, new MReturn()], AirSynth.StructCtor));
                return;
            }
            if (userCtor) return;   // user constructors get FieldInits prepended by the IL backend
            t.Methods.Add(Ctor(t.Abstract ? AirVis.Protected : AirVis.Public, [], [.. t.FieldInits, BaseCtor(t, t.Base ?? ObjectT), new MReturn()], AirSynth.DefaultCtor));
        }

        void ToStringOverride(AirTypeDecl t, TypeSym s, List<MethodDecl> methods)
        {
            if (t.Kind is AirTypeKind.Interface or AirTypeKind.Module) return;
            if (methods.FirstOrDefault(m => m.Name == "to_s" && m.Params.Count == 0 && m.Sym is MethodSym) is not { Sym: MethodSym user }) return;
            t.Methods.Add(new AirMethodDecl { Name = "ToString", Flags = AirMethodFlags.Virtual | AirMethodFlags.Override, Ret = Str, User = user, Synth = AirSynth.ToStringOverride });
        }

        // ---- MIR leaves ----

        static readonly AirType Bool = new AirType.Prim(PrimKind.Bool);
        static readonly AirType Int = new AirType.Prim(PrimKind.Int);
        static readonly AirType Str = new AirType.Prim(PrimKind.String);
        static readonly AirType Void = new AirType.Prim(PrimKind.Void);

        /// <summary>A binder type as AIR; an uninferred type is `dynamic`, as Lowerer.TypeCs spells it.</summary>
        static AirType Ty(AType t) => t.IsUnknown ? new AirType.Prim(PrimKind.Dynamic) : t.ToAir();

        static AirType Clr(Type t, params AirType[] args)
        {
            string name = "global::" + (t.IsGenericType ? t.FullName![..t.FullName!.IndexOf('`')] : t.FullName!);
            if (args.Length == 0) return new AirType.ClrRef(name, t, []);
            var closed = args.Select(ClrOf).ToArray();
            return new AirType.ClrRef(name, closed.All(c => c != null) ? t.MakeGenericType(closed!) : t, args);
        }

        static Type? ClrOf(AirType t) => t switch
        {
            AirType.Prim { Kind: var k } when !t.Nullable => k switch
            {
                PrimKind.Bool => typeof(bool), PrimKind.Char => typeof(char), PrimKind.SByte => typeof(sbyte), PrimKind.Byte => typeof(byte),
                PrimKind.Short => typeof(short), PrimKind.UShort => typeof(ushort), PrimKind.Int => typeof(int), PrimKind.UInt => typeof(uint),
                PrimKind.Long => typeof(long), PrimKind.ULong => typeof(ulong), PrimKind.Float => typeof(float), PrimKind.Double => typeof(double),
                PrimKind.Decimal => typeof(decimal), PrimKind.String => typeof(string), PrimKind.Object => typeof(object), _ => null,
            },
            AirType.Prim { Kind: PrimKind.String or PrimKind.Object } p => p.Kind == PrimKind.String ? typeof(string) : typeof(object),
            AirType.ClrRef { Clr: { ContainsGenericParameters: false } c } r when !r.Nullable || !c.IsValueType => c,
            _ => null,
        };

        static bool IsValueType(AirType t) => t switch
        {
            AirType.Prim { Kind: var k } => k is not (PrimKind.String or PrimKind.Object or PrimKind.Dynamic or PrimKind.Null),
            AirType.ClrRef { Clr: { } c } => c.IsValueType,
            AirType.AliceType { Sym: var s } => s.Type.Kind is "struct" or "enum",
            AirType.Tuple => true,
            _ => false,   // type parameters box through `(object)x`, as Roslyn does
        };

        /// <summary>`EqualityComparer&lt;T&gt;.Default`.</summary>
        static MExpr Comparer(AirType t)
        {
            var c = Clr(typeof(EqualityComparer<>), t);
            return new MMember { Target = TypeRef(c), Name = "Default", Type = c };
        }

        static MExpr This(AirTypeDecl t) => new AirThis { Type = t.Self };
        /// <summary>`base`: a non-virtual call on `this` to a member of <paramref name="baseType"/> (the node's type).</summary>
        static MExpr Base(AirType baseType) => new AirBase { Type = baseType };
        static MExpr Local(string name, AirType t) => new AirLocalRef(name, t);
        static MExpr Param(string name, AirType t) => new AirParamRef(name, t);
        static MExpr TypeRef(AirType t) => new AirTypeRef(t);
        static MExpr Const(object? v) => v == null ? new AirNull() : AirConst.Of(v);
        static MExpr TypeOf(AirType t) => new AirTypeOf(t);
        static MExpr Member(MExpr target, string name, AirType t) => new MMember { Target = target, Name = name, Type = t };
        static MExpr Call(MExpr? target, string name, AirType ret, params MExpr[] args) => new MCall { Target = target, Name = name, Args = args.ToList(), Type = ret };
        static MExpr New(AirType t, params MExpr[] args) => new MNew { TypeCs = t.ToCs(), Args = args.ToList(), Type = t };
        static MExpr Cast(AirType t, MExpr e) => new MCast { TypeCs = t.ToCs(), E = e, Type = t };
        static MExpr Is(MExpr e, AirType t) => new MIs { E = e, TypeCs = t.ToCs(), Type = Bool };
        /// <summary>`e as T`: `e is T ? (T)e : null`.</summary>
        static MExpr As(MExpr e, AirType t) => new MCond { C = Is(e, t), A = Cast(t, e), B = Const(null), Type = t with { Nullable = true } };
        static MExpr Not(MExpr e) => new MUnary { Op = AirUnOp.Not, E = e, Type = Bool };
        static MExpr Bin(AirBinOp op, MExpr l, MExpr r, bool isChecked = true) =>
            new MBinary { Op = op, L = l, R = r, Checked = isChecked, Type = op is AirBinOp.Add or AirBinOp.Mul ? l.Type : Bool };
        static MStmt Assign(MExpr target, MExpr value) => new MAssign { Target = target, Value = value };
        static MStmt Append(MExpr builder, MExpr value) => new MExprStmt { E = Call(builder, "Append", builder.Type!, value) };
    }
}
