using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using Alice.Compiler;
using Alice.Compiler.Backend.Il;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;
using Alice.Runtime;
using Alice.Tests.Integration;
using Binder = Alice.Compiler.Semantics.Binder;

namespace Alice.Tests;

/// <summary>Review round 1, F5 and F17: callback exceptions surface from the C call during which they were raised,
/// whichever `fun` of the lib made it; a raise with no Alice call to rethrow into fails fast.</summary>
[Collection("console")]
public class FfiCallbackR1Tests
{
    const string CSource = """
        #include <pthread.h>
        typedef int (*cb_t)(int);
        static cb_t g_cb;
        void set_cb(cb_t f) { g_cb = f; }
        int fire(int x) { return g_cb ? g_cb(x) : -1; }
        int fire2(int x) { return g_cb(x) + g_cb(x + 1); }
        static void* thr(void* a) { long r = g_cb((int)(long)a); return (void*)r; }
        int fire_thread(int x) { pthread_t t; void* r; pthread_create(&t, 0, thr, (void*)(long)x); pthread_join(t, &r); return (int)(long)r; }
        """;

    static string? s_lib;

    /// <summary>libcbl_r1.so: `set_cb` stores the pointer, `fire`/`fire2`/`fire_thread` (no callback parameter) call it.</summary>
    static string? CblLib()
    {
        if (!OperatingSystem.IsLinux()) return null;   // pthread + .so naming
        if (s_lib != null) return s_lib;
        var dir = Directory.CreateTempSubdirectory("alice-cbl-r1-").FullName;
        return AliceCompiler.BuildNativeLibrary(CSource, dir, "cbl_r1") ? s_lib = Path.Combine(dir, "libcbl_r1.so") : null;
    }

    static string Lib(string path) => $"@[Link(\"{path}\")]\nlib Cbl\n  fun set_cb(f : Callback(Int32, Int32)) : Void\n  fun fire(x : Int32) : Int32\n  fun fire2(x : Int32) : Int32\n  fun fire_thread(x : Int32) : Int32\nend\n";

    [Fact]
    public void A_stored_callback_raising_surfaces_from_the_fun_that_ran_it()
    {
        if (CblLib() is not { } lib) return;
        var src = Lib(lib) + """
            Cbl.set_cb(->(x : Int32) { raise "boom from fire" if x > 0; x })
            begin
              puts Cbl.fire(5)
            rescue e
              puts "fire raised: #{e.message}"
            end
            Cbl.set_cb(->(x : Int32) { x })
            puts "set_cb ok"
            puts Cbl.fire(7)
            """;
        Assert.Equal("fire raised: boom from fire\nset_cb ok\n7", AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Two_raises_in_one_c_call_surface_the_first()
    {
        if (CblLib() is not { } lib) return;
        var src = Lib(lib) + """
            Cbl.set_cb(->(x : Int32) { raise "boom #{x}" })
            begin
              puts Cbl.fire2(1)
            rescue e
              puts e.message
            end
            Cbl.set_cb(->(x : Int32) { x })
            puts Cbl.fire(3)
            """;
        Assert.Equal("boom 1\n3", AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n"));
    }

    [Fact]
    public void A_raise_on_a_thread_created_by_c_fails_fast()
    {
        if (CblLib() is not { } lib) return;
        var src = Lib(lib) + """
            Cbl.set_cb(->(x : Int32) { raise "boom on thread" })
            puts Cbl.fire_thread(1)
            puts "after fire_thread: alive"
            """;
        var dir = Directory.CreateTempSubdirectory("alice-cbl-r1-run-").FullName;
        File.WriteAllBytes(Path.Combine(dir, "cbthread.dll"), AliceCompiler.Emit([AliceCompiler.Transpile(src)], "cbthread", exe: true));
        File.Copy(typeof(NativeCallback).Assembly.Location, Path.Combine(dir, "Alice.Runtime.dll"));
        File.WriteAllText(Path.Combine(dir, "cbthread.runtimeconfig.json"),
            "{\"runtimeOptions\":{\"tfm\":\"net10.0\",\"framework\":{\"name\":\"Microsoft.NETCore.App\",\"version\":\""
            + $"{Environment.Version.Major}.{Environment.Version.Minor}.0" + "\"}}}");
        var psi = new ProcessStartInfo("dotnet") { WorkingDirectory = dir };
        psi.ArgumentList.Add("cbthread.dll");
        var (code, output) = ChildProcess.Run(psi, timeoutMinutes: 2);
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        Assert.NotEqual(0, code);
        Assert.Contains("boom on thread", output);
        Assert.Contains("cannot be rethrown", output);
        Assert.DoesNotContain("alive", output);
    }

    [Fact]
    public void Runtime_keeps_every_raise_and_restores_the_depth()
    {
        int d = NativeCallback.Enter();
        NativeCallback.Fail(new InvalidOperationException("a"));
        NativeCallback.Fail(new ArgumentException("b"));
        NativeCallback.Fail(new ArgumentException("c"));
        var e = Assert.Throws<InvalidOperationException>(() => NativeCallback.Rethrow(d));
        Assert.Equal("a", e.Message);
        Assert.Equal(["b", "c"], ((Exception[])e.Data[NativeCallback.SuppressedKey]!).Select(x => x.Message));
        d = NativeCallback.Enter();
        NativeCallback.Fail(new InvalidOperationException("one"));
        Assert.Equal("one", Assert.Throws<InvalidOperationException>(() => NativeCallback.Rethrow(d)).Message);
        NativeCallback.Rethrow(d);   // drained
    }

    static (IlModule Module, AirToIlFfi Ffi, TypeSym Lib, LibDecl Decl) IlLib(string src)
    {
        var asts = new List<FileNode> { Parser.ParseFile(src, "main.al") };
        var binder = new Binder(asts, new ClrTypes(null));
        binder.Bind();
        var module = new IlModule("cbl_r1");
        var ffi = new AirToIlFfi(module);
        var l = asts[0].Items.OfType<LibDecl>().Single();
        ffi.EmitLib(l);
        return (module, ffi, (TypeSym)l.Sym!, l);
    }

    [Fact]
    public void Il_wraps_every_fun_of_a_lib_with_callbacks()
    {
        var (module, ffi, s, _) = IlLib(Lib("libcbl_r1.so"));
        var fire = s.Methods["fire"][0];
        Assert.NotSame(ffi.Imports[fire], ffi.Funs[fire]);   // a wrapper, not the bare import
        var text = IlDisassembler.Disassemble(module);
        int at = text.IndexOf("fire(", StringComparison.Ordinal);
        Assert.True(at >= 0, text);
        Assert.Contains("Enter", text[at..]);
        Assert.Contains("Rethrow", text[at..]);
    }

    [Fact]
    public void Il_leaves_a_lib_without_callbacks_direct()
    {
        var (_, ffi, s, _) = IlLib("@[Link(\"libm.so.6\")]\nlib M\n  fun cos(x : Float64) : Float64\nend\n");
        var cos = s.Methods["cos"][0];
        Assert.Same(ffi.Imports[cos], ffi.Funs[cos]);
    }

    [Fact]
    public void Csharp_wraps_every_fun_of_a_lib_with_callbacks()
    {
        var cs = AliceCompiler.Transpile(Lib("libcbl_r1.so") + "puts 1\n");
        int at = cs.IndexOf("public static int fire(", StringComparison.Ordinal);
        Assert.True(at >= 0, cs);
        var body = cs[at..cs.IndexOf('}', at)];
        Assert.Contains("NativeCallback.Enter()", body);
        Assert.Contains("NativeCallback.Rethrow(__d)", body);
    }

    [Theory]
    [InlineData(SignatureCallingConvention.CDecl, "System.Runtime.CompilerServices.CallConvCdecl")]
    [InlineData(SignatureCallingConvention.StdCall, "System.Runtime.CompilerServices.CallConvStdcall")]
    [InlineData(SignatureCallingConvention.ThisCall, "System.Runtime.CompilerServices.CallConvThiscall")]
    [InlineData(SignatureCallingConvention.FastCall, "System.Runtime.CompilerServices.CallConvFastcall")]
    [InlineData(SignatureCallingConvention.Unmanaged, null)]
    public void Callback_trampolines_use_the_declared_calling_convention(SignatureCallingConvention conv, string? callConv)
    {
        // review F17: UnmanagedCallersOnly was always CallConvCdecl
        var module = new IlModule("cc_r1");
        var ffi = new AirToIlFfi(module);
        var t = module.DefineType("", "P", TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class);
        var body = t.DefineMethod("body", MethodAttributes.Private | MethodAttributes.Static, IlType.Int32, [IlType.Int32]);
        body.Il.EmitLoadArg(0);
        body.Il.EmitReturn();
        var cb = ffi.EmitCallback(t, body, conv: conv);
        var named = cb.CustomAttributes.Single().Named ?? [];
        if (callConv == null) Assert.Empty(named);
        else Assert.Equal([callConv], (string[])named.Single().Value!);
        // the convention FnPtrSig reads from the declared type round-trips
        if (conv == SignatureCallingConvention.StdCall)
            Assert.Equal(conv, ffi.FnPtrSig("delegate* unmanaged[Stdcall]<int, int>").CallingConvention);
    }
}

/// <summary>Review round 1, F11 and F14: Replicate never shares a mutable reference state; Ring.Release is bounded.</summary>
public class DataflowR1Tests
{
    sealed class Box { public long V; }

    static long[] Ones(int n)
    {
        var a = new long[n];
        Array.Fill(a, 1L);
        return a;
    }

    [Fact]
    public void Replicate_rejects_a_reference_state_without_a_factory()
    {
        var g = new Graph(capacity: 256, batch: 16);
        var e = Assert.Throws<ArgumentException>(() => g.Source(Ones(64)).Replicate(new Box(),
            static (ref Box b, ReadOnlySpan<long> xs, ref RingWriter<long> w) => { foreach (var x in xs) w.Write(b.V += x); }, workers: 4));
        Assert.Contains("factory", e.Message);
        // a struct holding a reference would share it just the same
        Assert.Throws<ArgumentException>(() => new Graph().Source(Ones(4)).Replicate((1, new Box()),
            static (ref (int, Box) b, ReadOnlySpan<long> xs, ref RingWriter<long> w) => w.Write(xs), workers: 2));
    }

    [Fact]
    public void Replicate_with_a_factory_gives_every_batch_its_own_state()
    {
        // the review's repro: a class counter shared by 8 workers reached 483436 instead of the batch size
        for (int round = 0; round < 5; round++)
        {
            var g = new Graph(capacity: 256, batch: 16);
            var sink = g.Source(Ones(20_000))
                .Replicate(() => new Box(), static (ref Box b, ReadOnlySpan<long> xs, ref RingWriter<long> w) =>
                {
                    foreach (var x in xs) w.Write(b.V += x);
                }, workers: 8, batch: 16)
                .Sink(0L, static (ref long m, ReadOnlySpan<long> xs) => { foreach (var x in xs) m = Math.Max(m, x); });
            g.Run();
            Assert.Equal(16, sink.State);
        }
    }

    [Fact]
    public unsafe void Function_pointer_replicate_accepts_a_factory()
    {
        var g = new Graph(capacity: 256, batch: 8);
        var sink = g.Source(Ones(1000)).Replicate<Box, long>(() => new Box(), &Count, workers: 4, batch: 8)
            .Sink(0L, static (ref long m, ReadOnlySpan<long> xs) => { foreach (var x in xs) m = Math.Max(m, x); });
        g.Run();
        Assert.Equal(8, sink.State);
    }

    static void Count(ref Box b, ReadOnlySpan<long> xs, ref RingWriter<long> w)
    {
        foreach (var x in xs) w.Write(b.V += x);
    }

    [Fact]
    public void Ring_release_past_the_published_items_throws()
    {
        var r = new Ring<int>(4);
        Assert.True(r.TryWrite(1));
        Assert.True(r.TryWrite(2));
        Assert.Equal(2, r.Peek(2).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => r.Release(4));
        Assert.Throws<ArgumentOutOfRangeException>(() => r.Release(-1));
        Assert.Equal(2, r.Count);                    // unchanged (was -2, then six writes fit a 4-slot ring)
        r.Release(2);
        Assert.Equal(0, r.Count);
        int written = 0;
        while (r.TryWrite(9)) written++;
        Assert.Equal(4, written);
    }
}

/// <summary>Review round 1, F13 and F15.</summary>
public class CmR1Tests
{
    [Fact]
    public void Permute_in_place_is_rejected()
    {
        int[] src = [1, 2, 3, 4];
        var e = Assert.Throws<ArgumentException>(() => Cm.Permute(src, [1, 2, 3, 0], src));
        Assert.Contains("in place", e.Message);
        Assert.Equal([1, 2, 3, 4], src);
        var dst = new int[4];
        Cm.Permute(src, [1, 2, 3, 0], dst);
        Assert.Equal([4, 1, 2, 3], dst);
    }

    [Fact]
    public void Float_sums_start_from_positive_zero()
    {
        Assert.Equal(0u, BitConverter.SingleToUInt32Bits(Cm.Reduce<float>([])));
        Assert.Equal(0UL, BitConverter.DoubleToUInt64Bits(Cm.Reduce<double>([])));
        float[] src = [-0f, -0f, 1f];
        var ex = new float[3];
        var inc = new float[3];
        Cm.ExclusiveScan<float>(src, ex);
        Cm.InclusiveScan<float>(src, inc);
        // the serial fold from 0: 0 + -0 is +0
        Assert.Equal([0u, 0u, 0u], ex.Select(BitConverter.SingleToUInt32Bits));
        Assert.Equal([0u, 0u, 0x3f80_0000u], inc.Select(BitConverter.SingleToUInt32Bits));
        Assert.Equal(0u, BitConverter.SingleToUInt32Bits(Cm.Reduce<float>([-0f, -0f])));
        var big = new double[100_000];
        Array.Fill(big, -0.0);
        Assert.Equal(0UL, BitConverter.DoubleToUInt64Bits(Cm.Reduce<double>(big)));
    }
}

/// <summary>Review round 1, F12: a name ClosureConvert cannot resolve inside a closure is reported, not assumed free.</summary>
public class ClosureConvertR1Tests
{
    static readonly Alice.Compiler.Mir.AirType Int = Alice.Compiler.Mir.AirLeaf.Int;

    static List<Alice.Compiler.Mir.MStmt> Body() =>
    [
        new Alice.Compiler.Mir.MDecl { Name = "q", Init = new Alice.Compiler.Mir.MLambda { ExprBody = new Alice.Compiler.Mir.AirLocalRef("p", Int) } },
        new Alice.Compiler.Mir.MReturn { Value = new Alice.Compiler.Mir.AirLocalRef("q") },
    ];

    [Fact]
    public void An_unresolved_local_in_a_lambda_is_unsupported()
    {
        var body = Body();
        string before = Alice.Compiler.Mir.MirDump.Of(body);
        var pass = new Alice.Compiler.Air.Passes.ClosureConvert(new Alice.Compiler.Air.Passes.ClosureContext { Method = "main" });
        pass.Run(body);
        Assert.Contains(pass.Unsupported, u => u.Contains("`p` in a closure"));
        Assert.Equal(before, Alice.Compiler.Mir.MirDump.Of(body));   // left untouched, not a static lambda reading `p`
    }

    [Fact]
    public void The_same_lambda_converts_once_the_parameter_is_known()
    {
        var body = Body();
        var pass = new Alice.Compiler.Air.Passes.ClosureConvert(new Alice.Compiler.Air.Passes.ClosureContext { Method = "main", Params = [("p", "int")] });
        pass.Run(body);
        Assert.Empty(pass.Unsupported);
        Assert.Contains("DisplayClass_main", Alice.Compiler.Mir.MirDump.Of(body));
    }
}

/// <summary>Review round 1, F18: Dyn against C# `dynamic`.</summary>
public class DynR1Tests
{
    [Flags] public enum Perm : byte { None = 0, Read = 1, Write = 2, Exec = 4 }
    public enum Signed : long { Low = -5, High = 7 }

    public sealed class Gen
    {
        public T Id<T>(T x) => x;
        public int Count<T>(List<T> xs) => xs.Count;
        public string Pick(int x) => "int";
        public string Pick<T>(T x) => "generic " + typeof(T).Name;
        public string Pair<T>(T a, T b) => typeof(T).Name;
        public string Seq<T>(IEnumerable<T> xs) where T : struct => typeof(T).Name;
    }

    static object? Call(object target, string name, params object?[] args)
    {
        var site = new DynSite(DynKind.Call, name, args.Length);
        return Dyn.Call(ref site, target, args.AsSpan());
    }

    static object? Op(DynOp op, object? l, object? r)
    {
        var site = new DynSite(DynKind.BinaryOp, op);
        return Dyn.BinaryOp(ref site, l, r);
    }

    [Fact]
    public void Generic_methods_are_callable_through_any()
    {
        var g = new Gen();
        Assert.Equal(3, Call(g, "Id", 3));
        Assert.Equal("s", Call(g, "Id", "s"));
        Assert.Equal(2, Call(g, "Count", new List<string> { "a", "b" }));
        Assert.Equal("int", Call(g, "Pick", 3));                 // the non-generic overload wins a tie, as in C#
        Assert.Equal("generic String", Call(g, "Pick", "s"));
        Assert.Equal("Object", Call(g, "Pair", "s", new object()));   // T widens to the common base
        Assert.Equal("Int32", Call(g, "Seq", new[] { 1, 2 }));     // through IEnumerable<T>
        Assert.ThrowsAny<MissingMemberException>(() => Call(g, "Seq", (object)new[] { "a" }));   // constraint: struct
    }

    [Fact]
    public void Enum_operators_match_csharp_dynamic()
    {
        Assert.Equal(Perm.Read | Perm.Write, Op(DynOp.BitOr, Perm.Read, Perm.Write));
        Assert.Equal(Perm.Read, Op(DynOp.BitAnd, Perm.Read | Perm.Exec, Perm.Read | Perm.Write));
        Assert.Equal(Perm.Exec, Op(DynOp.BitXor, Perm.Read | Perm.Exec, Perm.Read));
        Assert.Equal(true, Op(DynOp.Lt, Perm.Read, Perm.Exec));
        Assert.Equal(true, Op(DynOp.Lt, Signed.Low, Signed.High));    // signed underlying type
        Assert.Equal(true, Op(DynOp.Eq, Perm.Write, Perm.Write));
        Assert.Throws<InvalidOperationException>(() => Op(DynOp.BitOr, Perm.Read, Signed.High));   // two enum types
    }

    [Fact]
    public void Equality_is_alices_not_csharps()
    {
        // documented (docs/language.md, Any): different types are unequal, structs and tuples compare by value,
        // where C# `dynamic` raises for both
        Assert.Equal(false, Op(DynOp.Eq, 1, "1"));
        Assert.Equal(true, Op(DynOp.Ne, 1, "1"));
        Assert.Equal(true, Op(DynOp.Eq, (1, 2), (1, 2)));
        Assert.Equal(false, Op(DynOp.Eq, new object(), new object()));
    }
}
