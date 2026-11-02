using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using Alice.Compiler;
using Alice.Compiler.Backend;
using Alice.Compiler.Backend.Il;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;
using ILVerify;
using Binder = Alice.Compiler.Semantics.Binder;

namespace Alice.Tests.Backends;

/// <summary>
/// D1.10: FFI through the IL backend (<see cref="AirToIlFfi"/>, ADR 0018/0033). The `lib` declarations of the
/// FfiTests programs are bound by the real Binder and emitted as IL; a small hand-written IL `Main` (the test driver,
/// standing in for AirToIl's statement emitter) makes the same calls and prints the same values. Every program runs
/// for real against libc and must print exactly what the C# backend prints for the Alice program.
/// </summary>
[Collection("console")]
public class AirToIlFfiTests
{
    const TypeAttributes StaticClass = TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class | TypeAttributes.BeforeFieldInit;
    const MethodAttributes PublicStatic = MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig;

    static string LibC => OperatingSystem.IsWindows() ? "ucrtbase.dll" : OperatingSystem.IsMacOS() ? "/usr/lib/libSystem.B.dylib" : "libc.so.6";

    static string Lib(string funs) => $"@[Link(\"{LibC}\")]\nlib LibC\n{funs}end\n";

    static string CSharpRun(string src) => AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n");

    // ------------------------------------------------------------------ harness

    /// <summary>The module under construction: the FFI emitter, the bound `lib` funs by Alice name, and a driver.</summary>
    internal sealed class Prog
    {
        public IlModule Module = null!;
        public AirToIlFfi Ffi = null!;
        public Binder Binder = null!;
        public List<FileNode> Asts = null!;
        public IlTypeDef Program = null!;
        public IlBuilder Il = null!;
        public readonly Dictionary<string, IlMethodDef> Funs = [];
        public readonly Dictionary<string, IlTypeDef> Structs = [];

        public IlMethod M(Type t, string name, params Type[] ps) => Module.Import(t.GetMethod(name, ps)!);

        public void Call(string fun) => Il.EmitCall(Funs[fun]);
        public void Str(string s) => Il.EmitLoadConst(s);
        public void Print(Type t) => Il.EmitCall(M(typeof(Console), "WriteLine", t));
        public void PrintStr() => Print(typeof(string));

        /// <summary>Alice prints booleans as `true` / `false`.</summary>
        public void PrintBool()
        {
            var no = Il.DefineLabel();
            var done = Il.DefineLabel();
            Il.EmitBranch(no, IlCond.False);
            Il.EmitLoadConst("true");
            Il.EmitBranch(done);
            Il.MarkLabel(no);
            Il.EmitLoadConst("false");
            Il.MarkLabel(done);
            PrintStr();
        }
    }

    static Prog Build(string name, string src, Action<Prog> main, Action<Prog>? types = null)
    {
        var asts = new List<FileNode> { Parser.ParseFile(src, "main.al") };
        var binder = new Binder(asts, new ClrTypes(null));
        binder.Bind();
        var p = new Prog { Module = new IlModule(name), Binder = binder, Asts = asts };
        p.Ffi = new AirToIlFfi(p.Module, cs => p.Structs.TryGetValue(cs, out var t) ? t.Type : null);
        types?.Invoke(p);
        foreach (var l in asts[0].Items.OfType<LibDecl>())
        {
            p.Ffi.EmitLib(l);
            var s = (TypeSym)l.Sym!;
            foreach (var f in l.Funs) p.Funs[f.Name] = p.Ffi.Funs[s.Methods[f.Name][0]];
        }
        p.Program = p.Module.DefineType("", "Program", StaticClass);
        var m = p.Program.DefineMethod("Main", PublicStatic, IlType.Void, [IlType.String.SzArray()]);
        p.Il = m.Il;
        main(p);
        p.Il.EmitReturn();
        p.Module.EntryPoint = m;
        return p;
    }

    sealed class Resolver : IResolver
    {
        static readonly string Shared = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        readonly Dictionary<string, PEReader?> cache = [];

        PEReader? Load(string name)
        {
            if (cache.TryGetValue(name, out var r)) return r;
            var path = Path.Combine(Shared, name + ".dll");
            if (!File.Exists(path)) path = Path.Combine(AppContext.BaseDirectory, name + ".dll");   // Alice.Runtime
            return cache[name] = File.Exists(path) ? new PEReader(File.OpenRead(path)) : null;
        }

        public PEReader ResolveAssembly(AssemblyNameInfo name) => Load(name.Name)!;
        public PEReader ResolveModule(AssemblyNameInfo name, string module) => null!;
    }

    /// <summary>What ILVerify may report, all of it what C# `unsafe` code with `[module: SkipLocalsInit]` (the C#
    /// backend's output) reports too: no `localsinit` flag, `conv.u` of a pinned byref (the `fixed` idiom), unmanaged
    /// pointers and `localloc` ("Unverifiable"), and `calli`, which ILVerify 10 does not implement. ILVerify stops at
    /// the first such instruction of a method, so these bodies are checked by running them. Anything else fails.</summary>
    static readonly string[] UnsafeCodes = [": InitLocals ", ": ExpectedNumericType ", ": UnmanagedPointer ", ": StackByRef ", ": Unverifiable ", ": None ImportCalli not implemented"];

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

    /// <summary>Emits, verifies, loads in a collectible context and runs Main; returns its output.</summary>
    static string Run(Prog p)
    {
        var (pe, pdb) = p.Module.Emit();
        var errors = IlVerify(pe).Where(e => !UnsafeCodes.Any(c => e.Contains(c, StringComparison.Ordinal))).ToList();
        Assert.True(errors.Count == 0, "ILVerify:\n" + string.Join("\n", errors));
        var alc = new AssemblyLoadContext("ffi-" + p.Module.Name, isCollectible: true);
        var output = new StringWriter();
        var old = Console.Out;
        try
        {
            var asm = alc.LoadFromStream(new MemoryStream(pe), new MemoryStream(pdb));
            Console.SetOut(output);
            try { asm.EntryPoint!.Invoke(null, [Array.Empty<string>()]); }
            catch (TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; }
            finally { Console.SetOut(old); }
        }
        finally { alc.Unload(); }
        return output.ToString().Trim().ReplaceLineEndings("\n");
    }

    /// <summary>`try { body } catch (T) { print text }`.</summary>
    static void Rescue<T>(Prog p, Action body, string text) where T : Exception
    {
        p.Il.BeginTry();
        body();
        p.Il.BeginCatch(p.Module.Import(typeof(T)));
        p.Il.EmitPop();
        p.Str(text);
        p.PrintStr();
        p.Il.EndTry();
    }

    // ------------------------------------------------------------------ (a) lib / fun

    [Fact]
    public void String_parameters_cross_as_utf8()
    {
        string lib = Lib("  fun strlen(s : String) : UInt64\n");
        var p = Build("FfiStrlen", lib, p =>
        {
            foreach (var s in new[] { "héllo", "", new string('x', 5000) })   // stack buffer and native buffer
            {
                p.Str(s);
                p.Call("strlen");
                p.Print(typeof(ulong));
            }
        });
        string il = Run(p);
        Assert.Equal("6\n0\n5000", il);
        Assert.Equal(CSharpRun(lib + "puts LibC.strlen(\"héllo\")\nputs LibC.strlen(\"\")\nputs LibC.strlen(\"x\" * 5000)\n"), il);
    }

    [Fact]
    public void Embedded_nul_is_rejected_not_truncated()
    {
        string lib = Lib("  fun strlen(s : String) : UInt64\n");
        var p = Build("FfiNul", lib, p => Rescue<ArgumentException>(p, () => { p.Str("ab\0cd"); p.Call("strlen"); p.Il.EmitPop(); }, "rejected"));
        string il = Run(p);
        Assert.Equal("rejected", il);
        Assert.Equal(CSharpRun(lib + "begin\n  LibC.strlen(\"ab\\0cd\")\nrescue e : ArgumentException\n  puts \"rejected\"\nend\n"), il);
    }

    [Fact]
    public void String_returns_are_borrowed_utf8_and_null_is_nil()
    {
        if (OperatingSystem.IsWindows()) return;
        string lib = Lib("  fun getenv(name : String) : String?\n  fun strerror(code : Int32) : String\n");
        var p = Build("FfiGetenv", lib, p =>
        {
            p.Str("PATH"); p.Call("getenv");
            p.Str("PATH"); p.Il.EmitCall(p.M(typeof(Environment), "GetEnvironmentVariable", typeof(string)));
            p.Il.EmitCall(p.M(typeof(string), "op_Equality", typeof(string), typeof(string)));
            p.PrintBool();
            p.Str("ALICE_FFI_MISSING_42"); p.Call("getenv");
            p.Il.EmitLoadNull(); p.Il.Emit(ILOpCode.Ceq);
            p.PrintBool();
            p.Il.EmitLoadConst(2); p.Call("strerror");
            p.Il.EmitCall(p.Module.Import(typeof(string).GetProperty("Length")!.GetMethod!));
            p.Il.EmitLoadConst(0); p.Il.Emit(ILOpCode.Cgt);
            p.PrintBool();
        });
        string il = Run(p);
        Assert.Equal("true\ntrue\ntrue", il);   // getenv twice and strerror: decoding never frees what C owns
        Assert.Equal(CSharpRun(lib + "puts LibC.getenv(\"PATH\") == Environment.GetEnvironmentVariable(\"PATH\")\nputs LibC.getenv(\"ALICE_FFI_MISSING_42\").nil?\nputs LibC.strerror(2).size > 0\n"), il);
    }

    [Fact]
    public void Non_nullable_string_return_raises_on_null()
    {
        if (OperatingSystem.IsWindows()) return;
        string lib = Lib("  fun getenv(name : String) : String\n");
        var p = Build("FfiNonNull", lib, p => Rescue<NullReferenceException>(p, () => { p.Str("ALICE_FFI_MISSING_42"); p.Call("getenv"); p.Il.EmitPop(); }, "null"));
        string il = Run(p);
        Assert.Equal("null", il);
        Assert.Equal(CSharpRun(lib + "begin\n  LibC.getenv(\"ALICE_FFI_MISSING_42\")\nrescue e : NullReferenceException\n  puts \"null\"\nend\n"), il);
    }

    [Fact]
    public void Nullable_string_parameter_passes_null()
    {
        if (OperatingSystem.IsWindows()) return;
        string lib = Lib("  fun setlocale(category : Int32, locale : String?) : String?\n");
        var p = Build("FfiSetlocale", lib, p =>
        {
            p.Il.EmitLoadConst(6); p.Il.EmitLoadNull(); p.Call("setlocale");
            var isNil = p.Il.DefineLabel(); var done = p.Il.DefineLabel();
            p.Il.EmitBranch(isNil, IlCond.False);
            p.Str("locale"); p.Il.EmitBranch(done);
            p.Il.MarkLabel(isNil); p.Str("nil");
            p.Il.MarkLabel(done); p.PrintStr();
        });
        string il = Run(p);
        Assert.Equal("locale", il);
        Assert.Equal(CSharpRun(lib + "puts LibC.setlocale(6, nil).nil? ? \"nil\" : \"locale\"\n"), il);
    }

    /// <summary>`Array(UInt8).new(n, 0_u8)` on the driver's stack: a local.</summary>
    static IlLocal NewBytes(Prog p, int n)
    {
        var buf = p.Il.DeclareLocal(IlType.Byte.SzArray());
        p.Il.EmitLoadConst(n);
        p.Il.EmitNewArray(IlType.Byte);
        p.Il.EmitStoreLocal(buf);
        return buf;
    }

    static void PrintByteSum(Prog p, IlLocal buf)
    {
        var sum = p.Il.DeclareLocal(IlType.Int32);
        var i = p.Il.DeclareLocal(IlType.Int32);
        var top = p.Il.DefineLabel(); var test = p.Il.DefineLabel();
        p.Il.EmitBranch(test);
        p.Il.MarkLabel(top);
        p.Il.EmitLoadLocal(sum); p.Il.EmitLoadLocal(buf); p.Il.EmitLoadLocal(i); p.Il.EmitLoadElement(IlType.Byte);
        p.Il.Emit(ILOpCode.Add); p.Il.EmitStoreLocal(sum);
        p.Il.EmitLoadLocal(i); p.Il.EmitLoadConst(1); p.Il.Emit(ILOpCode.Add); p.Il.EmitStoreLocal(i);
        p.Il.MarkLabel(test);
        p.Il.EmitLoadLocal(i); p.Il.EmitLoadLocal(buf); p.Il.Emit(ILOpCode.Ldlen); p.Il.Emit(ILOpCode.Conv_i4);
        p.Il.EmitBranch(top, IlCond.Lt);
        p.Il.EmitLoadLocal(sum);
        p.Print(typeof(int));
    }

    [Fact]
    public void Arrays_are_pinned_not_copied()
    {
        string lib = Lib("  fun memset(dst : Array(UInt8), c : Int32, n : UInt64) : Void\n");
        var p = Build("FfiMemset", lib, p =>
        {
            var buf = NewBytes(p, 8);
            p.Il.EmitLoadLocal(buf); p.Il.EmitLoadConst(7); p.Il.EmitLoadConst(8L); p.Call("memset");
            PrintByteSum(p, buf);
            var empty = NewBytes(p, 0);   // empty array: NULL, as C#'s `fixed` gives
            p.Il.EmitLoadLocal(empty); p.Il.EmitLoadConst(7); p.Il.EmitLoadConst(0L); p.Call("memset");
        });
        string il = Run(p);
        Assert.Equal("56", il);
        Assert.Equal(CSharpRun(lib + "buf = Array(UInt8).new(8, 0_u8)\nLibC.memset(buf, 7, 8_u64)\nputs buf.sum\n"), il);
    }

    [Fact]
    public void Imports_are_blittable_pinvoke_rows_with_the_csharp_flags()
    {
        string lib = Lib("  @[Fast]\n  fun strlen(s : String) : UInt64\n  fun getenv(name : String) : String?\n  fun memset(dst : Array(UInt8), c : Int32, n : UInt64) : Void\n  fun abs(x : Int32) : Int32\n  fun isatty(fd : Int32) : Bool\n");
        var p = Build("FfiShapes", lib, p => { });
        var (pe, _) = p.Module.Emit();
        using var reader = new PEReader(new MemoryStream(pe));
        var md = reader.GetMetadataReader();
        var rows = new Dictionary<string, (string Entry, string Module, MethodImportAttributes Flags, MethodDefinition Def)>();
        foreach (var h in md.MethodDefinitions)
        {
            var d = md.GetMethodDefinition(h);
            var imp = d.GetImport();
            if (imp.Module.IsNil) continue;
            Assert.True((d.Attributes & MethodAttributes.PinvokeImpl) != 0);
            Assert.True((d.ImplAttributes & MethodImplAttributes.PreserveSig) != 0);
            Assert.Equal(0, d.RelativeVirtualAddress);   // no body
            rows[md.GetString(d.Name)] = (md.GetString(imp.Name), md.GetString(md.GetModuleReference(imp.Module).Name), imp.Attributes, d);
        }
        Assert.Equal(["__raw_getenv", "__raw_isatty", "__raw_memset", "__raw_strlen", "abs"], rows.Keys.Order());
        Assert.Equal(1, md.GetTableRowCount(TableIndex.ModuleRef));   // one ModuleRef per library
        foreach (var (name, r) in rows)
        {
            Assert.Equal(name.Replace("__raw_", ""), r.Entry);
            Assert.Equal(LibC, r.Module);
            // DllImport(lib, EntryPoint = e, ExactSpelling = true): WinApi, no CharSet, no SetLastError
            Assert.Equal(MethodImportAttributes.ExactSpelling | MethodImportAttributes.CallingConventionWinApi, r.Flags);
            Assert.Equal(name == "abs", (r.Def.Attributes & MethodAttributes.Public) != 0);   // blittable: the import is the public method
        }
        Assert.Equal(0, md.GetTableRowCount(TableIndex.FieldMarshal));   // no MarshalAs anywhere: no runtime marshalling stubs

        string Attrs(MethodDefinition d) => string.Join(",", d.GetCustomAttributes().Select(a =>
        {
            var ctor = md.GetMemberReference((MemberReferenceHandle)md.GetCustomAttribute(a).Constructor);
            return md.GetString(md.GetTypeReference((TypeReferenceHandle)ctor.Parent).Name);
        }));
        Assert.Equal("SuppressGCTransitionAttribute", Attrs(rows["__raw_strlen"].Def));   // @[Fast]
        Assert.Equal("", Attrs(rows["__raw_getenv"].Def));

        // raw signatures: byte* for strings, T* for arrays, byte for Bool (what Emitter.cs declares as `__raw_x`)
        Assert.Equal("UInt64(Byte*)", SigText.Decode(md, rows["__raw_strlen"].Def));
        Assert.Equal("Byte*(Byte*)", SigText.Decode(md, rows["__raw_getenv"].Def));
        Assert.Equal("Void(Byte*,Int32,UInt64)", SigText.Decode(md, rows["__raw_memset"].Def));
        Assert.Equal("Byte(Int32)", SigText.Decode(md, rows["__raw_isatty"].Def));

        // and the C# backend writes the same raw imports
        var cs = AliceCompiler.Transpile(lib);
        Assert.Contains("private static extern ulong __raw_strlen(byte* s);", cs);
        Assert.Contains("private static extern byte* __raw_getenv(byte* name);", cs);
        Assert.Contains("private static extern void __raw_memset(byte* dst, int c, ulong n);", cs);
        Assert.Contains("public static extern int abs(int x);", cs);
    }

    /// <summary>A method signature as `Ret(P1,P2)` with primitive and pointer names.</summary>
    static class SigText
    {
        sealed class Names : ISignatureTypeProvider<string, object?>
        {
            public string GetPrimitiveType(PrimitiveTypeCode t) => t.ToString();
            public string GetPointerType(string e) => e + "*";
            public string GetSZArrayType(string e) => e + "[]";
            public string GetByReferenceType(string e) => e + "&";
            public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => r.GetString(r.GetTypeDefinition(h).Name);
            public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) => r.GetString(r.GetTypeReference(h).Name);
            public string GetTypeFromSpecification(MetadataReader r, object? c, TypeSpecificationHandle h, byte k) => "spec";
            public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
            public string GetGenericInstantiation(string g, System.Collections.Immutable.ImmutableArray<string> a) => g;
            public string GetArrayType(string e, ArrayShape s) => e + "[,]";
            public string GetGenericMethodParameter(object? c, int i) => "!!" + i;
            public string GetGenericTypeParameter(object? c, int i) => "!" + i;
            public string GetModifiedType(string m, string u, bool r) => u;
            public string GetPinnedType(string e) => e;
        }

        public static string Decode(MetadataReader md, MethodDefinition d)
        {
            var s = d.DecodeSignature(new Names(), null);
            return s.ReturnType + "(" + string.Join(",", s.ParameterTypes) + ")";
        }
    }

    // ------------------------------------------------------------------ (d) unsafe pointers

    const string Malloc = "  fun malloc(n : UInt64) : Pointer(Int32)\n  fun free(p : Pointer(Int32)) : Void\n";

    [Fact]
    public void Pointer_reads_writes_and_arithmetic()
    {
        string lib = Lib(Malloc);
        var p = Build("FfiPointers", lib, p =>
        {
            var il = p.Il;
            var ptr = il.DeclareLocal(IlType.Int32.Pointer(), "p");
            il.EmitLoadConst(16L); p.Call("malloc"); il.EmitStoreLocal(ptr);
            // p[0] = 41; p[1] = 1
            il.EmitLoadLocal(ptr); il.EmitLoadConst(41); AirToIlFfi.EmitStoreIndirect(il, IlType.Int32);
            il.EmitLoadLocal(ptr); il.EmitLoadConst(1); AirToIlFfi.EmitElementAddress(il, IlType.Int32); il.EmitLoadConst(1); AirToIlFfi.EmitStoreIndirect(il, IlType.Int32);
            // puts p[0] + p[1]
            il.EmitLoadLocal(ptr); AirToIlFfi.EmitLoadIndirect(il, IlType.Int32);
            il.EmitLoadLocal(ptr); il.EmitLoadConst(1); AirToIlFfi.EmitElementAddress(il, IlType.Int32); AirToIlFfi.EmitLoadIndirect(il, IlType.Int32);
            il.Emit(ILOpCode.Add); p.Print(typeof(int));
            // (p + 3) - p == 3, sizeof(Int32) == 4, sizeof(Int64) == 8 and sizeof of a struct via the opcode
            il.EmitLoadLocal(ptr); il.EmitLoadConst(3); AirToIlFfi.EmitElementAddress(il, IlType.Int32);
            il.EmitLoadLocal(ptr); AirToIlFfi.EmitPointerDifference(il, IlType.Int32); p.Print(typeof(long));
            AirToIlFfi.EmitSizeOf(il, IlType.Int32); p.Print(typeof(int));
            AirToIlFfi.EmitSizeOf(il, p.Module.Import(typeof(Guid))); p.Print(typeof(int));
            il.EmitLoadLocal(ptr); p.Call("free");
        });
        Assert.Equal("42\n3\n4\n16", Run(p));
        Assert.Equal("42", CSharpRun(lib + "unsafe\n  p = LibC.malloc(16_u64)\n  p[0] = 41\n  p[1] = 1\n  puts p[0] + p[1]\n  LibC.free(p)\nend\n"));
    }

    // ------------------------------------------------------------------ (b) callbacks

    const string Qsort = "  fun qsort(base : Array(Int32), n : UInt64, size : UInt64, cmp : Callback(Pointer(Int32), Pointer(Int32), Int32)) : Void\n";

    /// <summary>`->(a : Pointer(Int32), b : Pointer(Int32)) { a[0] &lt;=&gt; b[0] }` lifted: the managed body method.</summary>
    static IlMethodDef CompareBody(Prog p)
    {
        var b = p.Program.DefineMethod("__cb_body_cmp", MethodAttributes.Private | MethodAttributes.Static, IlType.Int32, [IlType.Int32.Pointer(), IlType.Int32.Pointer()]);
        var il = b.Il;
        var a0 = il.DeclareLocal(IlType.Int32);
        il.EmitLoadArg(0); AirToIlFfi.EmitLoadIndirect(il, IlType.Int32); il.EmitStoreLocal(a0);
        il.EmitLoadLocalAddress(a0);
        il.EmitLoadArg(1); AirToIlFfi.EmitLoadIndirect(il, IlType.Int32);
        il.EmitCall(p.M(typeof(int), "CompareTo", typeof(int)));
        il.EmitReturn();
        return b;
    }

    static IlLocal IntArray(Prog p, params int[] xs)
    {
        var a = p.Il.DeclareLocal(IlType.Int32.SzArray());
        p.Il.EmitLoadConst(xs.Length); p.Il.EmitNewArray(IlType.Int32); p.Il.EmitStoreLocal(a);
        for (int i = 0; i < xs.Length; i++) { p.Il.EmitLoadLocal(a); p.Il.EmitLoadConst(i); p.Il.EmitLoadConst(xs[i]); p.Il.EmitStoreElement(IlType.Int32); }
        return a;
    }

    [Fact]
    public void Lambdas_become_unmanaged_callers_only_function_pointers()
    {
        string lib = Lib(Qsort);
        IlMethodDef? uco = null;
        var p = Build("FfiQsort", lib, p =>
        {
            var xs = IntArray(p, 5, 3, 9, 1);
            uco = p.Ffi.EmitCallback(p.Program, CompareBody(p));
            p.Il.EmitLoadLocal(xs); p.Il.EmitLoadConst(4L); p.Il.EmitLoadConst(4L);
            p.Ffi.EmitLoadCallback(p.Il, uco);
            p.Call("qsort");
            // xs.join(","): string.Join<int>(",", xs)
            p.Str(","); p.Il.EmitLoadLocal(xs);
            p.Il.EmitCall(p.Module.Import(typeof(string).GetMethods().Single(m => m.Name == "Join" && m.IsGenericMethodDefinition
                && m.GetParameters() is [{ ParameterType: var s }, { ParameterType: { IsGenericType: true } e }] && s == typeof(string)
                && e.GetGenericTypeDefinition() == typeof(IEnumerable<>)).MakeGenericMethod(typeof(int))));
            p.PrintStr();
        });
        string il = Run(p);
        Assert.Equal("1,3,5,9", il);
        Assert.Equal(CSharpRun(lib + "xs = [5, 3, 9, 1]\nLibC.qsort(xs, 4_u64, 4_u64, ->(a : Pointer(Int32), b : Pointer(Int32)) { a[0] <=> b[0] })\nputs xs.join(\",\")\n"), il);

        // the trampoline: [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })], the import takes a cdecl fnptr
        var (pe, _) = p.Module.Emit();
        using var reader = new PEReader(new MemoryStream(pe));
        var md = reader.GetMetadataReader();
        var cb = md.MethodDefinitions.Select(md.GetMethodDefinition).Single(d => md.GetString(d.Name) == uco!.Name);
        var attr = md.GetCustomAttribute(cb.GetCustomAttributes().Single());
        var ctor = md.GetMemberReference((MemberReferenceHandle)attr.Constructor);
        Assert.Equal("UnmanagedCallersOnlyAttribute", md.GetString(md.GetTypeReference((TypeReferenceHandle)ctor.Parent).Name));
        var value = attr.DecodeValue(new AttrTypes());
        var named = Assert.Single(value.NamedArguments);
        Assert.Equal("CallConvs", named.Name);
        var arr = (System.Collections.Immutable.ImmutableArray<CustomAttributeTypedArgument<string>>)named.Value!;
        Assert.Equal("System.Runtime.CompilerServices.CallConvCdecl", Assert.Single(arr).Value);
        var raw = md.MethodDefinitions.Select(md.GetMethodDefinition).Single(d => md.GetString(d.Name) == "__raw_qsort");
        var sig = raw.DecodeSignature(new FnPtrConv(), null);
        Assert.Equal("CDecl", sig.ParameterTypes[3]);
        Assert.Contains("delegate* unmanaged[Cdecl]<int*, int*, int>", AliceCompiler.Transpile(lib + "xs = [5, 3, 9, 1]\nLibC.qsort(xs, 4_u64, 4_u64, ->(a : Pointer(Int32), b : Pointer(Int32)) { a[0] <=> b[0] })\n"));
    }

    sealed class AttrTypes : ICustomAttributeTypeProvider<string>
    {
        public string GetPrimitiveType(PrimitiveTypeCode t) => t.ToString();
        public string GetSystemType() => "Type";
        public string GetSZArrayType(string e) => e + "[]";
        public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => "def";
        public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) => "ref";
        public string GetTypeFromSerializedName(string name) => name;
        public PrimitiveTypeCode GetUnderlyingEnumType(string t) => PrimitiveTypeCode.Int32;
        public bool IsSystemType(string t) => t == "Type";
    }

    sealed class FnPtrConv : ISignatureTypeProvider<string, object?>
    {
        public string GetPrimitiveType(PrimitiveTypeCode t) => t.ToString();
        public string GetPointerType(string e) => e + "*";
        public string GetSZArrayType(string e) => e + "[]";
        public string GetByReferenceType(string e) => e + "&";
        public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => "def";
        public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) => "ref";
        public string GetTypeFromSpecification(MetadataReader r, object? c, TypeSpecificationHandle h, byte k) => "spec";
        public string GetFunctionPointerType(MethodSignature<string> s) => s.Header.CallingConvention.ToString();
        public string GetGenericInstantiation(string g, System.Collections.Immutable.ImmutableArray<string> a) => g;
        public string GetArrayType(string e, ArrayShape s) => e;
        public string GetGenericMethodParameter(object? c, int i) => "";
        public string GetGenericTypeParameter(object? c, int i) => "";
        public string GetModifiedType(string m, string u, bool r) => u;
        public string GetPinnedType(string e) => e;
    }

    [Fact]
    public void Exception_in_a_callback_surfaces_after_the_c_call()
    {
        string lib = Lib(Qsort);
        var p = Build("FfiQsortThrow", lib, p =>
        {
            var body = p.Program.DefineMethod("__cb_body_boom", MethodAttributes.Private | MethodAttributes.Static, IlType.Int32, [IlType.Int32.Pointer(), IlType.Int32.Pointer()]);
            body.Il.EmitLoadConst("boom");
            body.Il.EmitNewObj(p.Module.Import(typeof(Exception).GetConstructor([typeof(string)])!));
            body.Il.EmitThrow();
            var uco = p.Ffi.EmitCallback(p.Program, body);
            var xs = IntArray(p, 2, 1, 3);
            p.Il.BeginTry();
            p.Il.EmitLoadLocal(xs); p.Il.EmitLoadConst(3L); p.Il.EmitLoadConst(4L);
            p.Ffi.EmitLoadCallback(p.Il, uco);
            p.Call("qsort");
            p.Il.BeginCatch(p.Module.Import(typeof(Exception)));
            p.Il.EmitCall(p.Module.Import(typeof(Exception).GetProperty("Message")!.GetMethod!), virtualCall: true);
            p.PrintStr();
            p.Il.EndTry();
            p.Str("alive"); p.PrintStr();
        });
        string il = Run(p);
        Assert.Equal("boom\nalive", il);
        Assert.Equal(CSharpRun(lib + "xs = [2, 1, 3]\nbegin\n  LibC.qsort(xs, 3_u64, 4_u64, ->(a : Pointer(Int32), b : Pointer(Int32)) { raise \"boom\" })\nrescue e\n  puts e.message\nend\nputs \"alive\"\n"), il);
    }

    // ------------------------------------------------------------------ out / ref structs

    const string TimespecAl = "struct Timespec\n  getter sec : Int64\n  getter nsec : Int64\n  def initialize(@sec : Int64, @nsec : Int64)\n  end\nend\n";

    /// <summary>The struct as Backend/Emitter.cs declares it: sequential, two Int64 fields.</summary>
    static void Timespec(Prog p)
    {
        var t = p.Module.DefineType("", "Timespec", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.SequentialLayout | TypeAttributes.BeforeFieldInit,
            p.Module.Import(typeof(ValueType)));
        t.DefineField("sec", IlType.Int64, FieldAttributes.Public);
        t.DefineField("nsec", IlType.Int64, FieldAttributes.Public);
        p.Structs["Timespec"] = t;
    }

    [Fact]
    public void Out_and_ref_struct_parameters_need_no_pointers()
    {
        if (OperatingSystem.IsWindows()) return;   // clock_gettime / timespec layout: Unix (64-bit time_t)
        string lib = Lib("  fun clock_gettime(clock : Int32, out ts : Timespec) : Int32\n  fun frexp(x : Float64, ref exp : Int32) : Float64\n");
        var p = Build("FfiOutRef", TimespecAl + lib, p =>
        {
            var il = p.Il;
            var t = p.Structs["Timespec"];
            var ts = il.DeclareLocal(t.Type, "ts");
            il.EmitLoadConst(0); il.EmitLoadLocalAddress(ts); p.Call("clock_gettime"); p.Print(typeof(int));
            il.EmitLoadLocal(ts); il.EmitLoadField(t.Fields[0]); il.EmitLoadConst(1_600_000_000L); il.Emit(ILOpCode.Cgt); p.PrintBool();
            il.EmitLoadLocal(ts); il.EmitLoadField(t.Fields[1]); il.EmitLoadConst(1_000_000_000L); il.Emit(ILOpCode.Clt); p.PrintBool();
            var e = il.DeclareLocal(IlType.Int32, "e");
            var m = il.DeclareLocal(IlType.Double, "m");
            il.EmitLoadConst(8.0); il.EmitLoadLocalAddress(e); p.Call("frexp"); il.EmitStoreLocal(m);
            // "#{m} #{e}"
            il.EmitLoadLocalAddress(m); il.EmitCall(p.Module.Import(typeof(double).GetMethod("ToString", Type.EmptyTypes)!));
            p.Str(" ");
            il.EmitLoadLocalAddress(e); il.EmitCall(p.Module.Import(typeof(int).GetMethod("ToString", Type.EmptyTypes)!));
            il.EmitCall(p.M(typeof(string), "Concat", typeof(string), typeof(string), typeof(string)));
            p.PrintStr();
        }, Timespec);
        string il = Run(p);
        Assert.Equal("0\ntrue\ntrue\n0.5 4", il);
        Assert.Equal(CSharpRun(TimespecAl + lib + "rc = LibC.clock_gettime(0, out ts)\nputs rc\nputs ts.sec > 1_600_000_000_i64\nputs ts.nsec < 1_000_000_000_i64\ne = 0\nm = LibC.frexp(8.0, ref e)\nputs \"#{m} #{e}\"\n"), il);

        // the wrapper takes `out Timespec` / `ref int` (byrefs, [Out] on the out parameter); the import takes pointers
        var (pe, _) = p.Module.Emit();
        using var reader = new PEReader(new MemoryStream(pe));
        var md = reader.GetMetadataReader();
        var defs = md.MethodDefinitions.Select(md.GetMethodDefinition).ToDictionary(d => md.GetString(d.Name));
        Assert.Equal("Int32(Int32,Timespec&)", SigText.Decode(md, defs["clock_gettime"]));
        Assert.Equal("Int32(Int32,Timespec*)", SigText.Decode(md, defs["__raw_clock_gettime"]));
        Assert.Equal("Double(Double,Int32*)", SigText.Decode(md, defs["__raw_frexp"]));
        var outParam = defs["clock_gettime"].GetParameters().Select(md.GetParameter).Single(x => x.SequenceNumber == 2);
        Assert.True((outParam.Attributes & ParameterAttributes.Out) != 0);
    }

    // ------------------------------------------------------------------ (c) @[Native]

    const string NativeSrc = """
        @[Native]
        def dot(a : Array(Float64), b : Array(Float64)) : Float64
          s = 0.0
          for i in 0...a.size
            s += a[i] * b[i]
          end
          s
        end

        @[Native]
        def mad(a : Float64, b : Float64, c : Float64) : Float64
          a * b + c
        end
        """;

    /// <summary>Emits the two @[Native] functions with a managed fallback that returns -1 (so the test sees which
    /// branch ran); `withLib` builds the C library with clang first.</summary>
    static (Prog P, string Lib) NativeProg(string name, bool withLib)
    {
        string libName = name + "_native";
        var p = Build(name, NativeSrc, p =>
        {
            var c = new CEmitter();
            foreach (var d in p.Asts[0].Items.OfType<MethodDecl>())
            {
                var m = (MethodSym)d.Sym!;
                var ps = m.Params.Select(x => x.Type.Cs == "double[]" ? IlType.Double.SzArray() : IlType.Double).ToArray();
                var managed = p.Program.DefineMethod(AirToIlFfi.NativeManagedName(m), MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig, IlType.Double, ps);
                managed.Il.EmitLoadConst(-1.0);
                managed.Il.EmitReturn();
                p.Ffi.EmitNative(p.Program, d, m, managed, c, "main.al", libName);
            }
            if (withLib)
            {
                var dir = Directory.CreateTempSubdirectory("alice-ilffi-native-").FullName;
                Assert.True(AliceCompiler.BuildNativeLibrary(c.Source, dir, libName), "clang");
                Alice.Runtime.NativeLibs.AddSearchPath(dir);
            }
            var il = p.Il;
            var a = il.DeclareLocal(IlType.Double.SzArray());
            var b = il.DeclareLocal(IlType.Double.SzArray());
            foreach (var (arr, vals) in new[] { (a, new[] { 1.0, 2.0, 3.0 }), (b, new[] { 4.0, 5.0, 6.0 }) })
            {
                il.EmitLoadConst(3); il.EmitNewArray(IlType.Double); il.EmitStoreLocal(arr);
                for (int i = 0; i < 3; i++) { il.EmitLoadLocal(arr); il.EmitLoadConst(i); il.EmitLoadConst(vals[i]); il.EmitStoreElement(IlType.Double); }
            }
            var dot = p.Program.Methods.Single(x => x.Name == "dot");
            var mad = p.Program.Methods.Single(x => x.Name == "mad");
            il.EmitLoadLocal(a); il.EmitLoadLocal(b); il.EmitCall(dot); p.Print(typeof(double));
            il.EmitLoadLocal(a); il.EmitLoadLocal(a); il.EmitCall(dot); p.Print(typeof(double));   // aliased: managed
            il.EmitLoadConst(2.0); il.EmitLoadConst(3.0); il.EmitLoadConst(1.0); il.EmitCall(mad); p.Print(typeof(double));
        });
        return (p, libName);
    }

    [Fact]
    public void Native_functions_calli_through_the_export_or_fall_back()
    {
        var (withLib, _) = NativeProg("FfiNat" + Guid.NewGuid().ToString("N")[..8], withLib: true);
        Assert.Equal("32\n-1\n7", Run(withLib));   // C ran dot and mad; dot(a, a) took the managed branch
        var (noLib, _) = NativeProg("FfiNoNat" + Guid.NewGuid().ToString("N")[..8], withLib: false);
        Assert.Equal("-1\n-1\n-1", Run(noLib));    // no library: every call is the managed fallback

        // the C# backend's @[Native] program prints the same values for the native branch
        var src = NativeSrc + "\nputs dot([1.0, 2.0, 3.0], [4.0, 5.0, 6.0])\nputs mad(2.0, 3.0, 1.0)\n";
        var nb = AliceCompiler.CompileNative(src, "ffinat" + Guid.NewGuid().ToString("N")[..8], true);
        Assert.NotNull(nb.NativeDir);

        // fnptr shapes: dot has a loop (GC transition kept), mad is straight-line (modopt SuppressGCTransition)
        var (pe, _) = withLib.Module.Emit();
        using var reader = new PEReader(new MemoryStream(pe));
        var md = reader.GetMetadataReader();
        var fields = md.FieldDefinitions.Select(md.GetFieldDefinition)
            .ToDictionary(f => md.GetString(md.GetTypeDefinition(f.GetDeclaringType()).Name), f => f.DecodeSignature(new FnPtrMods(), null));
        Assert.Equal("Unmanaged Double(Double*,Int32,Double*,Int32)", fields["__np_dot"]);
        Assert.Equal("Unmanaged Double+modopt(CallConvSuppressGCTransition)(Double,Double,Double)", fields["__np_mad"]);
        Assert.Contains("delegate* unmanaged[SuppressGCTransition]<double, double, double, double>", AliceCompiler.Transpile(NativeSrc));
    }

    sealed class FnPtrMods : ISignatureTypeProvider<string, object?>
    {
        public string GetPrimitiveType(PrimitiveTypeCode t) => t.ToString();
        public string GetPointerType(string e) => e + "*";
        public string GetSZArrayType(string e) => e + "[]";
        public string GetByReferenceType(string e) => e + "&";
        public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => r.GetString(r.GetTypeDefinition(h).Name);
        public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) => r.GetString(r.GetTypeReference(h).Name);
        public string GetTypeFromSpecification(MetadataReader r, object? c, TypeSpecificationHandle h, byte k) => "spec";
        public string GetFunctionPointerType(MethodSignature<string> s) =>
            s.Header.CallingConvention + " " + s.ReturnType + "(" + string.Join(",", s.ParameterTypes) + ")";
        public string GetGenericInstantiation(string g, System.Collections.Immutable.ImmutableArray<string> a) => g;
        public string GetArrayType(string e, ArrayShape s) => e;
        public string GetGenericMethodParameter(object? c, int i) => "";
        public string GetGenericTypeParameter(object? c, int i) => "";
        public string GetModifiedType(string m, string u, bool r) => u + (r ? "+modreq(" : "+modopt(") + m + ")";
        public string GetPinnedType(string e) => e;
    }

    // ------------------------------------------------------------------ a real process, and NativeAOT

    /// <summary>One program over every FFI shape: strings both ways, a pinned array, a callback, out/ref, pointers.</summary>
    internal static Prog Everything(string name)
    {
        string lib = Lib("  fun strlen(s : String) : UInt64\n  fun getenv(name : String) : String?\n  fun memset(dst : Array(UInt8), c : Int32, n : UInt64) : Void\n"
            + Qsort + Malloc + "  fun clock_gettime(clock : Int32, out ts : Timespec) : Int32\n");
        return Build(name, TimespecAl + lib, p =>
        {
            var il = p.Il;
            p.Str("héllo"); p.Call("strlen"); p.Print(typeof(ulong));
            p.Str(new string('x', 300)); p.Call("strlen"); p.Print(typeof(ulong));
            p.Str("ALICE_FFI_MISSING_42"); p.Call("getenv"); il.EmitLoadNull(); il.Emit(ILOpCode.Ceq); p.PrintBool();
            var buf = NewBytes(p, 8);
            il.EmitLoadLocal(buf); il.EmitLoadConst(7); il.EmitLoadConst(8L); p.Call("memset");
            PrintByteSum(p, buf);
            var xs = IntArray(p, 5, 3, 9, 1);
            var uco = p.Ffi.EmitCallback(p.Program, CompareBody(p));
            il.EmitLoadLocal(xs); il.EmitLoadConst(4L); il.EmitLoadConst(4L); p.Ffi.EmitLoadCallback(il, uco); p.Call("qsort");
            for (int i = 0; i < 4; i++) { il.EmitLoadLocal(xs); il.EmitLoadConst(i); il.EmitLoadElement(IlType.Int32); p.Print(typeof(int)); }
            var t = p.Structs["Timespec"];
            var ts = il.DeclareLocal(t.Type);
            il.EmitLoadConst(0); il.EmitLoadLocalAddress(ts); p.Call("clock_gettime"); p.Print(typeof(int));
            il.EmitLoadLocal(ts); il.EmitLoadField(t.Fields[0]); il.EmitLoadConst(1_600_000_000L); il.Emit(ILOpCode.Cgt); p.PrintBool();
            var ptr = il.DeclareLocal(IlType.Int32.Pointer());
            il.EmitLoadConst(16L); p.Call("malloc"); il.EmitStoreLocal(ptr);
            il.EmitLoadLocal(ptr); il.EmitLoadConst(41); AirToIlFfi.EmitStoreIndirect(il, IlType.Int32);
            il.EmitLoadLocal(ptr); il.EmitLoadConst(1); AirToIlFfi.EmitElementAddress(il, IlType.Int32); il.EmitLoadConst(1); AirToIlFfi.EmitStoreIndirect(il, IlType.Int32);
            il.EmitLoadLocal(ptr); AirToIlFfi.EmitLoadIndirect(il, IlType.Int32);
            il.EmitLoadLocal(ptr); il.EmitLoadConst(1); AirToIlFfi.EmitElementAddress(il, IlType.Int32); AirToIlFfi.EmitLoadIndirect(il, IlType.Int32);
            il.Emit(ILOpCode.Add); p.Print(typeof(int));
            il.EmitLoadLocal(ptr); p.Call("free");
        }, Timespec);
    }

    internal const string EverythingOut = "6\n300\ntrue\n56\n1\n3\n5\n9\n0\ntrue\n42";

    [Fact]
    public void Ffi_program_runs_as_a_process()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal(EverythingOut, Run(Everything("FfiAll")));
        var dir = Directory.CreateTempSubdirectory("alice-ilffi-").FullName;
        try
        {
            var (pe, _) = Everything("FfiAll").Module.Emit();
            File.WriteAllBytes(Path.Combine(dir, "FfiAll.dll"), pe);
            File.Copy(typeof(Alice.Runtime.Utf8Arg).Assembly.Location, Path.Combine(dir, "Alice.Runtime.dll"));
            File.WriteAllText(Path.Combine(dir, "FfiAll.runtimeconfig.json"),
                "{\"runtimeOptions\":{\"tfm\":\"net" + Environment.Version.Major + ".0\",\"framework\":{\"name\":\"Microsoft.NETCore.App\",\"version\":\""
                + Environment.Version.Major + ".0.0\"}}}");
            var (code, stdout, stderr) = Exec("dotnet", [Path.Combine(dir, "FfiAll.dll")], dir);
            Assert.True(code == 0, stderr);
            Assert.Equal(EverythingOut, stdout.Trim().ReplaceLineEndings("\n"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    internal static (int Code, string Out, string Err) Exec(string file, string[] args, string cwd, int timeoutMs = 60_000)
    {
        var psi = new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = cwd };
        psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        using var p = Process.Start(psi)!;
        var so = p.StandardOutput.ReadToEndAsync();
        var se = p.StandardError.ReadToEndAsync();
        Assert.True(p.WaitForExit(timeoutMs), file + " timed out");
        return (p.ExitCode, so.Result, se.Result);
    }
}

/// <summary>D1.10: NativeAOT publish of an IL-emitted FFI program. In the `msbuild` collection with the other publish
/// tests (TrimAotTests), so no two publishes run at once.</summary>
[Trait("Category", "Integration")]
[Collection("msbuild")]
public class AirToIlFfiAotTests
{
    /// <summary>
    /// NativeAOT compiles the emitted IL (ImplMap P/Invokes become direct calls, the UnmanagedCallersOnly trampoline a
    /// native entry, calli a native indirect call). ILC needs a project, so a two-line C# host references the
    /// emitted dll and calls its Main.
    /// </summary>
    [Fact]
    public void Ffi_program_publishes_with_native_aot()
    {
        if (!OperatingSystem.IsLinux()) return;
        var root = Directory.CreateTempSubdirectory("alice-ilffi-aot-").FullName;
        try
        {
            var (pe, _) = AirToIlFfiTests.Everything("FfiAot").Module.Emit();
            string dll = Path.Combine(root, "FfiAot.dll");
            File.WriteAllBytes(dll, pe);
            string runtime = typeof(Alice.Runtime.Utf8Arg).Assembly.Location;
            File.WriteAllText(Path.Combine(root, "host.csproj"), $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net{Environment.Version.Major}.0</TargetFramework>
                    <PublishAot>true</PublishAot>
                    <InvariantGlobalization>true</InvariantGlobalization>
                    <ImplicitUsings>disable</ImplicitUsings>
                  </PropertyGroup>
                  <ItemGroup>
                    <Reference Include="FfiAot"><HintPath>{dll}</HintPath></Reference>
                    <Reference Include="Alice.Runtime"><HintPath>{runtime}</HintPath></Reference>
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(root, "Host.cs"), "static class Host { static void Main(string[] a) => Program.Main(a); }\n");
            var (code, stdout, stderr) = AirToIlFfiTests.Exec("dotnet", ["publish", "host.csproj", "-c", "Release", "-r", "linux-x64", "-o", "out", "-nologo", "-v", "m"], root, 600_000);
            Assert.True(code == 0, stdout + stderr);
            Assert.DoesNotContain("warning IL3", stdout);   // no AOT warnings about the emitted code
            Assert.False(File.Exists(Path.Combine(root, "out", "FfiAot.dll")));   // compiled into the native executable
            Assert.True(File.Exists(Path.Combine(root, "out", "host.dbg")));
            var (rc, output, err) = AirToIlFfiTests.Exec(Path.Combine(root, "out", "host"), [], root);
            Assert.True(rc == 0, err);
            Assert.Equal(AirToIlFfiTests.EverythingOut, output.Trim().ReplaceLineEndings("\n"));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
