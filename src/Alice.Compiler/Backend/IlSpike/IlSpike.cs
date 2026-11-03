// THROWAWAY SPIKE (backend plan D1.1): see README.md in this folder. Not production code.
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Alice.Compiler.Lowering;
using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Backend.IlSpike;

/// <summary>Result of one spike compile: the PE image (null when a node was unsupported) and the census.</summary>
public sealed class SpikeResult
{
    public byte[]? Image;
    public string? Error;
    /// <summary>Every MIR node kind seen in the lowered bodies (MCode split by leaf shape), with counts.</summary>
    public readonly Dictionary<string, int> Nodes = new();
    /// <summary>Per method: the first unsupported kind (methods blocked by it).</summary>
    public readonly Dictionary<string, int> Blockers = new();
    /// <summary>Blocker kind + detail (unresolved names, text leaves, helper kinds).</summary>
    public readonly Dictionary<string, int> Details = new();
    public int Methods, MethodsOk;
    /// <summary>Methods rebuilt with long branches after the PersistedAssemblyBuilder short-branch corruption.</summary>
    public readonly HashSet<string> Corrupted = new();
}

/// <summary>
/// D1.1 spike: post-optimizer MIR of static methods -> IL through PersistedAssemblyBuilder. Lowers with
/// <c>Sequential = true</c> (forks lower to serial code, ADR 0024) because fork helpers are C# text
/// (Lowerer.Extra) and need closures; see README.
/// </summary>
public static class IlSpikeCompiler
{
    public static SpikeResult Compile(IReadOnlyList<SourceFile> files, string name, SpikeOptions opt, bool censusOnly = false, bool sequential = true)
    {
        var res = new SpikeResult();
        var asts = files.Select(f => Parser.ParseFile(f.Text, f.Path)).ToList();
        var binder = new Semantics.Binder(asts, new ClrTypes(null));
        binder.Bind();
        var lowerer = new Lowerer(binder) { Sequential = sequential };

        var ab = new PersistedAssemblyBuilder(new AssemblyName(name), typeof(object).Assembly);
        var mod = ab.DefineDynamicModule(name);
        mod.SetCustomAttribute(new CustomAttributeBuilder(typeof(System.Runtime.CompilerServices.SkipLocalsInitAttribute).GetConstructor(Type.EmptyTypes)!, []));
        var r = new ClrResolve();
        var program = mod.DefineType("Program", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class, typeof(object));

        var errors = new List<string>();
        void Block(string where, string kind, string? detail = null)
        {
            if (detail != null) { string k = kind + " `" + detail + "`"; res.Details[k] = res.Details.GetValueOrDefault(k) + 1; }
            res.Blockers[kind] = res.Blockers.GetValueOrDefault(kind) + 1;
            errors.Add($"{where}: {kind}");
        }

        // 1. Alice value types: fields + positional constructor
        var structs = new List<(TypeSym Sym, TypeBuilder B)>();
        foreach (var ast in asts)
            foreach (var td in AllTypes(ast.Items))
            {
                if (td.Sym is not TypeSym ts) continue;
                if (ts.Kind is not ("struct" or "record struct") || ts.TypeParams.Count > 0) { Block(td.Name, "TypeDecl:" + ts.Kind + (ts.TypeParams.Count > 0 ? "<generic>" : "")); continue; }
                var tb = mod.DefineType(ts.Type.Cs, TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout, typeof(ValueType));
                r.Alice[ts.Type.Cs] = tb;
                structs.Add((ts, tb));
            }
        foreach (var (ts, tb) in structs)
        {
            var order = ts.FieldOrder.Count > 0 ? ts.FieldOrder : ts.Fields.Values.Where(f => !f.IsStatic && f.Kind != "const").Select(f => f.Name).ToList();
            var fields = new Dictionary<string, FieldInfo>();
            foreach (var fname in order)
            {
                var fs = ts.Fields[fname];
                fields[fs.CsName] = tb.DefineField(fs.CsName, r.Type(fs.Type.Cs), FieldAttributes.Public);
            }
            r.AliceFields[tb] = fields;
            // record struct: positional; `struct` with `initialize(@a, @b...)`: auto params in field order
            var ctorSym = ts.Methods.GetValueOrDefault("initialize")?.FirstOrDefault();
            if (ctorSym != null && (!ctorSym.Params.All(p => p.Auto) || ctorSym.Params.Count != fields.Count || ctorSym.Decl is { Body.Count: > 0 }))
            {
                Block(ts.Name, "Ctor:body");
                continue;
            }
            var cb = tb.DefineConstructor(MethodAttributes.Public, CallingConventions.Standard, fields.Values.Select(f => f.FieldType).ToArray());
            var cil = cb.GetILGenerator();
            int i = 1;
            foreach (var f in fields.Values)
            {
                cil.Emit(OpCodes.Ldarg_0);
                cil.Ldarg(i++);
                cil.Emit(OpCodes.Stfld, f);
            }
            cil.Emit(OpCodes.Ret);
            r.AliceCtors[tb] = cb;
        }

        // 2. lower every top-level method (owner Program) and the main statements
        var methods = new Dictionary<string, List<SpikeMethod>>();
        SpikeMethod? main = null;
        foreach (var ast in asts)
        {
            lowerer.File = ast.Path;
            foreach (var it in ast.Items)
            {
                if (it is not MethodDecl d || d.Sym is not MethodSym m) continue;
                res.Methods++;
                string why = Skip(d, m);
                if (why != "") { Block(m.CsName, why); continue; }
                List<MStmt> body;
                try
                {
                    lowerer.Type = binder.Program;
                    lowerer.Method = m;
                    lowerer.Forked = false;
                    bool returns = !m.Ret.IsVoid && !m.IsCtor && !m.IsIterator;
                    body = lowerer.LowerBody(d.Body, returns ? Tail.Return : Tail.None, d.HoistedLocals, d.Line);
                    Optimizer.Pipeline.Run(body);
                    Census(body, res.Nodes);
                }
                catch (Exception e) when (e is not SpikeUnsupported) { Block(m.CsName, "Lowering:" + e.GetType().Name); continue; }
                if (lowerer.Extra.Count > 0) { Block(m.CsName, "Lowerer.Extra(C# text helper)", ExtraKinds(lowerer.Extra)); lowerer.Extra.Clear(); continue; }
                if (lowerer.FlushSpecializations().Length > 0) { Block(m.CsName, "Specialization(C# text)"); continue; }
                try
                {
                    var sm = new SpikeMethod { Name = m.CsName, Ret = r.Type(m.Ret.Cs), Body = body, Params = m.Params.Select(p => (p.CsName, r.Type(p.Type.Cs))).ToList() };
                    if (!methods.TryGetValue(sm.Name, out var list)) methods[sm.Name] = list = new();
                    list.Add(sm);
                }
                catch (SpikeUnsupported u) { Block(m.CsName, u.Kind, u.Message); }
            }
            // members of types: lowered for the census only (instance/class code is D1.4-D1.6 territory)
            foreach (var td in AllTypes(ast.Items))
            {
                if (td.Sym is not TypeSym ts) continue;
                foreach (var d in td.Members.OfType<MethodDecl>())
                {
                    if (d.Sym is not MethodSym m) continue;
                    res.Methods++;
                    string why = Skip(d, m);
                    if (why == "")
                        try
                        {
                            lowerer.Type = ts;
                            lowerer.Method = m;
                            lowerer.Forked = false;
                            bool returns = !m.Ret.IsVoid && !m.IsCtor && !m.IsIterator;
                            var body = lowerer.LowerBody(d.Body, returns ? Tail.Return : Tail.None, d.HoistedLocals, d.Line);
                            Optimizer.Pipeline.Run(body);
                            Census(body, res.Nodes);
                            lowerer.Extra.Clear();
                            lowerer.FlushSpecializations();
                            why = (m.IsCtor ? "Ctor" : m.IsStatic ? "TypeMember:static" : "TypeMember:instance") + "(" + ts.Kind + ")";
                        }
                        catch (Exception e) when (e is not SpikeUnsupported) { why = "Lowering:" + e.GetType().Name; }
                    if (!(ts.Kind is "struct" or "record struct" && m.IsCtor && d.Body.Count == 0)) Block(ts.Name + "." + m.CsName, why);
                }
            }
            var stmts = ast.Items.OfType<StmtItem>().Select(s => s.Stmt).ToList();
            if (stmts.Count > 0)
            {
                lowerer.Type = binder.Program;
                lowerer.Method = null;
                try
                {
                    var body = lowerer.LowerBody(stmts, Tail.None, ast.HoistedLocals, stmts[0].Line);
                    Optimizer.Pipeline.Run(body);
                    Census(body, res.Nodes);
                    res.Methods++;
                    if (lowerer.Extra.Count > 0) { Block("Main", "Lowerer.Extra(C# text helper)", ExtraKinds(lowerer.Extra)); lowerer.Extra.Clear(); }
                    else if (lowerer.FlushSpecializations().Length > 0) Block("Main", "Specialization(C# text)");
                    else main = new SpikeMethod { Name = "Main", IsMain = true, Body = body, Params = [("ARGV", typeof(string[]))] };
                }
                catch (Exception e) when (e is not SpikeUnsupported) { Block("Main", "Lowering:" + e.GetType().Name); }
            }
        }
        foreach (var (ts, tb) in structs) { try { tb.CreateType(); } catch (Exception) { } }

        // 3. signatures, then bodies
        foreach (var sm in methods.Values.SelectMany(l => l).Append(main).OfType<SpikeMethod>())
        {
            sm.Builder = program.DefineMethod(sm.Name, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, sm.Ret, sm.Params.Select(p => p.Type).ToArray());
            for (int i = 0; i < sm.Params.Count; i++) sm.Builder.DefineParameter(i + 1, ParameterAttributes.None, sm.Params[i].Name);
            sm.Builder.InitLocals = false;   // [module: SkipLocalsInit], as the C# backend emits
            var impl = (opt.Hot.Contains(sm.Name) ? MethodImplAttributes.AggressiveOptimization : 0)
                | (opt.Inline.Contains(sm.Name) ? MethodImplAttributes.AggressiveInlining : 0) | (opt.NoInline.Contains(sm.Name) ? MethodImplAttributes.NoInlining : 0);
            if (impl != 0) sm.Builder.SetImplementationFlags(impl);
        }
        // pass 1 into scratch methods (branch offsets), pass 2 for real with short branches where they fit
        var scratch = mod.DefineType("<IlSpikeScratch>", TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class, typeof(object));
        int sn = 0;
        foreach (var sm in methods.Values.SelectMany(l => l).Append(main).OfType<SpikeMethod>())
        {
            try
            {
                var sb = scratch.DefineMethod("s" + sn++, MethodAttributes.Assembly | MethodAttributes.Static, sm.Ret, sm.Params.Select(p => p.Type).ToArray());
                var p1 = new IlBody(r, methods, sm, opt, sb.GetILGenerator());
                p1.Emit();
                var p2 = new IlBody(r, methods, sm, opt, sm.Builder.GetILGenerator(), Environment.GetEnvironmentVariable("ILSPIKE_NOSHORT") == "1" || opt.LongBranches.Contains(sm.Name) ? null : p1.ShortPlan());
                p2.Emit();
                if (Environment.GetEnvironmentVariable("ILSPIKE_DEBUG") == sm.Name) Console.Error.WriteLine("pass1 " + p1.Debug() + " | pass2 " + p2.Debug());
                res.MethodsOk++;
            }
            catch (SpikeUnsupported u) { Block(sm.Name, u.Kind, u.Message); }
        }
        try { scratch.CreateType(); } catch (Exception) { }
        if (censusOnly) return res;
        if (errors.Count > 0) { res.Error = string.Join("\n", errors); return res; }
        if (main == null) { res.Error = "no main statements"; return res; }

        program.CreateType();
        var md = ab.GenerateMetadata(out BlobBuilder ilStream, out BlobBuilder fieldData);
        var pe = new ManagedPEBuilder(
            new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage),
            new MetadataRootBuilder(md), ilStream, fieldData,
            entryPoint: MetadataTokens.MethodDefinitionHandle(main.Builder.MetadataToken));
        var blob = new BlobBuilder();
        pe.Serialize(blob);
        res.Image = blob.ToArray();
        // PersistedAssemblyBuilder (.NET 10) sometimes drops a byte after a short branch (README: "Framework bug"):
        // verify the serialized IL and rebuild the affected methods with long branches only
        var bad = CorruptMethods(res.Image);
        if (bad.Count > 0 && bad.Except(opt.LongBranches).Any())
        {
            opt.LongBranches.UnionWith(bad);
            var again = Compile(files, name, opt, censusOnly, sequential);
            again.Corrupted.UnionWith(bad);
            return again;
        }
        if (bad.Count > 0) { res.Image = null; res.Error = "corrupt IL even with long branches: " + string.Join(",", bad); }
        return res;
    }

    static readonly Dictionary<short, OpCode> Ops = typeof(OpCodes).GetFields().Select(f => (OpCode)f.GetValue(null)!).ToDictionary(o => o.Value);

    /// <summary>Methods whose serialized IL does not decode cleanly: a branch target off an instruction boundary,
    /// or the last instruction not ending at the body end.</summary>
    public static HashSet<string> CorruptMethods(byte[] image)
    {
        var bad = new HashSet<string>();
        using var pe = new PEReader(new MemoryStream(image));
        var md = pe.GetMetadataReader();
        foreach (var h in md.MethodDefinitions)
        {
            var m = md.GetMethodDefinition(h);
            if (m.RelativeVirtualAddress == 0) continue;
            var il = pe.GetMethodBody(m.RelativeVirtualAddress).GetILBytes()!;
            var starts = new HashSet<int>();
            var targets = new List<int>();
            int i = 0;
            bool ok = true;
            while (i < il.Length && ok)
            {
                starts.Add(i);
                short v = il[i] == 0xFE && i + 1 < il.Length ? unchecked((short)(0xFE00 | il[i + 1])) : il[i];
                i += il[i] == 0xFE ? 2 : 1;
                if (!Ops.TryGetValue(v, out var op)) { ok = false; break; }
                switch (op.OperandType)
                {
                    case OperandType.InlineNone: break;
                    case OperandType.ShortInlineBrTarget: targets.Add(i + 1 + (sbyte)il[i]); i += 1; break;
                    case OperandType.InlineBrTarget: targets.Add(i + 4 + BitConverter.ToInt32(il, i)); i += 4; break;
                    case OperandType.ShortInlineI or OperandType.ShortInlineVar: i += 1; break;
                    case OperandType.InlineVar: i += 2; break;
                    case OperandType.InlineI8 or OperandType.InlineR: i += 8; break;
                    case OperandType.InlineSwitch: i += 4 + 4 * BitConverter.ToInt32(il, i); break;
                    default: i += 4; break;
                }
            }
            if (!ok || i != il.Length || targets.Any(t => !starts.Contains(t))) bad.Add(md.GetString(m.Name));
        }
        return bad;
    }

    /// <summary>The helper families in Lowerer.Extra (`__fork_batch_1` -> `__fork`), sorted.</summary>
    static string ExtraKinds(List<string> extra) => string.Join("+", extra.Select(x =>
        Alice.Core.Text.Regex.Cached(@"(__[a-z]+|[A-Za-z]+__[a-z]+)").Match(x) is { } mm ? mm[1]! : FirstLine(x)).Distinct().Order());

    static string FirstLine(string x)
    {
        var l = x.Trim().Split('\n')[0];
        return l.Length == 0 ? "<empty>" : l[..Math.Min(60, l.Length)];
    }

    static string Skip(MethodDecl d, MethodSym m)
    {
        if (!d.HasBody || d.IsPartial || d.IsAbstract) return "Method:no-body";
        if (CEmitter.IsNative(d)) return "Method:@[Native]";
        if (m.HasInferredGenerics || m.TypeParams.Count > 0) return "Method:generic";
        if (m.IsAsync) return "Method:async";
        if (m.IsIterator || m.UsesYield) return "Method:iterator";
        if (m.Params.Any(p => p.Splat || p.Block || p.HasDefault || p.Mod != "")) return "Method:param-kind";
        if (m.Ret.IsUnknown || m.Params.Any(p => p.Type.IsUnknown)) return "Method:untyped";
        return "";
    }

    static IEnumerable<TypeDecl> AllTypes(IEnumerable<Item> items)
    {
        foreach (var it in items)
        {
            if (it is NamespaceItem ns) foreach (var t in AllTypes(ns.Items)) yield return t;
            if (it is TypeDecl td)
            {
                yield return td;
                foreach (var t in AllTypes(td.Members)) yield return t;
            }
        }
    }

    // ---------------- census ----------------

    /// <summary>Counts node kinds by reflection over the public fields (works for node kinds added by other workers).</summary>
    public static void Census(List<MStmt> body, Dictionary<string, int> counts)
    {
        void Add(string k) => counts[k] = counts.GetValueOrDefault(k) + 1;
        void Visit(object? o)
        {
            if (o is null or string) return;
            if (o is MExpr { Type: not null }) Add("(MExpr.Type set)");
            switch (o)
            {
                case MCode c: Add("MCode:" + Leaf(c.Text)); return;
                case MCall c when c.Target == null && c.Name is "unchecked" or "checked": Add($"MCall:{c.Name}()"); break;
                case MBlock b: Add(b.Lock != null ? "MBlock:lock" : b.Unchecked ? "MBlock:unchecked" : b.Unsafe ? "MBlock:unsafe" : "MBlock"); break;
                case MExpr or MStmt or MCatch: Add(o.GetType().Name); break;
                case System.Collections.IEnumerable list: foreach (var x in list) Visit(x); return;
                default: return;
            }
            foreach (var f in o.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
                if (f.FieldType != typeof(string) && f.FieldType != typeof(AirType) && f.FieldType != typeof(Span) && f.FieldType != typeof(Span?) && !f.FieldType.IsPrimitive && !f.FieldType.IsEnum)
                    Visit(f.GetValue(o));
        }
        foreach (var s in body) Visit(s);
    }

    /// <summary>What an MCode leaf holds: the shape the spike (and AIR) must give a typed node.</summary>
    static string Leaf(string t)
    {
        t = t.Trim();
        if (ClrResolve.TryLiteral(t, out _, out _)) return "literal";
        if (t is "default!" or "default") return "default";
        if (t == "this" || t == "base") return "this/base";
        if (Alice.Core.Text.Regex.Cached(@"\A@?[A-Za-z_][A-Za-z0-9_]*\Z").IsMatch(t)) return "name";
        if (Alice.Core.Text.Regex.Cached(@"\A(?:global::)?[A-Za-z_][A-Za-z0-9_.]*(?:<[^()]*>)?\Z").IsMatch(t)) return "type/static path";
        if (t.StartsWith("$\"") || t.StartsWith("@\"")) return "C# string form";
        if (t.Contains('(')) return "C# call/expr text";
        return "other text";
    }
}
