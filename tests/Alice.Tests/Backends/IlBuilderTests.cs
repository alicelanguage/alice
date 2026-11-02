using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using Alice.Compiler.Backend.Il;
using Alice.Compiler.Mir;
using ILVerify;

namespace Alice.Tests.Backends;

/// <summary>D1.2: the SRM IL builder (ADR 0033). Every emitted assembly is checked by ILVerify, loaded in a
/// collectible AssemblyLoadContext and run.</summary>
public class IlBuilderTests
{
    const TypeAttributes PublicClass = TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.BeforeFieldInit;
    const TypeAttributes StaticClass = PublicClass | TypeAttributes.Abstract | TypeAttributes.Sealed;
    const MethodAttributes PublicStatic = MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig;

    // ------------------------------------------------------------------ harness

    sealed class Resolver : IResolver
    {
        static readonly string Shared = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        readonly Dictionary<string, PEReader?> cache = [];

        PEReader? Load(string name)
        {
            if (cache.TryGetValue(name, out var r)) return r;
            var path = Path.Combine(Shared, name + ".dll");
            return cache[name] = File.Exists(path) ? new PEReader(File.OpenRead(path)) : null;
        }

        public PEReader ResolveAssembly(AssemblyNameInfo name) => Load(name.Name)!;
        public PEReader ResolveModule(AssemblyNameInfo name, string module) => null!;
    }

    /// <summary>ILVerify over the whole image; returns "Type::Method: Code" lines.</summary>
    static List<string> IlVerify(byte[] pe)
    {
        using var reader = new PEReader(new MemoryStream(pe));
        var md = reader.GetMetadataReader();
        var verifier = new Verifier(new Resolver(), new VerifierOptions { SanityChecks = true });
        verifier.SetSystemModuleName(new AssemblyNameInfo("System.Private.CoreLib"));
        var errors = new List<string>();
        foreach (var r in verifier.Verify(reader))
        {
            string where = r.Method.IsNil ? "" : md.GetString(md.GetTypeDefinition(md.GetMethodDefinition(r.Method).GetDeclaringType()).Name)
                + "::" + md.GetString(md.GetMethodDefinition(r.Method).Name);
            errors.Add(where + ": " + r.Code + " " + r.Message);
        }
        return errors;
    }

    /// <summary>Emits, verifies (only <paramref name="allowed"/> "Method: Code" prefixes may fail), loads and runs <paramref name="run"/>.</summary>
    static T Run<T>(IlModule module, Func<Assembly, T> run, params string[] allowed)
    {
        var (pe, pdb) = module.Emit();
        var errors = IlVerify(pe).Where(e => !allowed.Any(a => e.Contains(a, StringComparison.Ordinal))).ToList();
        Assert.True(errors.Count == 0, "ILVerify:\n" + string.Join("\n", errors));
        var alc = new AssemblyLoadContext("il-" + module.Name, isCollectible: true);
        try
        {
            var asm = alc.LoadFromStream(new MemoryStream(pe), new MemoryStream(pdb));
            return run(asm);
        }
        finally { alc.Unload(); }
    }

    static object? Call(Assembly asm, string type, string method, params object?[] args)
    {
        try { return asm.GetType(type, throwOnError: true)!.GetMethod(method)!.Invoke(null, args); }
        catch (TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; }
    }

    static IlModule NewModule(string name) => new(name);

    // ------------------------------------------------------------------ programs

    static IlModule HelloWorld()
    {
        var m = NewModule("Hello");
        var program = m.DefineType("Demo", "Program", StaticClass);
        var main = program.DefineMethod("Main", PublicStatic, IlType.Void, [IlType.String.SzArray()]);
        main.DefineParameter(1, "args");
        var il = main.Il;
        il.EmitLoadConst("Hello, World!");
        il.EmitCall(m.Import(typeof(Console).GetMethod("WriteLine", [typeof(string)])!));
        il.EmitReturn();
        m.EntryPoint = main;
        return m;
    }

    [Fact]
    public void Hello_world_runs_in_a_collectible_context_and_as_a_process()
    {
        var m = HelloWorld();
        var output = new StringWriter();
        var old = Console.Out;
        Run(m, asm =>
        {
            Console.SetOut(output);
            try { asm.EntryPoint!.Invoke(null, [Array.Empty<string>()]); } finally { Console.SetOut(old); }
            return 0;
        });
        Assert.Contains("Hello, World!", output.ToString());

        var dir = Directory.CreateTempSubdirectory("alice-il-").FullName;
        try
        {
            var (pe, _) = HelloWorld().Emit();
            File.WriteAllBytes(Path.Combine(dir, "Hello.dll"), pe);
            File.WriteAllText(Path.Combine(dir, "Hello.runtimeconfig.json"),
                "{\"runtimeOptions\":{\"tfm\":\"net" + Environment.Version.Major + ".0\",\"framework\":{\"name\":\"Microsoft.NETCore.App\",\"version\":\""
                + Environment.Version.Major + ".0.0\"}}}");
            var psi = new ProcessStartInfo("dotnet", [Path.Combine(dir, "Hello.dll")]) { RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            Assert.True(p.WaitForExit(60_000));
            Assert.True(p.ExitCode == 0, stderr);
            Assert.Equal("Hello, World!", stdout.Trim());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Framework_references_come_from_the_ref_pack()
    {
        Assert.Contains("Microsoft.NETCore.App.Ref", RefPack.Default.Directory);
        var (pe, _) = HelloWorld().Emit();
        using var reader = new PEReader(new MemoryStream(pe));
        var md = reader.GetMetadataReader();
        var refs = md.AssemblyReferences.Select(h => md.GetString(md.GetAssemblyReference(h).Name)).Order().ToList();
        Assert.Equal(new[] { "System.Console", "System.Runtime" }, refs);
        var runtime = md.AssemblyReferences.Select(md.GetAssemblyReference).Single(a => md.GetString(a.Name) == "System.Runtime");
        Assert.Equal("b03f5f7f11d50a3a", Convert.ToHexStringLower(md.GetBlobBytes(runtime.PublicKeyOrToken)));
        Assert.Equal(Environment.Version.Major, runtime.Version.Major);
    }

    [Fact]
    public void Checked_and_unchecked_arithmetic()
    {
        var m = NewModule("Arith");
        var t = m.DefineType("Demo", "Math", StaticClass);

        var add = t.DefineMethod("AddChecked", PublicStatic, IlType.Int32, [IlType.Int32, IlType.Int32]);
        add.Il.EmitLoadArg(0); add.Il.EmitLoadArg(1); add.Il.EmitBinary(IlBinOp.Add, isChecked: true); add.Il.EmitReturn();

        var addU = t.DefineMethod("AddUnsigned", PublicStatic, IlType.UInt32, [IlType.UInt32, IlType.UInt32]);
        addU.Il.EmitLoadArg(0); addU.Il.EmitLoadArg(1); addU.Il.EmitBinary(IlBinOp.Add, isChecked: true, unsigned: true); addU.Il.EmitReturn();

        var wrap = t.DefineMethod("MulWrap", PublicStatic, IlType.Int32, [IlType.Int32, IlType.Int32]);
        wrap.Il.EmitLoadArg(0); wrap.Il.EmitLoadArg(1); wrap.Il.EmitBinary(IlBinOp.Mul); wrap.Il.EmitReturn();

        var toByte = t.DefineMethod("ToByte", PublicStatic, IlType.Byte, [IlType.Int32]);
        toByte.Il.EmitLoadArg(0); toByte.Il.EmitConvert(PrimitiveTypeCode.Byte, isChecked: true); toByte.Il.EmitReturn();

        // (a * 3 + b) / 2 % 1000 as long, then << 1, with a double average
        var mix = t.DefineMethod("Mix", PublicStatic, IlType.Double, [IlType.Int32, IlType.Int64]);
        var il = mix.Il;
        il.EmitLoadArg(0); il.EmitConvert(PrimitiveTypeCode.Int64); il.EmitLoadConst(3L); il.EmitBinary(IlBinOp.Mul, isChecked: true);
        il.EmitLoadArg(1); il.EmitBinary(IlBinOp.Add, isChecked: true); il.EmitLoadConst(2L); il.EmitBinary(IlBinOp.Div);
        il.EmitLoadConst(1000L); il.EmitBinary(IlBinOp.Rem); il.EmitLoadConst(1); il.EmitBinary(IlBinOp.Shl);
        il.EmitConvert(PrimitiveTypeCode.Double); il.EmitLoadConst(0.5); il.EmitBinary(IlBinOp.Mul); il.EmitReturn();

        var neg = t.DefineMethod("NegShr", PublicStatic, IlType.Int32, [IlType.Int32]);
        neg.Il.EmitLoadArg(0); neg.Il.Emit(ILOpCode.Neg); neg.Il.EmitLoadConst(1); neg.Il.EmitBinary(IlBinOp.Shr, unsigned: true); neg.Il.EmitReturn();

        Run(m, asm =>
        {
            Assert.Equal(5, Call(asm, "Demo.Math", "AddChecked", 2, 3));
            Assert.Throws<OverflowException>(() => Call(asm, "Demo.Math", "AddChecked", int.MaxValue, 1));
            Assert.Throws<OverflowException>(() => Call(asm, "Demo.Math", "AddUnsigned", uint.MaxValue, 1u));
            Assert.Equal(unchecked(int.MaxValue * 2), Call(asm, "Demo.Math", "MulWrap", int.MaxValue, 2));
            Assert.Equal((byte)200, Call(asm, "Demo.Math", "ToByte", 200));
            Assert.Throws<OverflowException>(() => Call(asm, "Demo.Math", "ToByte", 300));
            Assert.Equal((double)(((10L * 3 + 7) / 2 % 1000) << 1) * 0.5, Call(asm, "Demo.Math", "Mix", 10, 7L));
            Assert.Equal(unchecked((int)((uint)-6 >> 1)), Call(asm, "Demo.Math", "NegShr", 6));
            return 0;
        });
    }

    /// <summary>sum of i for i in [0, n) with a while loop.</summary>
    static void SumLoop(IlBuilder il, int padding)
    {
        var sum = il.DeclareLocal(IlType.Int32, "sum");
        var i = il.DeclareLocal(IlType.Int32, "i");
        var cond = il.DefineLabel();
        var body = il.DefineLabel();
        il.EmitBranch(cond);
        il.MarkLabel(body);
        il.EmitLoadLocal(sum); il.EmitLoadLocal(i); il.EmitBinary(IlBinOp.Add); il.EmitStoreLocal(sum);
        for (int k = 0; k < padding; k++) il.Emit(ILOpCode.Nop);
        il.EmitLoadLocal(i); il.EmitLoadConst(1); il.EmitBinary(IlBinOp.Add); il.EmitStoreLocal(i);
        il.MarkLabel(cond);
        il.EmitLoadLocal(i); il.EmitLoadArg(0); il.EmitBranch(body, IlCond.Lt);
        il.EmitLoadLocal(sum); il.EmitReturn();
    }

    [Fact]
    public void Loops_pick_short_branches_and_widen_far_ones()
    {
        var m = NewModule("Loops");
        var t = m.DefineType("Demo", "Loops", StaticClass);
        var near = t.DefineMethod("Near", PublicStatic, IlType.Int32, [IlType.Int32]);
        SumLoop(near.Il, 0);
        var far = t.DefineMethod("Far", PublicStatic, IlType.Int32, [IlType.Int32]);
        SumLoop(far.Il, 300);
        Run(m, asm =>
        {
            Assert.Equal(4950, Call(asm, "Demo.Loops", "Near", 100));
            Assert.Equal(4950, Call(asm, "Demo.Loops", "Far", 100));
            return 0;
        });
        // br.s(2) ldloc.0 ldloc.1 add stloc.0 ldloc.1 ldc.i4.1 add stloc.1 ldloc.1 ldarg.0 blt.s(2) ldloc.0 ret
        Assert.Equal(2 + 8 + 2 + 2 + 2, BodySize(m, "Near"));
        Assert.Equal(5 + 8 + 300 + 2 + 5 + 2, BodySize(m, "Far"));
    }

    static int BodySize(IlModule m, string method)
    {
        var (pe, _) = m.Emit();
        using var reader = new PEReader(new MemoryStream(pe));
        var md = reader.GetMetadataReader();
        var h = md.MethodDefinitions.Single(x => md.GetString(md.GetMethodDefinition(x).Name) == method);
        return reader.GetMethodBody(md.GetMethodDefinition(h).RelativeVirtualAddress).GetILBytes()!.Length;
    }

    static MethodBodyBlock Body(byte[] pe, string method, out PEReader reader)
    {
        reader = new PEReader(new MemoryStream(pe));
        var md = reader.GetMetadataReader();
        var h = md.MethodDefinitions.Single(x => md.GetString(md.GetMethodDefinition(x).Name) == method);
        return reader.GetMethodBody(md.GetMethodDefinition(h).RelativeVirtualAddress);
    }

    [Fact]
    public void Arrays_sz_and_multidimensional()
    {
        var m = NewModule("Arrays");
        var t = m.DefineType("Demo", "Arrays", StaticClass);

        // squares 0..n-1 into int[], return the sum over ldlen
        var sq = t.DefineMethod("SumSquares", PublicStatic, IlType.Int64, [IlType.Int32]);
        var il = sq.Il;
        var arr = il.DeclareLocal(IlType.Int32.SzArray(), "arr");
        var i = il.DeclareLocal(IlType.Int32, "i");
        var acc = il.DeclareLocal(IlType.Int64, "acc");
        il.EmitLoadArg(0); il.EmitNewArray(IlType.Int32); il.EmitStoreLocal(arr);
        var fill = il.DefineLabel(); var fillCond = il.DefineLabel();
        il.EmitBranch(fillCond);
        il.MarkLabel(fill);
        il.EmitLoadLocal(arr); il.EmitLoadLocal(i); il.EmitLoadLocal(i); il.EmitLoadLocal(i); il.EmitBinary(IlBinOp.Mul); il.EmitStoreElement(IlType.Int32);
        il.EmitLoadLocal(i); il.EmitLoadConst(1); il.EmitBinary(IlBinOp.Add); il.EmitStoreLocal(i);
        il.MarkLabel(fillCond);
        il.EmitLoadLocal(i); il.EmitLoadLocal(arr); il.Emit(ILOpCode.Ldlen); il.EmitConvert(PrimitiveTypeCode.Int32); il.EmitBranch(fill, IlCond.Lt);
        il.EmitLoadConst(0); il.EmitStoreLocal(i);
        var sum = il.DefineLabel(); var sumCond = il.DefineLabel();
        il.EmitBranch(sumCond);
        il.MarkLabel(sum);
        il.EmitLoadLocal(acc); il.EmitLoadLocal(arr); il.EmitLoadLocal(i); il.EmitLoadElement(IlType.Int32); il.EmitConvert(PrimitiveTypeCode.Int64);
        il.EmitBinary(IlBinOp.Add); il.EmitStoreLocal(acc);
        il.EmitLoadLocal(i); il.EmitLoadConst(1); il.EmitBinary(IlBinOp.Add); il.EmitStoreLocal(i);
        il.MarkLabel(sumCond);
        il.EmitLoadLocal(i); il.EmitLoadLocal(arr); il.Emit(ILOpCode.Ldlen); il.EmitConvert(PrimitiveTypeCode.Int32); il.EmitBranch(sum, IlCond.Lt);
        il.EmitLoadLocal(acc); il.EmitReturn();

        // string[] round trip through stelem.ref / ldelem.ref
        var strs = t.DefineMethod("Second", PublicStatic, IlType.String, [IlType.String, IlType.String]);
        il = strs.Il;
        var sa = il.DeclareLocal(IlType.String.SzArray());
        il.EmitLoadConst(2); il.EmitNewArray(IlType.String); il.EmitStoreLocal(sa);
        il.EmitLoadLocal(sa); il.EmitLoadConst(0); il.EmitLoadArg(0); il.EmitStoreElement(IlType.String);
        il.EmitLoadLocal(sa); il.EmitLoadConst(1); il.EmitLoadArg(1); il.EmitStoreElement(IlType.String);
        il.EmitLoadLocal(sa); il.EmitLoadConst(1); il.EmitLoadElement(IlType.String); il.EmitReturn();

        // int[,]: newobj int32[,]::.ctor(int, int), Set, Get (array methods are MemberRefs on the TypeSpec)
        var grid = IlType.Int32.Array(2);
        var ctor = m.MethodRef(grid, ".ctor", MethodSig.Instance(IlType.Void, IlType.Int32, IlType.Int32));
        var set = m.MethodRef(grid, "Set", MethodSig.Instance(IlType.Void, IlType.Int32, IlType.Int32, IlType.Int32));
        var get = m.MethodRef(grid, "Get", MethodSig.Instance(IlType.Int32, IlType.Int32, IlType.Int32));
        var md = t.DefineMethod("Grid", PublicStatic, IlType.Int32, [IlType.Int32]);
        il = md.Il;
        var g = il.DeclareLocal(grid, "g");
        il.EmitLoadConst(3); il.EmitLoadConst(4); il.EmitNewObj(ctor); il.EmitStoreLocal(g);
        il.EmitLoadLocal(g); il.EmitLoadConst(2); il.EmitLoadConst(3); il.EmitLoadArg(0); il.EmitCall(set);
        il.EmitLoadLocal(g); il.EmitLoadConst(2); il.EmitLoadConst(3); il.EmitCall(get); il.EmitReturn();

        Run(m, asm =>
        {
            Assert.Equal(Enumerable.Range(0, 50).Sum(x => (long)x * x), Call(asm, "Demo.Arrays", "SumSquares", 50));
            Assert.Equal("b", Call(asm, "Demo.Arrays", "Second", "a", "b"));
            Assert.Equal(42, Call(asm, "Demo.Arrays", "Grid", 42));
            return 0;
        });
    }

    [Fact]
    public void Try_catch_filter_finally_and_fault()
    {
        var m = NewModule("Eh");
        var t = m.DefineType("Demo", "Eh", StaticClass);
        var log = t.DefineField("Log", IlType.Int32, FieldAttributes.Public | FieldAttributes.Static);
        var ioe = m.Import(typeof(InvalidOperationException));
        var ioeCtor = m.Import(typeof(InvalidOperationException).GetConstructor([typeof(string)])!);
        var exn = m.Import(typeof(Exception));
        var message = m.Import(typeof(Exception).GetProperty("Message")!.GetMethod!);
        var strEq = m.Import(typeof(string).GetMethod("op_Equality", [typeof(string), typeof(string)])!);

        // Guard(x): r = 0; try { if (x == 0) throw new IOE("zero"); r = 1 } catch (IOE) { r = 2 } finally { Log += 10 } return r
        var guard = t.DefineMethod("Guard", PublicStatic, IlType.Int32, [IlType.Int32]);
        var il = guard.Il;
        var r = il.DeclareLocal(IlType.Int32, "r");
        var end = il.BeginTry();
        var ok = il.DefineLabel();
        il.EmitLoadArg(0); il.EmitBranch(ok, IlCond.True);
        il.EmitLoadConst("zero"); il.EmitNewObj(ioeCtor); il.EmitThrow();
        il.MarkLabel(ok);
        il.EmitLoadConst(1); il.EmitStoreLocal(r);
        il.BeginCatch(ioe);
        il.EmitPop(); il.EmitLoadConst(2); il.EmitStoreLocal(r);
        il.BeginFinally();
        il.EmitLoadField(log); il.EmitLoadConst(10); il.EmitBinary(IlBinOp.Add); il.EmitStoreField(log);
        il.EndTry();
        il.EmitLoadLocal(r); il.EmitReturn();
        _ = end;

        // Filtered(s): try { throw new IOE(s) } catch (Exception e) when (e.Message == "yes") { return 1 } ... return 0
        var filtered = t.DefineMethod("Filtered", PublicStatic, IlType.Int32, [IlType.String]);
        il = filtered.Il;
        var res = il.DeclareLocal(IlType.Int32);
        var outer = il.BeginTry();
        var inner = il.BeginTry();
        il.EmitLoadArg(0); il.EmitNewObj(ioeCtor); il.EmitThrow();
        il.BeginFilter();
        il.EmitCastClass(exn); il.EmitCall(message, virtualCall: true); il.EmitLoadConst("yes"); il.EmitCall(strEq);
        il.BeginFilterHandler();
        il.EmitPop(); il.EmitLoadConst(1); il.EmitStoreLocal(res); il.EmitLeave(inner);
        il.EndTry();
        il.BeginCatch(exn);   // the outer catch sees what the filter rejected
        il.EmitPop(); il.EmitLoadConst(-1); il.EmitStoreLocal(res);
        il.EndTry();
        il.EmitLoadLocal(res); il.EmitReturn();
        _ = outer;

        // Faulty(): try { try { throw } fault { Log += 100 } } catch { } return Log
        var faulty = t.DefineMethod("Faulty", PublicStatic, IlType.Int32);
        il = faulty.Il;
        il.BeginTry();
        il.BeginTry();
        il.EmitLoadConst("f"); il.EmitNewObj(ioeCtor); il.EmitThrow();
        il.BeginFault();
        il.EmitLoadField(log); il.EmitLoadConst(100); il.EmitBinary(IlBinOp.Add); il.EmitStoreField(log);
        il.EndTry();
        il.BeginCatch(m.CoreType("Object"));
        il.EmitPop();
        il.EndTry();
        il.EmitLoadField(log); il.EmitReturn();

        Run(m, asm =>
        {
            Assert.Equal(1, Call(asm, "Demo.Eh", "Guard", 5));
            Assert.Equal(2, Call(asm, "Demo.Eh", "Guard", 0));
            Assert.Equal(20, asm.GetType("Demo.Eh")!.GetField("Log")!.GetValue(null));
            Assert.Equal(1, Call(asm, "Demo.Eh", "Filtered", "yes"));
            Assert.Equal(-1, Call(asm, "Demo.Eh", "Filtered", "no"));
            Assert.Equal(120, Call(asm, "Demo.Eh", "Faulty"));
            return 0;
        });
        var (pe, _) = m.Emit();
        Assert.Equal(2, Body(pe, "Guard", out var rd).ExceptionRegions.Length);   // catch nested in finally
        rd.Dispose();
    }

    [Fact]
    public void Generic_method_with_constraint_and_generic_type()
    {
        var m = NewModule("Generics");
        var t = m.DefineType("Demo", "G", StaticClass);
        var icomparable = m.External("System", "IComparable`1");
        var compareTo = m.MethodRef(icomparable.Instantiate(IlType.MVar(0)), "CompareTo", MethodSig.Instance(IlType.Int32, IlType.Var(0)));

        // static T Max<T>(T a, T b) where T : IComparable<T> => a.CompareTo(b) >= 0 ? a : b   (constrained. callvirt)
        var max = t.DefineMethod("Max", PublicStatic, IlType.MVar(0), [IlType.MVar(0), IlType.MVar(0)], genericParameters: ["T"]);
        max.GenericParameters[0].Constraints.Add(icomparable.Instantiate(IlType.MVar(0)));
        var il = max.Il;
        var second = il.DefineLabel();
        il.EmitLoadArgAddress(0); il.EmitLoadArg(1); il.EmitConstrainedPrefix(IlType.MVar(0)); il.EmitCall(compareTo, virtualCall: true);
        il.EmitLoadConst(0); il.EmitBranch(second, IlCond.Lt);
        il.EmitLoadArg(0); il.EmitReturn();
        il.MarkLabel(second);
        il.EmitLoadArg(1); il.EmitReturn();

        // class Box<T> { public T Value; public Box(T v) { base(); Value = v; } public T Get() => Value; }
        var box = m.DefineType("Demo", "Box`1", PublicClass);
        box.DefineGenericParameter("T");
        var value = box.DefineField("Value", IlType.Var(0), FieldAttributes.Public);
        var ctor = box.DefineConstructor(MethodAttributes.Public | MethodAttributes.HideBySig, [IlType.Var(0)]);
        il = ctor.Il;
        il.EmitLoadArg(0); il.EmitCall(m.Import(typeof(object).GetConstructor([])!));
        il.EmitLoadArg(0); il.EmitLoadArg(1); il.EmitStoreField(value); il.EmitReturn();
        var getter = box.DefineMethod("get_Item", MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName, IlType.Var(0));
        getter.Il.EmitLoadArg(0); getter.Il.EmitLoadField(value); getter.Il.EmitReturn();
        box.DefineProperty("Item", IlType.Var(0), getter, null);

        // static int UseBoth(int a, int b) => new Box<int>(Max<int>(a, b)).Item ; static string MaxS(string, string) => Max<string>
        var boxInt = box.Instantiate(IlType.Int32);
        var use = t.DefineMethod("UseBoth", PublicStatic, IlType.Int32, [IlType.Int32, IlType.Int32]);
        il = use.Il;
        il.EmitLoadArg(0); il.EmitLoadArg(1); il.EmitCall(max.MakeGeneric(IlType.Int32));
        il.EmitNewObj(ctor.On(boxInt)); il.EmitCall(getter.On(boxInt)); il.EmitReturn();
        var maxS = t.DefineMethod("MaxS", PublicStatic, IlType.String, [IlType.String, IlType.String]);
        maxS.Il.EmitLoadArg(0); maxS.Il.EmitLoadArg(1); maxS.Il.EmitCall(max.MakeGeneric(IlType.String)); maxS.Il.EmitReturn();

        Run(m, asm =>
        {
            Assert.Equal(9, Call(asm, "Demo.G", "UseBoth", 4, 9));
            Assert.Equal(7, Call(asm, "Demo.G", "UseBoth", 7, -3));
            Assert.Equal("pear", Call(asm, "Demo.G", "MaxS", "apple", "pear"));
            var boxType = asm.GetType("Demo.Box`1")!.MakeGenericType(typeof(long));
            var b = Activator.CreateInstance(boxType, 5L)!;
            Assert.Equal(5L, boxType.GetProperty("Item")!.GetValue(b));
            var constraint = asm.GetType("Demo.G")!.GetMethod("Max")!.GetGenericArguments()[0].GetGenericParameterConstraints().Single();
            Assert.Equal(typeof(IComparable<>).Name, constraint.Name);
            return 0;
        });
    }

    [Fact]
    public void Struct_with_fields_and_instance_method()
    {
        var m = NewModule("Structs");
        var point = m.DefineStruct("Demo", "Point");
        var x = point.DefineField("X", IlType.Int32, FieldAttributes.Public);
        var y = point.DefineField("Y", IlType.Int32, FieldAttributes.Public);
        var sum = point.DefineMethod("Sum", MethodAttributes.Public | MethodAttributes.HideBySig, IlType.Int32);
        sum.Il.EmitLoadArg(0); sum.Il.EmitLoadField(x); sum.Il.EmitLoadArg(0); sum.Il.EmitLoadField(y); sum.Il.EmitBinary(IlBinOp.Add); sum.Il.EmitReturn();

        var t = m.DefineType("Demo", "Use", StaticClass);
        var make = t.DefineMethod("Make", PublicStatic, point.Type, [IlType.Int32, IlType.Int32]);
        var il = make.Il;
        var p = il.DeclareLocal(point.Type, "p");
        il.EmitLoadLocalAddress(p); il.EmitInitObj(point.Type);
        il.EmitLoadLocalAddress(p); il.EmitLoadArg(0); il.EmitStoreField(x);
        il.EmitLoadLocalAddress(p); il.EmitLoadArg(1); il.EmitStoreField(y);
        il.EmitLoadLocal(p); il.EmitReturn();

        var total = t.DefineMethod("Total", PublicStatic, IlType.Int32, [IlType.Int32, IlType.Int32]);
        il = total.Il;
        var q = il.DeclareLocal(point.Type, "q");
        il.EmitLoadArg(0); il.EmitLoadArg(1); il.EmitCall(make); il.EmitStoreLocal(q);
        il.EmitLoadLocalAddress(q); il.EmitCall(sum); il.EmitReturn();

        var boxed = t.DefineMethod("Boxed", PublicStatic, IlType.Object, [IlType.Int32]);
        il = boxed.Il;
        il.EmitLoadArg(0); il.EmitLoadArg(0); il.EmitCall(make); il.EmitBox(point.Type); il.EmitReturn();

        Run(m, asm =>
        {
            Assert.Equal(7, Call(asm, "Demo.Use", "Total", 3, 4));
            var o = Call(asm, "Demo.Use", "Boxed", 6)!;
            Assert.True(o.GetType().IsValueType);
            Assert.Equal(6, o.GetType().GetField("Y")!.GetValue(o));
            return 0;
        });
    }

    [Fact]
    public void Static_class_calls_through_a_function_pointer()
    {
        var m = NewModule("FnPtr");
        var t = m.DefineType("Demo", "Native", StaticClass);
        var twice = t.DefineMethod("Twice", PublicStatic, IlType.Int32, [IlType.Int32]);
        twice.Il.EmitLoadArg(0); twice.Il.EmitLoadConst(2); twice.Il.EmitBinary(IlBinOp.Mul); twice.Il.EmitReturn();

        var sig = MethodSig.Static(IlType.Int32, IlType.Int32);
        var via = t.DefineMethod("Via", PublicStatic, IlType.Int32, [IlType.Int32]);
        var il = via.Il;
        var fp = il.DeclareLocal(IlType.FnPtr(sig), "fp");   // delegate*<int, int>
        il.EmitLoadFunction(twice); il.EmitStoreLocal(fp);
        il.EmitLoadArg(0); il.EmitLoadLocal(fp); il.EmitCallIndirect(sig); il.EmitReturn();

        // calli is unverifiable (ECMA-335 III.3.20); nothing else may fail
        Run(m, asm =>
        {
            Assert.Equal(42, Call(asm, "Demo.Native", "Via", 21));
            var local = asm.GetType("Demo.Native")!.GetMethod("Via")!.GetMethodBody()!.LocalVariables.Single();
            Assert.True(local.LocalType.IsFunctionPointer);
            return 0;
        }, "Native::Via:");
        // ILVerify 10 does not check calli at all: the only finding is its own "not implemented"
        Assert.Equal(new[] { "Native::Via: None ImportCalli not implemented" }, IlVerify(m.Emit().Pe));
    }

    [Fact]
    public void IlVerify_is_a_live_oracle()
    {
        var m = NewModule("Broken");
        var f = m.DefineType("Demo", "B", StaticClass).DefineMethod("F", PublicStatic, IlType.Int32);
        f.Il.EmitLoadConst(1); f.Il.EmitLoadConst("x"); f.Il.EmitBinary(IlBinOp.Add); f.Il.EmitReturn();   // int32 + string
        Assert.Contains(IlVerify(m.Emit().Pe), e => e.StartsWith("B::F: ExpectedNumericType", StringComparison.Ordinal));
    }

    [Fact]
    public void Custom_attributes_round_trip()
    {
        var m = NewModule("Attrs");
        m.CustomAttributes.Add(new IlCustomAttribute(m.Import(typeof(AssemblyMetadataAttribute).GetConstructor([typeof(string), typeof(string)])!), ["k", "v"]));
        var t = m.DefineType("Demo", "A", StaticClass);
        var f = t.DefineMethod("F", PublicStatic, IlType.Void);
        f.Il.EmitReturn();
        f.CustomAttributes.Add(new IlCustomAttribute(m.Import(typeof(ObsoleteAttribute).GetConstructor([typeof(string), typeof(bool)])!), ["old", false],
            [new IlNamedArgument("DiagnosticId", IlType.String, "AL1")]));
        t.CustomAttributes.Add(new IlCustomAttribute(m.Import(typeof(System.ComponentModel.DescriptionAttribute).GetConstructor([typeof(string)])!), ["desc"]));
        Run(m, asm =>
        {
            Assert.Equal("v", asm.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "k").Value);
            var o = asm.GetType("Demo.A")!.GetMethod("F")!.GetCustomAttribute<ObsoleteAttribute>()!;
            Assert.Equal(("old", "AL1"), (o.Message, o.DiagnosticId));
            Assert.Equal("desc", asm.GetType("Demo.A")!.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()!.Description);
            return 0;
        });
    }

    static IlModule Sample(string greeting = "hi")
    {
        var m = NewModule("Det");
        var doc = m.AddDocument("/src/det.al", "def main\n  puts \"" + greeting + "\"\nend\n", embed: true);
        m.SourceLinkJson = """{"documents":{"/src/*":"https://example.org/src/*"}}""";
        var t = m.DefineType("Demo", "Program", StaticClass);
        var main = t.DefineMethod("Main", PublicStatic, IlType.Void);
        var il = main.Il;
        il.MarkSequencePoint(doc, 1, 1, 1, 9);
        il.Emit(ILOpCode.Nop);
        il.BeginScope();
        var g = il.DeclareLocal(IlType.String, "greeting");
        il.MarkSequencePoint(new Span("/src/det.al", 2, 3, 12));
        il.EmitLoadConst(greeting); il.EmitStoreLocal(g);
        il.EmitLoadLocal(g); il.EmitCall(m.Import(typeof(Console).GetMethod("WriteLine", [typeof(string)])!));
        il.EndScope();
        il.MarkHiddenSequencePoint();
        il.Emit(ILOpCode.Nop);
        il.MarkSequencePoint(doc, 3, 1, 3, 4);
        il.EmitReturn();
        m.EntryPoint = main;
        SumLoop(t.DefineMethod("Sum", PublicStatic, IlType.Int32, [IlType.Int32]).Il, 0);
        return m;
    }

    [Fact]
    public void Output_is_byte_identical_across_builds()
    {
        var (pe1, pdb1) = Sample().Emit();
        var (pe2, pdb2) = Sample().Emit();
        Assert.Equal(pe1, pe2);
        Assert.Equal(pdb1, pdb2);
        var (pe3, _) = Sample("hello").Emit();
        Guid Mvid(byte[] pe)
        {
            using var r = new PEReader(new MemoryStream(pe));
            var md = r.GetMetadataReader();
            return md.GetGuid(md.GetModuleDefinition().Mvid);
        }
        Assert.Equal(Mvid(pe1), Mvid(pe2));
        Assert.NotEqual(Mvid(pe1), Mvid(pe3));
        Assert.NotEqual(Guid.Empty, Mvid(pe1));
        Assert.Empty(IlVerify(pe1));
    }

    [Fact]
    public void Pdb_round_trips_sequence_points_scopes_and_source()
    {
        var (pe, pdb) = Sample().Emit();
        using var peReader = new PEReader(new MemoryStream(pe));
        var cv = peReader.ReadDebugDirectory().Single(d => d.Type == DebugDirectoryEntryType.CodeView);
        Assert.Equal("Det.pdb", peReader.ReadCodeViewDebugDirectoryData(cv).Path);
        Assert.Contains(peReader.ReadDebugDirectory(), d => d.Type == DebugDirectoryEntryType.Reproducible);
        Assert.Contains(peReader.ReadDebugDirectory(), d => d.Type == DebugDirectoryEntryType.PdbChecksum);

        using var provider = MetadataReaderProvider.FromPortablePdbImage([.. pdb]);
        var r = provider.GetMetadataReader();
        Assert.Equal(new BlobContentId(r.DebugMetadataHeader!.Id).Guid, peReader.ReadCodeViewDebugDirectoryData(cv).Guid);
        var doc = r.GetDocument(r.Documents.Single());
        Assert.Equal("/src/det.al", r.GetString(doc.Name));
        Assert.Equal(IlDocument.AliceLanguage, r.GetGuid(doc.Language));
        Assert.Equal(32, r.GetBlobBytes(doc.Hash).Length);

        var main = MetadataTokens.MethodDefinitionHandle(1);
        var sps = r.GetMethodDebugInformation(main).GetSequencePoints().ToList();
        Assert.Equal(4, sps.Count);
        Assert.Equal((0, 1, 1, 1, 9), (sps[0].Offset, sps[0].StartLine, sps[0].StartColumn, sps[0].EndLine, sps[0].EndColumn));
        Assert.Equal((1, 2, 3, 2, 15), (sps[1].Offset, sps[1].StartLine, sps[1].StartColumn, sps[1].EndLine, sps[1].EndColumn));
        Assert.True(sps[2].IsHidden);
        Assert.Equal((3, 1, 3, 4), (sps[3].StartLine, sps[3].StartColumn, sps[3].EndLine, sps[3].EndColumn));
        Assert.True(sps[3].Offset > sps[2].Offset);

        var scopes = r.GetLocalScopes(main).Select(r.GetLocalScope).ToList();
        Assert.Equal(2, scopes.Count);
        Assert.Equal(0, scopes[0].StartOffset);
        var named = scopes[1].GetLocalVariables().Select(h => r.GetString(r.GetLocalVariable(h).Name)).ToList();
        Assert.Equal(new[] { "greeting" }, named);
        Assert.Equal(1, scopes[1].StartOffset);

        var sumScopes = r.GetLocalScopes(MetadataTokens.MethodDefinitionHandle(2)).Select(r.GetLocalScope).ToList();
        Assert.Equal(new[] { "sum", "i" }, sumScopes.Single().GetLocalVariables().Select(h => r.GetString(r.GetLocalVariable(h).Name)).ToList());

        var sourceLink = new Guid("cc110556-a091-4d38-9fec-25ab9a351a6a");
        var embedded = new Guid("0e8a571b-6926-466e-b4ad-8ab04611f5fe");
        var cdis = r.CustomDebugInformation.Select(r.GetCustomDebugInformation).ToList();
        var link = cdis.Single(c => r.GetGuid(c.Kind) == sourceLink);
        Assert.Contains("example.org", System.Text.Encoding.UTF8.GetString(r.GetBlobBytes(link.Value)));
        var src = r.GetBlobBytes(cdis.Single(c => r.GetGuid(c.Kind) == embedded).Value);
        Assert.Equal(0, BitConverter.ToInt32(src, 0));   // short text is stored uncompressed
        Assert.StartsWith("def main", System.Text.Encoding.UTF8.GetString(src, 4, src.Length - 4));
    }

    [Fact]
    public void Max_stack_is_computed_and_bad_stacks_are_rejected()
    {
        var m = NewModule("Stack");
        var t = m.DefineType("Demo", "S", StaticClass);
        var deep = t.DefineMethod("Deep", PublicStatic, IlType.Int32);
        for (int i = 1; i <= 9; i++) deep.Il.EmitLoadConst(i);
        for (int i = 1; i < 9; i++) deep.Il.EmitBinary(IlBinOp.Add);
        deep.Il.EmitReturn();
        Run(m, asm => { Assert.Equal(45, Call(asm, "Demo.S", "Deep")); return 0; });
        Assert.Equal(9, deep.Il.MaxStack);

        var bad = NewModule("Bad");
        var b = bad.DefineType("Demo", "B", StaticClass).DefineMethod("F", PublicStatic, IlType.Int32, [IlType.Int32]);
        var join = b.Il.DefineLabel();
        b.Il.EmitLoadArg(0); b.Il.EmitBranch(join, IlCond.True);
        b.Il.EmitLoadConst(1);                // depth 1 on this path, 0 on the other
        b.Il.MarkLabel(join);
        b.Il.EmitReturn();
        Assert.Throws<InvalidOperationException>(() => bad.Emit());
    }

    [Fact]
    public void Peephole_folds_constants_temps_and_branches()
    {
        var m = NewModule("Peep");
        var t = m.DefineType("Demo", "P", StaticClass);

        // ((2 + 3) * 4) as long -> ldc.i4.s 20; conv.i8; ret
        var c = t.DefineMethod("Const", PublicStatic, IlType.Int64);
        c.Il.Peephole = IlPeephole.All;
        c.Il.EmitLoadConst(2); c.Il.EmitLoadConst(3); c.Il.EmitBinary(IlBinOp.Add); c.Il.EmitLoadConst(4); c.Il.EmitBinary(IlBinOp.Mul);
        c.Il.EmitConvert(PrimitiveTypeCode.Int64); c.Il.EmitReturn();

        // a checked overflow stays (it must throw at run time)
        var o = t.DefineMethod("Overflow", PublicStatic, IlType.Int32);
        o.Il.Peephole = IlPeephole.All;
        o.Il.EmitLoadConst(int.MaxValue); o.Il.EmitLoadConst(1); o.Il.EmitBinary(IlBinOp.Add, isChecked: true); o.Il.EmitReturn();

        // tmp = a + b; return tmp  ->  ldarg.0 ldarg.1 add ret
        var tmp = t.DefineMethod("Temp", PublicStatic, IlType.Int32, [IlType.Int32, IlType.Int32]);
        tmp.Il.Peephole = IlPeephole.All;
        var tl = tmp.Il.DeclareLocal(IlType.Int32);
        tmp.Il.EmitLoadArg(0); tmp.Il.EmitLoadArg(1); tmp.Il.EmitBinary(IlBinOp.Add); tmp.Il.EmitStoreLocal(tl); tmp.Il.EmitLoadLocal(tl);
        tmp.Il.EmitDup(); tmp.Il.EmitPop(); tmp.Il.EmitReturn();

        // if (!x) goto L1 else goto L2, L1 jumps to L2 through a chain and a br to the next instruction
        var br = t.DefineMethod("Branchy", PublicStatic, IlType.Int32, [IlType.Bool]);
        var il = br.Il;
        il.Peephole = IlPeephole.All;
        var l1 = il.DefineLabel(); var l2 = il.DefineLabel(); var l3 = il.DefineLabel(); var next = il.DefineLabel();
        il.EmitLoadArg(0); il.EmitBranch(l1, IlCond.True);
        il.EmitBranch(l2);
        il.MarkLabel(l1);
        il.EmitBranch(next);
        il.MarkLabel(next);
        il.EmitLoadConst(1); il.EmitReturn();
        il.MarkLabel(l2);
        il.EmitBranch(l3);
        il.MarkLabel(l3);
        il.EmitLoadConst(0); il.EmitReturn();

        Run(m, asm =>
        {
            Assert.Equal(20L, Call(asm, "Demo.P", "Const"));
            Assert.Throws<OverflowException>(() => Call(asm, "Demo.P", "Overflow"));
            Assert.Equal(7, Call(asm, "Demo.P", "Temp", 3, 4));
            Assert.Equal(1, Call(asm, "Demo.P", "Branchy", true));
            Assert.Equal(0, Call(asm, "Demo.P", "Branchy", false));
            return 0;
        });
        Assert.Equal(2 + 1 + 1, BodySize(m, "Const"));
        Assert.Equal(5 + 1 + 1 + 1, BodySize(m, "Overflow"));
        Assert.Equal(4, BodySize(m, "Temp"));
        // ldarg.0 brfalse.s L2 ldc.i4.1 ret L2: ldc.i4.0 ret
        Assert.Equal(1 + 2 + 1 + 1 + 1 + 1, BodySize(m, "Branchy"));
    }

    [Fact]
    public void Prefixes_encode_before_their_instruction()
    {
        var m = NewModule("Prefix");
        var t = m.DefineType("Demo", "P", StaticClass);
        var cell = t.DefineField("Cell", IlType.Int32, FieldAttributes.Public | FieldAttributes.Static);
        var read = t.DefineMethod("Read", PublicStatic, IlType.Int32);
        read.Il.EmitLoadFieldAddress(cell); read.Il.EmitVolatilePrefix(); read.Il.EmitUnalignedPrefix(4); read.Il.Emit(ILOpCode.Ldind_i4); read.Il.EmitReturn();
        var self = t.DefineMethod("Tail", PublicStatic, IlType.Int32);
        self.Il.EmitTailPrefix(); self.Il.EmitCall(read); self.Il.EmitReturn();
        var arr = t.DefineMethod("First", PublicStatic, IlType.Int32, [IlType.Int32.SzArray()]);
        arr.Il.EmitLoadArg(0); arr.Il.EmitLoadConst(0); arr.Il.EmitReadonlyPrefix(); arr.Il.Emit(ILOpCode.Ldelema, IlType.Int32);
        arr.Il.Emit(ILOpCode.Ldind_i4); arr.Il.EmitReturn();
        Run(m, asm =>
        {
            asm.GetType("Demo.P")!.GetField("Cell")!.SetValue(null, 11);
            Assert.Equal(11, Call(asm, "Demo.P", "Tail"));
            Assert.Equal(5, Call(asm, "Demo.P", "First", new[] { 5, 6 }));
            return 0;
        });
        var (pe, _) = m.Emit();
        var body = Body(pe, "Read", out var rd);
        // ldsflda <tok>; volatile. (FE 13); unaligned. 4 (FE 12 04); ldind.i4 (4A); ret
        Assert.Equal(new byte[] { 0xFE, 0x13, 0xFE, 0x12, 0x04, 0x4A, 0x2A }, body.GetILBytes()![5..]);
        rd.Dispose();
    }

    [Fact]
    public void Air_types_map_to_il_types()
    {
        var m = NewModule("Air");
        Assert.Same(IlType.Int32, m.FromAir(new AirType.Prim(PrimKind.Int)));
        Assert.Equal("double[]", m.FromAir(new AirType.Array(new AirType.Prim(PrimKind.Double))).ToString());
        Assert.Equal("int32[,]", m.FromAir(new AirType.Array(new AirType.Prim(PrimKind.Int), 2)).ToString());
        var nullable = m.FromAir(new AirType.Prim(PrimKind.Long) { Nullable = true });
        Assert.Equal("[System.Runtime]System.Nullable`1<int64>", nullable.ToString());
        var list = m.FromAir(new AirType.ClrRef("global::System.Collections.Generic.List", typeof(List<>), [new AirType.Prim(PrimKind.String)]));
        Assert.Equal("[System.Collections]System.Collections.Generic.List`1<string>", list.ToString());
        var tuple = m.FromAir(new AirType.Tuple([new AirType.Prim(PrimKind.Int), new AirType.Prim(PrimKind.Bool)], null));
        Assert.True(tuple.IsValueType);
        Assert.True(m.CoreType("Decimal").IsValueType);
        Assert.True(m.TypeRef("System", "DayOfWeek").IsValueType);
        Assert.False(m.CoreType("Exception").IsValueType);
        Assert.Throws<NotSupportedException>(() => m.FromAir(new AirType.TypeParam("T")));
        Assert.Equal("!!0", m.FromAir(new AirType.TypeParam("T"), _ => IlType.MVar(0)).ToString());
    }
}
