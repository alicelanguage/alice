using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Lowering;

/// <summary>
/// Typed JSON mapping (ADR 0036, C2.5). A <see cref="JsonMapCall"/> lowers to <c>JsonMap.Parse&lt;T&gt;(s, R)</c> or
/// <c>JsonMap.ToJson&lt;T&gt;(v, W)</c>, where R and W are typed helper methods (<see cref="AirHelperDecl"/>, through
/// <c>AddHelper</c>) of the enclosing type, built once per type and enclosing type, so both backends emit them:
/// <c>JsonIterator</c> readers that match field names with <c>JsonMap.Is</c> and skip unknown fields, <c>JsonWriter</c>
/// writers. No reflection. Shapes: records and variants by their positional fields, classes by public properties
/// (built through the `initialize` whose parameters are all properties, else a parameterless one, then property
/// assignments), sum types as <c>{"type":"Variant", ...fields}</c>, plain enums by member name, nil-able values as
/// null, Array / List / Hash(String, V). A missing non-nil-able field raises <c>JsonParseException</c>; nil-able
/// fields default to nil.
/// </summary>
public sealed partial class Lowerer
{
    const string JM = "global::Alice.Core.Json.JsonMap";
    static readonly AirType JIter = new AirType.ClrRef("global::Alice.Core.Json.JsonIterator", typeof(Alice.Core.Json.JsonIterator), []);
    static readonly AirType JWriter = new AirType.ClrRef("global::Alice.Core.Json.JsonWriter", typeof(Alice.Core.Json.JsonWriter), []);
    static readonly AirType JSpan = new AirType.ClrRef("global::System.ReadOnlySpan<byte>", typeof(ReadOnlySpan<byte>), []);
    const string SpanCs = "global::System.ReadOnlySpan<byte>";

    readonly Dictionary<string, string> jsonIds = new();
    readonly HashSet<(TypeSym?, string, string)> jsonDone = new();
    readonly Queue<AType> jsonQueue = new();
    string jsonSuffix = "";
    int jsonN;

    MExpr JsonCall(CallExpr c)
    {
        var j = (JsonMapCall)c.Sym!;
        var arg = Expr(j.Arg);
        JsonEnsure(j.Type);
        var map = JMap();
        var at = AirOf(j.Type, j.Type.Cs);
        if (j.Read)
            return At(new MCall { Target = map, Name = $"Parse<{j.Type.Cs}>", Args = { arg, new AirMethodGroup(null, JName(j.Type, 'R')) }, Type = at }, c);
        var call = new MCall { Target = map, Name = $"ToJson<{j.Type.Cs}>", Args = { arg, new AirMethodGroup(null, JName(j.Type, 'W')) }, Type = new AirType.Prim(PrimKind.String) };
        if (j.Indented) call.Args.Add(AirConst.Bool(true));
        return At(call, c);
    }

    string JsonId(AType t)
    {
        if (jsonIds.TryGetValue(t.Cs, out var id)) return id;
        var sb = new System.Text.StringBuilder().Append(jsonN++).Append('_');
        foreach (char ch in t.Cs.Replace("global::", "")) sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        return jsonIds[t.Cs] = sb.ToString();
    }

    /// <summary>The helper name of <paramref name="t"/> in the current scope (R reader, W writer).</summary>
    string JName(AType t, char rw) => $"__json_{rw}{JsonId(t)}{jsonSuffix}";

    /// <summary>A helper that needs <paramref name="t"/>'s pair: its name, and the pair queued for this scope.</summary>
    string JUse(AType t, char rw)
    {
        jsonQueue.Enqueue(t);
        return JName(t, rw);
    }

    /// <summary>Adds the readers/writers of <paramref name="root"/> and everything it contains to the enclosing type
    /// (a specialization clone gets its own names: its text lands in the caller's type).</summary>
    void JsonEnsure(AType root)
    {
        jsonSuffix = specializing != null ? "_" + specializing.Name : "";
        jsonQueue.Enqueue(root);
        while (jsonQueue.Count > 0)
        {
            var t = jsonQueue.Dequeue();
            if (jsonDone.Add((Type, jsonSuffix, t.Cs))) JsonEmit(t);
        }
    }

    // ---- MIR pieces ----

    static AirTypeRef JMap() => new(new AirType.ClrRef(JM, typeof(Alice.Core.Json.JsonMap), []));
    static AirParamRef JR() => new("r", JIter);
    static AirParamRef JW() => new("w", JWriter);
    static MArg RefR() => new() { Prefix = "ref ", E = JR() };
    static MArg RefW() => new() { Prefix = "ref ", E = JW() };
    static MCall On(MExpr target, string name, params MExpr[] args) => new() { Target = target, Name = name, Args = args.ToList() };
    static MCall Map(string name, params MExpr[] args) => On(JMap(), name, args);

    /// <summary>The raw name <paramref name="raw"/> equals <paramref name="name"/>: <c>IsAscii</c> (length first, then a
    /// vectorized compare) for ASCII names, <c>Is</c> (UTF-8 aware) otherwise.</summary>
    static MCall JIs(MExpr raw, string name) => Map(name.All(ch => ch < 0x80) ? "IsAscii" : "Is", raw, S(name));
    static MCall Self(string name, AirType? ret, params MExpr[] args) => new() { Name = name, Args = args.ToList(), Type = ret };
    static MExprStmt Do(MExpr e) => new() { E = e };
    static AirConst S(string s) => AirConst.Of(s);
    static MCast Cast(string cs, MExpr e) => new() { TypeCs = cs, E = e };
    static MBinary Bin(AirBinOp op, MExpr l, MExpr r) => new() { Op = op, L = l, R = r };

    void JHelper(string name, AirType ret, List<AirParamDecl> ps, List<MStmt> body)
    {
        Optimizer.Pipeline.Run(body);
        AddHelper(new AirHelperDecl
        {
            Method = new AirMethodDecl { Name = name, Vis = AirVis.Internal, Flags = AirMethodFlags.Static, Params = ps, Ret = ret, Body = body },
        });
    }

    void JPair(AType t, List<MStmt> read, List<MStmt> write)
    {
        var at = AirOf(t, t.Cs);
        JHelper(JName(t, 'R'), at, [new("r", new AirType.ByRef(JIter, RefKind.Ref))], read);
        JHelper(JName(t, 'W'), AirLeaf.Void, [new("w", new AirType.ByRef(JWriter, RefKind.Ref)), new("v", at)], write);
    }

    /// <summary>`if (!JsonMap.NextProperty(ref r, out var n)) break;`: the name local is declared by the call, in the
    /// loop body (a span tied to the iterator), before the statements that read it.</summary>
    static MIf PropertyOrBreak(AirLocalRef name) => new()
    {
        Cond = new MUnary { Op = AirUnOp.Not, E = Map("NextProperty", RefR(), new MArg { Prefix = "out var ", E = name }) },
        Then = { new MBreak() },
    };

    static List<MStmt> Ret(MExpr e) => [new MReturn { Value = e }];

    static bool JsonIsValue(AType t) => t.Kind is "prim" or "struct" or "enum" || t.Sym?.Kind is "struct" or "record struct" or "enum";

    static Type? JsonGeneric(AType t) => t.OpenClr ?? (t.Clr is { IsGenericType: true } g ? g.GetGenericTypeDefinition() : null);

    /// <summary>`if (v == null) { w.WriteNull(); return; }`</summary>
    static MIf NullGuard(AType t) => new()
    {
        Cond = Bin(AirBinOp.Eq, new AirParamRef("v", null), new AirNull()),
        Then = { Do(Map("WriteNull", RefW())), new MReturn() },
    };

    void JsonEmit(AType t)
    {
        var at = AirOf(t, t.Cs);
        var v = new AirParamRef("v", at);
        if (t.Nullable)
        {
            var u = Binder.JsonUnderlying(t);
            var ut = AirOf(u, u.Cs);
            string ur = JUse(u, 'R'), uw = JName(u, 'W');
            MExpr inner = Self(ur, ut, RefR());
            JPair(t,
                [new MIf { Cond = Map("TryNull", RefR()), Then = { new MReturn { Value = JsonIsValue(u) ? new AirDefault(at) : new AirNull() } } },
                 new MReturn { Value = JsonIsValue(u) ? Cast(t.Cs, inner) : inner }],
                [JsonIsValue(u)
                    ? new MIf { Cond = new MMember { Target = v, Name = "HasValue" }, Then = { Do(Self(uw, AirLeaf.Void, RefW(), new MMember { Target = v, Name = "Value" })) }, Else = [Do(Map("WriteNull", RefW()))] }
                    : new MIf { Cond = Bin(AirBinOp.Eq, v, new AirNull()), Then = { Do(Map("WriteNull", RefW())) }, Else = [Do(Self(uw, AirLeaf.Void, RefW(), v))] }]);
            return;
        }
        switch (t.Cs)
        {
            case "bool": JPair(t, Ret(Map("ReadBoolean", RefR())), [Do(Map("WriteBoolean", RefW(), v))]); return;
            case "int": JPair(t, Ret(Map("ReadInt32", RefR())), [Do(Map("WriteInt64", RefW(), Cast("long", v)))]); return;
            case "long": JPair(t, Ret(Map("ReadInt64", RefR())), [Do(Map("WriteInt64", RefW(), v))]); return;
            case "uint": JPair(t, Ret(Map("ReadUInt32", RefR())), [Do(Map("WriteUInt64", RefW(), Cast("ulong", v)))]); return;
            case "ulong": JPair(t, Ret(Map("ReadUInt64", RefR())), [Do(Map("WriteUInt64", RefW(), v))]); return;
            case "short": JPair(t, Ret(Map("ReadInt16", RefR())), [Do(Map("WriteInt64", RefW(), Cast("long", v)))]); return;
            case "ushort": JPair(t, Ret(Map("ReadUInt16", RefR())), [Do(Map("WriteInt64", RefW(), Cast("long", v)))]); return;
            case "sbyte": JPair(t, Ret(Map("ReadSByte", RefR())), [Do(Map("WriteInt64", RefW(), Cast("long", v)))]); return;
            case "byte": JPair(t, Ret(Map("ReadByte", RefR())), [Do(Map("WriteInt64", RefW(), Cast("long", v)))]); return;
            case "double": JPair(t, Ret(Map("ReadDouble", RefR())), [Do(Map("WriteDouble", RefW(), v))]); return;
            case "float": JPair(t, Ret(Map("ReadSingle", RefR())), [Do(Map("WriteDouble", RefW(), Cast("double", v)))]); return;
            case "string": JPair(t, Ret(Map("ReadString", RefR())), [Do(Map("WriteString", RefW(), v))]); return;
        }
        if (t.Kind == "array" && t.Elem is { } e) { JsonArray(t, e); return; }
        if (JsonGeneric(t) is { } def && def == typeof(List<>)) { JsonList(t, t.Args[0]); return; }
        if (JsonGeneric(t) is { } hdef && hdef == typeof(Dictionary<,>)) { JsonHash(t, t.Args[1]); return; }
        var s = t.Sym ?? throw new CompileException($"internal: no JSON mapping for {t.Cs}");
        if (s.Kind == "enum")
        {
            JPair(t, Ret(Map($"ReadEnum<{t.Cs}>", RefR())), [Do(Map($"WriteEnum<{t.Cs}>", RefW(), v))]);
            return;
        }
        if (s.Kind == "adt") { JsonAdt(t, s); return; }
        JsonObject(t, s);
    }

    // collections: the loops are written out (direct calls the JIT can inline, no delegate per element)
    void JsonArray(AType t, AType e)
    {
        var et = AirOf(e, e.Cs);
        var at = AirOf(t, t.Cs);
        var a = new AirLocalRef("a", at);
        var n = new AirLocalRef("n", AirLeaf.Int);
        var i = new AirLocalRef("i", AirLeaf.Int);
        var v = new AirParamRef("v", at);
        string er = JUse(e, 'R'), ew = JName(e, 'W');
        JPair(t,
            [new MDecl { Name = "a", TypeCs = t.Cs, Init = Map($"NewArray<{e.Cs}>") },
             new MDecl { Name = "n", TypeCs = "int", Init = AirConst.Int(0) },
             Do(Map("StartArray", RefR())),
             new MWhile
             {
                 Cond = Map("NextElement", RefR()),
                 Body =
                 {
                     new MIf { Cond = Bin(AirBinOp.Eq, n, new MMember { Target = a, Name = "Length" }), Then = { new MAssign { Target = a, Value = Map($"Grow<{e.Cs}>", a) } } },
                     new MAssign { Target = new MIndex { Target = a, Args = { n } }, Value = Self(er, et, RefR()) },
                     new MAssign { Target = n, Value = Bin(AirBinOp.Add, n, AirConst.Int(1)) },
                 },
             },
             new MReturn { Value = Map($"Trim<{e.Cs}>", a, n) }],
            [NullGuard(t),
             Do(Map("StartArray", RefW())),
             new MFor { Var = "i", VarType = "int", Start = AirConst.Int(0), Cmp = "<", End = new MMember { Target = v, Name = "Length" }, Step = AirForStep.Inc(),
                 Body = { Do(Self(ew, AirLeaf.Void, RefW(), new MIndex { Target = v, Args = { i } })) } },
             Do(Map("EndArray", RefW()))]);
    }

    void JsonList(AType t, AType e)
    {
        var et = AirOf(e, e.Cs);
        var at = AirOf(t, t.Cs);
        var l = new AirLocalRef("l", at);
        var i = new AirLocalRef("i", AirLeaf.Int);
        var v = new AirParamRef("v", at);
        string er = JUse(e, 'R'), ew = JName(e, 'W');
        JPair(t,
            [new MDecl { Name = "l", TypeCs = t.Cs, Init = new MNew { TypeCs = t.Cs } },
             Do(Map("StartArray", RefR())),
             new MWhile { Cond = Map("NextElement", RefR()), Body = { Do(On(l, "Add", Self(er, et, RefR()))) } },
             new MReturn { Value = l }],
            [NullGuard(t),
             Do(Map("StartArray", RefW())),
             new MFor { Var = "i", VarType = "int", Start = AirConst.Int(0), Cmp = "<", End = new MMember { Target = v, Name = "Count" }, Step = AirForStep.Inc(),
                 Body = { Do(Self(ew, AirLeaf.Void, RefW(), new MIndex { Target = v, Args = { i } })) } },
             Do(Map("EndArray", RefW()))]);
    }

    void JsonHash(AType t, AType e)
    {
        var et = AirOf(e, e.Cs);
        var at = AirOf(t, t.Cs);
        var d = new AirLocalRef("d", at);
        var k = new AirLocalRef("k", JSpan);
        var kv = new AirLocalRef("kv");
        string er = JUse(e, 'R'), ew = JName(e, 'W');
        JPair(t,
            [new MDecl { Name = "d", TypeCs = t.Cs, Init = new MNew { TypeCs = t.Cs } },
             Do(Map("StartObject", RefR())),
             new MWhile
             {
                 Cond = AirConst.Bool(true),
                 Body = { PropertyOrBreak(k), new MAssign { Target = new MIndex { Target = d, Args = { Map("Key", k) } }, Value = Self(er, et, RefR()) } },
             },
             new MReturn { Value = d }],
            [NullGuard(t),
             Do(Map("StartObject", RefW())),
             new MForeach
             {
                 Vars = { "kv" },
                 Coll = new AirParamRef("v", at),
                 Body =
                 {
                     Do(Map("WriteName", RefW(), new MMember { Target = kv, Name = "Key" })),
                     Do(Self(ew, AirLeaf.Void, RefW(), new MMember { Target = kv, Name = "Value" })),
                 },
             },
             Do(Map("EndObject", RefW()))]);
    }

    /// <summary>A sum type: the variant named by the `type` property (it may come after the fields).</summary>
    void JsonAdt(AType t, TypeSym s)
    {
        var at = AirOf(t, t.Cs);
        var tag = new AirLocalRef("tag", JSpan);
        var v = new AirParamRef("v", at);
        var read = new List<MStmt> { new MDecl { Name = "tag", TypeCs = SpanCs, Init = Map("PeekTag", RefR(), S("type")) } };
        var write = new List<MStmt>();
        foreach (var vs in s.Variants)
        {
            var vt = AirOf(vs.Type, vs.Type.Cs);
            read.Add(new MIf { Cond = JIs(tag, vs.Name), Then = { new MReturn { Value = Self(JUse(vs.Type, 'R'), vt, RefR()) } } });
            write.Add(new MIf
            {
                Cond = new MIs { E = v, TypeCs = vs.Type.Cs },
                Then = { Do(Self(JName(vs.Type, 'W'), AirLeaf.Void, RefW(), Cast(vs.Type.Cs, v))), new MReturn() },
            });
        }
        read.Add(new MThrow { Value = new MNew { TypeCs = "global::Alice.Core.Json.JsonParseException", Args = { S($"no variant of {s.Name} in \"type\""), Map("Offset", RefR()) } } });
        write.Add(Do(Map("WriteNull", RefW())));
        JPair(t, read, write);
    }

    void JsonObject(AType t, TypeSym s)
    {
        var at = AirOf(t, t.Cs);
        var v = new AirParamRef("v", at);
        var fields = Binder.JsonFields(s);
        var fts = fields.Select(f => AirOf(f.Type, f.Type.Cs)).ToList();

        // writer
        var write = new List<MStmt>();
        if (!JsonIsValue(t)) write.Add(NullGuard(t));
        write.Add(Do(Map("StartObject", RefW())));
        if (s.Kind == "variant") write.Add(Do(Map("WriteTag", RefW(), S("type"), S(s.Name))));
        for (int i = 0; i < fields.Count; i++)
        {
            write.Add(Do(Map("WriteName", RefW(), S(fields[i].Name))));
            write.Add(Do(Self(JUse(fields[i].Type, 'W'), AirLeaf.Void, RefW(), new MMember { Target = v, Name = fields[i].CsName, Type = fts[i] })));
        }
        write.Add(Do(Map("EndObject", RefW())));

        // construction: positional fields, else the matching `initialize`, else a parameterless constructor; then
        // the other settable properties
        MExpr F(int i) => new AirLocalRef("f" + i, fts[i]);
        var used = new bool[fields.Count];
        var ctorArgs = new List<MExpr>();
        var assigns = new List<int>();
        bool unit = s.Kind == "variant" && fields.Count == 0;
        if (s.FieldOrder.Count > 0)
        {
            Array.Fill(used, true);
            for (int i = 0; i < fields.Count; i++) ctorArgs.Add(F(i));
        }
        else if (!unit)
        {
            var names = fields.Select(f => f.Name).ToList();
            var inits = s.Methods.TryGetValue("initialize", out var ms) ? ms : new List<MethodSym>();
            var pick = inits.Where(m => m.Params.All(p => !p.Splat && !p.Block && names.Contains(p.Name))).OrderByDescending(m => m.Params.Count).FirstOrDefault();
            if (pick == null && inits.Count > 0)
                throw new CompileException($"JSON mapping: {s.Name}.from_json needs an `initialize` whose parameters are all properties of {s.Name}");
            foreach (var p in pick?.Params ?? new List<ParamSym>())
            {
                int k = names.IndexOf(p.Name);
                used[k] = true;
                ctorArgs.Add(F(k));
            }
            for (int i = 0; i < fields.Count; i++)
                if (!used[i] && fields[i].Kind == "property") { used[i] = true; assigns.Add(i); }
        }

        // reader
        var read = new List<MStmt> { new MDecl { Name = "at", TypeCs = "long", Init = Map("Offset", RefR()) } };
        for (int i = 0; i < fields.Count; i++)
        {
            if (!used[i]) continue;
            read.Add(new MDecl { Name = "f" + i, TypeCs = fields[i].Type.Cs, Init = new AirDefault(fts[i], forgiving: !JsonIsValue(fields[i].Type), targetTyped: true) });
            read.Add(new MDecl { Name = "h" + i, TypeCs = "bool", Init = AirConst.Bool(false) });
        }
        read.Add(Do(Map("StartObject", RefR())));
        var n = new AirLocalRef("n", JSpan);
        // the names grouped by UTF-8 length: one compare picks the group (a jump table under IL, SwitchLower), then
        // only same-length names are compared, as the hand-written readers' length switch does
        var len = new AirLocalRef("len", AirLeaf.Int);
        List<MStmt> Skip() => [Do(Map("Skip", RefR()))];
        List<MStmt> chain = Skip();
        var byLen = Enumerable.Range(0, fields.Count).Where(i => used[i])
            .GroupBy(i => System.Text.Encoding.UTF8.GetByteCount(fields[i].Name)).OrderBy(g => g.Key).ToList();
        for (int g = byLen.Count - 1; g >= 0; g--)
        {
            List<MStmt> inner = Skip();
            foreach (int i in byLen[g].Reverse())
                inner = [new MIf
                {
                    Cond = JIs(n, fields[i].Name),
                    Then = { new MAssign { Target = F(i), Value = Self(JUse(fields[i].Type, 'R'), fts[i], RefR()) }, new MAssign { Target = new AirLocalRef("h" + i, AirLeaf.Bool), Value = AirConst.Bool(true) } },
                    Else = inner,
                }];
            var test = new MIf { Cond = Bin(AirBinOp.Eq, len, AirConst.Int(byLen[g].Key)), Else = chain };
            test.Then.AddRange(inner);
            chain = [test];
        }
        var loop = new MWhile { Cond = AirConst.Bool(true), Body = { PropertyOrBreak(n) } };
        if (byLen.Count > 0) loop.Body.Add(new MDecl { Name = "len", TypeCs = "int", Init = new MMember { Target = n, Name = "Length", Type = AirLeaf.Int } });
        loop.Body.AddRange(chain);
        read.Add(loop);
        for (int i = 0; i < fields.Count; i++)
            if (used[i] && !fields[i].Type.Nullable)
                read.Add(new MIf
                {
                    Cond = new MUnary { Op = AirUnOp.Not, E = new AirLocalRef("h" + i, AirLeaf.Bool) },
                    Then = { new MThrow { Value = Map("MissingField", S(fields[i].Name), S(s.Name), new AirLocalRef("at", AirLeaf.Long)) } },
                });
        if (unit) read.Add(new MReturn { Value = new MMember { Target = new AirTypeRef(at), Name = "Value", Type = at } });
        else if (assigns.Count == 0) read.Add(new MReturn { Value = new MNew { TypeCs = t.Cs, Args = ctorArgs, Type = at } });
        else
        {
            read.Add(new MDecl { Name = "o", TypeCs = t.Cs, Init = new MNew { TypeCs = t.Cs, Args = ctorArgs, Type = at } });
            var o = new AirLocalRef("o", at);
            foreach (int i in assigns) read.Add(new MAssign { Target = new MMember { Target = o, Name = fields[i].CsName, Type = fts[i] }, Value = F(i) });
            read.Add(new MReturn { Value = o });
        }
        JPair(t, read, write);
    }
}
