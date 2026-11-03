using System.Reflection;
using System.Text;
using Alice.Compiler.Lowering;
using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;
using Binder = Alice.Compiler.Semantics.Binder;

namespace Alice.Compiler.Backend.Wasm;

/// <summary>A node the wasm backend does not emit yet, with its location (the coverage ranking).</summary>
public sealed class WasmUnsupportedException(string kind, Span? span = null, string? detail = null)
    : Exception($"{kind}{(detail != null ? " (" + detail + ")" : "")}{(span is { } s ? $" at {s.File}:{s.Line}" : "")}")
{
    public string NodeKind { get; } = kind;
    public Span? Where { get; } = span;
}

/// <summary>Per-module statistics: node kinds seen, methods emitted and the unsupported kinds that blocked one.</summary>
public sealed class WasmStats
{
    public readonly SortedDictionary<string, int> Nodes = new(StringComparer.Ordinal);
    public readonly SortedDictionary<string, int> Unsupported = new(StringComparer.Ordinal);
    public int Methods, MethodsOk;

    internal void Seen(string kind) => Nodes[kind] = Nodes.GetValueOrDefault(kind) + 1;
    internal void Blocked(string kind) => Unsupported[kind] = Unsupported.GetValueOrDefault(kind) + 1;

    public override string ToString() =>
        $"methods {MethodsOk}/{Methods}" + (Unsupported.Count > 0 ? "; unsupported: " + string.Join(", ", Unsupported.OrderByDescending(k => k.Value).Select(k => k.Key + " x" + k.Value)) : "");
}

/// <summary>Build options of the wasm backend (`alice build --target wasm32-wasi`).</summary>
public sealed record WasmOptions
{
    /// <summary>`--panic=abort`: no wasm EH; `raise` and runtime errors print and exit (code 101), `rescue` never runs.</summary>
    public bool PanicAbort { get; init; }
    /// <summary>The `name` custom section (function and local names).</summary>
    public bool Names { get; init; } = true;
    /// <summary>A source map file name: the module gets a `sourceMappingURL` section and the result its JSON.</summary>
    public string? SourceMapUrl { get; init; }
    /// <summary>Shadow stack bytes (struct locals, reference locals, frames).</summary>
    public int StackSize { get; init; } = 1 << 20;
    /// <summary>Arena enums (ADR 0037) as slab terms; false keeps them heap records (as `ALICE_NO_ARENA=1`).</summary>
    public bool Arena { get; init; } = Environment.GetEnvironmentVariable("ALICE_NO_ARENA") != "1";
}

/// <summary>The module bytes (null when a method was unsupported), the source map and the statistics.</summary>
public sealed record WasmCompileResult(byte[]? Wasm, string? SourceMap, WasmStats Stats, IReadOnlyList<WasmUnsupportedException> Errors)
{
    public bool Ok => Wasm != null;
}

/// <summary>
/// The wasm backend for `runtime = "alice"` (ADR 0034): binds and lowers exactly as the IL backend does
/// (<see cref="Lowerer"/> with serial forks, <see cref="Optimizer.Pipeline"/>, the IL-only lowering passes), lays out
/// Alice types in linear memory (<see cref="WClass"/>), emits each body through <see cref="WasmBody"/> and links the
/// thin runtime (src/Alice.Core.Wasm). Exceptions use wasm EH (one tag carrying the exception object) unless
/// <see cref="WasmOptions.PanicAbort"/>. I/O, time and randomness are WASI preview 1 (<see cref="Wasi"/>).
/// </summary>
public sealed partial class AirToWasm
{
    internal WasmModule M = null!;
    internal WatReader Rt = null!;
    internal readonly WasmStats Stats = new();
    internal readonly WasmOptions Opt;
    readonly List<WasmUnsupportedException> errors = [];
    internal Binder binder = null!;
    internal Lowerer lowerer = null!;
    internal string File = "";
    internal WasmTag Exn = null!;
    internal Wasi Wasi = null!;

    AirToWasm(WasmOptions opt) => Opt = opt;

    /// <summary>Compiles Alice sources to a wasm32-wasi module.</summary>
    public static WasmCompileResult Compile(IReadOnlyList<SourceFile> files, WasmOptions? options = null)
    {
        var c = new AirToWasm(options ?? new WasmOptions());
        // ADR 0037 arena enums: u64 terms in linear-memory slabs (WasmBody.Arena.cs), or heap records with WasmOptions.Arena = false
        // thread-static compile state set by the other backends (il-emit: Pipeline.Backend = Il) must not leak in:
        // the wasm compile runs the C# pass set and lowers (IlLowering) itself, serially
        var saved = AffinePlan.Disable;
        var savedBackend = Optimizer.Pipeline.Backend;
        AffinePlan.Disable = !c.Opt.Arena;
        Optimizer.Pipeline.Backend = Optimizer.AirBackend.CSharp;
        try { return c.Run(files); }
        finally { AffinePlan.Disable = saved; Optimizer.Pipeline.Backend = savedBackend; }
    }

    /// <summary>The runtime sources (embedded from src/Alice.Core.Wasm, or read from the source tree in development).</summary>
    internal static string RuntimeText(string name)
    {
        var asm = typeof(AirToWasm).Assembly;
        var res = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("." + name, StringComparison.Ordinal) || n == name);
        if (res != null)
        {
            using var s = asm.GetManifestResourceStream(res)!;
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "src", "Alice.Core.Wasm", name);
            if (System.IO.File.Exists(p)) return System.IO.File.ReadAllText(p);
        }
        throw new FileNotFoundException("wasm runtime source " + name + " (src/Alice.Core.Wasm)");
    }

    WasmCompileResult Run(IReadOnlyList<SourceFile> files)
    {
        var all = files.ToList();
        all.Add(new SourceFile("<alice-wasm>/rt.al", RuntimeText("rt.al"), LibraryOnly: true));
        var asts = all.Select(f =>
        {
            var ast = Parser.ParseFile(f.Text, f.Path);
            if (f.LibraryOnly) ast.Items.RemoveAll(i => i is StmtItem);
            return ast;
        }).ToList();
        binder = new Binder(asts, new ClrTypes(null));
        binder.Bind();
        lowerer = new Lowerer(binder) { Sequential = true };
        M = new WasmModule();
        Exn = M.Tag("exn", new FuncSig([ValType.I32], []));
        Wasi = new Wasi(this);
        Rt = new WatReader(M);
        Rt.Bind("throw_code", () => ThrowCodeFunc);
        try
        {
            Rt.Load(RuntimeText("rt.wat"));
            DeclareRuntimeClasses();
            DeclareTypes(asts);
            DeclareMembers(asts);
            Rt.Finish();
            Layout();
            EmitBodies(asts);
            EmitStart(asts);
            FinishLayout();
        }
        catch (WasmUnsupportedException e) { Fail(e); }
        if (errors.Count > 0) return new WasmCompileResult(null, null, Stats, errors);
        M.SourceMapUrl = Opt.SourceMapUrl;
        var bytes = M.Encode(Opt.Names);
        return new WasmCompileResult(bytes, Opt.SourceMapUrl != null ? M.SourceMapJson() : null, Stats, errors);
    }

    internal void Fail(WasmUnsupportedException e)
    {
        errors.Add(e);
        Stats.Blocked(e.NodeKind);
    }

    internal static Span? Sp(Node n) => n.Line > 0 ? new Span("", n.Line, Math.Max(1, n.Col), 0) : null;

    // ------------------------------------------------------------------ memory layout

    /// <summary>Statics area (zeroed; scanned as GC roots): static fields, ARGV, unit variants, closures of method groups.</summary>
    readonly List<byte> statics = new();
    internal int StaticsBase => M.DataBase;

    /// <summary>A zeroed static slot; addresses are final (the statics come first in the data segment).</summary>
    internal int StaticSlot(int size, int align)
    {
        if (staticsFrozen) throw new InvalidOperationException("wasm: static slot after layout");
        while (statics.Count % align != 0) statics.Add(0);
        int at = M.DataBase + statics.Count;
        for (int i = 0; i < size; i++) statics.Add(0);
        return at;
    }

    bool staticsFrozen;
    /// <summary>Spare statics for slots requested while bodies are emitted (cached method-group closures).</summary>
    int spareStatics, spareUsed;

    internal int LateStatic()
    {
        if (spareUsed + 4 > spareStatics) throw new WasmUnsupportedException("Statics:late-slots");
        int at = spareBase + spareUsed;
        spareUsed += 4;
        return at;
    }

    int spareBase;
    internal int ArgvSlot;

    /// <summary>Called when every static field is declared: the statics region is written as the head of the data segment.</summary>
    void Layout()
    {
        if (M.Data.Count != 0) throw new InvalidOperationException("wasm: data before the statics");
        foreach (var c in classes) c.Lay();
        foreach (var c in classes)
            foreach (var f in c.Fields)
                if (f.IsStatic && f.Literal == null) f.Address = StaticSlot(Math.Max(1, f.Type.Size), f.Type.Align);
        ArgvSlot = StaticSlot(4, 4);
        spareStatics = 256;
        spareBase = StaticSlot(spareStatics, 4);
        while (statics.Count % 8 != 0) statics.Add(0);
        staticsFrozen = true;
        M.AddData(statics.ToArray(), 8);
    }

    /// <summary>Runtime area, shadow stack and heap after the data; the runtime's globals get their addresses.</summary>
    void FinishLayout()
    {
        int rt = (M.DataEnd + 15) & ~15;
        int stackBase = rt + 136976;   // rt.wat: the end of the runtime area
        stackBase = (stackBase + 15) & ~15;
        int stackTop = stackBase + Opt.StackSize;
        int heap = (stackTop + 0xFFFF) & ~0xFFFF;
        Rt.Global("rt").InitI = rt;
        Rt.Global("statics_start").InitI = StaticsBase;
        Rt.Global("statics_end").InitI = StaticsBase + statics.Count;
        Rt.Global("stack_base").InitI = stackBase;
        Rt.Global("stack_top").InitI = stackTop;
        Rt.Global("sp").InitI = stackTop;
        Rt.Global("heap_start").InitI = heap;
        M.MemoryPages = (heap >> 16) + 1;
    }

    // ------------------------------------------------------------------ string literals

    readonly Dictionary<string, int> literals = new(StringComparer.Ordinal);

    /// <summary>A static string object (header type 1, never collected: it is not in the heap).</summary>
    internal int Literal(string s)
    {
        if (literals.TryGetValue(s, out int at)) return at;
        var bytes = Encoding.UTF8.GetBytes(s);
        var obj = new byte[8 + bytes.Length];
        BitConverter.GetBytes(0x2000001).CopyTo(obj, 0);
        BitConverter.GetBytes(bytes.Length).CopyTo(obj, 4);
        bytes.CopyTo(obj, 8);
        return literals[s] = M.AddData(obj, 8);
    }

    // ------------------------------------------------------------------ runtime errors

    /// <summary>Runtime error codes raised by rt.wat and generated checks (alice.throw_code).</summary>
    internal static readonly (string Type, string Message)[] ErrorCodes =
    [
        ("System.Exception", "Exception of type 'System.Exception' was thrown."),
        ("System.IndexOutOfRangeException", "Index was outside the bounds of the array."),
        ("System.OverflowException", "Arithmetic operation resulted in an overflow."),
        ("System.DivideByZeroException", "Attempted to divide by zero."),
        ("System.NullReferenceException", "Object reference not set to an instance of an object."),
        ("System.FormatException", "The input string was not in a correct format."),
        ("System.ArgumentOutOfRangeException", "Specified argument was out of the range of valid values."),
        ("System.InvalidCastException", "Specified cast is not valid."),
        ("System.StackOverflowException", "Operation caused a stack overflow."),
        ("System.OutOfMemoryException", "Insufficient memory to continue the execution of the program."),
        ("System.Collections.Generic.KeyNotFoundException", "The given key was not present in the dictionary."),
        ("System.InvalidOperationException", "Operation is not valid due to the current state of the object."),
        ("System.ArgumentException", "Value does not fall within the expected range."),
        ("System.InvalidOperationException", "arena: stale term (a term of an ended region, or a forged word)"),
    ];

    internal const int ErrIndex = 1, ErrOverflow = 2, ErrDivZero = 3, ErrNull = 4, ErrFormat = 5, ErrRange = 6, ErrCast = 7, ErrStack = 8, ErrMemory = 9, ErrKey = 10, ErrState = 11, ErrArg = 12, ErrStaleTerm = 13;

    WasmFunc? throwCode;

    /// <summary>`throw_code(code)`: a new exception of the code's class with the .NET message, thrown.</summary>
    internal WasmFunc ThrowCodeFunc
    {
        get
        {
            if (throwCode != null) return throwCode;
            var f = throwCode = M.Define("alice.throw_code", new FuncSig([ValType.I32], []), ["code"]);
            pendingThrowCode = true;
            return f;
        }
    }

    bool pendingThrowCode;

    void EmitThrowCode()
    {
        if (!pendingThrowCode) return;
        var c = throwCode!.Code!;
        // stack overflow and out of memory abort: there is no room to build an exception
        var ok = c.Block();
        c.Get(0); c.I32(ErrStack); c.Emit(Op.I32Ne);
        c.Get(0); c.I32(ErrMemory); c.Emit(Op.I32Ne);
        c.Emit(Op.I32And);
        c.BrIf(ok);
        c.Get(0); c.I32(ErrStack); c.Emit(Op.I32Eq);
        var iff = c.If(ValType.I32);
        c.I32(Literal("Stack overflow."));
        c.Else();
        c.I32(Literal("Out of memory."));
        c.End(iff);
        c.Call(Rt.Func("eputs"));
        c.I32(134);
        c.Call(Rt.Func("exit"));
        c.Emit(Op.Unreachable);
        c.End(ok);
        int ex = c.Local(ValType.I32, "e");
        var labels = new List<WasmLabel>();
        var outer = c.Block();
        for (int i = 0; i < ErrorCodes.Length; i++) labels.Add(c.Block());
        c.Get(0);
        c.BrTable(labels, labels[0]);
        for (int i = 0; i < ErrorCodes.Length; i++)
        {
            c.End(labels[ErrorCodes.Length - 1 - i]);
            int code = ErrorCodes.Length - 1 - i;
            var cls = RuntimeClass(ErrorCodes[code].Type);
            c.I32(Literal(ErrorCodes[code].Message));
            c.Call(NewException(cls));
            c.Set(ex);
            c.Br(outer);
        }
        c.End(outer);
        c.Get(ex);
        EmitThrow(c);
        c.Finish();
    }

    /// <summary>Throws the exception object on the stack (wasm EH, or print and exit under --panic=abort).</summary>
    internal void EmitThrow(WasmCode c)
    {
        if (Opt.PanicAbort) { c.Call(PanicFunc); c.Emit(Op.Unreachable); }
        else c.Throw(Exn);
    }

    WasmFunc? panic;

    /// <summary>`error: Type: message` on stderr, exit 101 (the `alice run` convention for an unhandled exception).</summary>
    internal WasmFunc PanicFunc
    {
        get
        {
            if (panic != null) return panic;
            var f = panic = M.Define("alice.panic", new FuncSig([ValType.I32], []), ["e"]);
            pendingPanic = true;
            return f;
        }
    }

    bool pendingPanic;

    void EmitPanic()
    {
        if (!pendingPanic) return;
        var c = panic!.Code!;
        var exc = RuntimeClass("System.Exception");
        c.I32(Literal("error: "));
        c.Get(0);
        c.Call(TypeNameFunc);
        c.Call(Rt.Func("str_concat"));
        c.I32(Literal(": "));
        c.Call(Rt.Func("str_concat"));
        c.Get(0);
        c.Mem(Op.I32Load, (uint)exc.FieldsByName["Message"].Offset);
        c.Call(Rt.Func("str_concat"));
        c.Call(Rt.Func("eputs"));
        c.I32(101);
        c.Call(Rt.Func("exit"));
        c.Finish();
    }

    WasmFunc? typeName;

    /// <summary>The short class name of an object (`OverflowException`, `MyError`), from a table of literals.</summary>
    internal WasmFunc TypeNameFunc
    {
        get
        {
            if (typeName != null) return typeName;
            return typeName = M.Define("alice.type_name", new FuncSig([ValType.I32], [ValType.I32]), ["o"]);
        }
    }

    void EmitTypeName()
    {
        if (typeName == null) return;
        var c = typeName.Code!;
        int maxId = classes.Max(x => x.Id);
        var table = new byte[(maxId + 1) * 4];
        foreach (var k in classes)
        {
            if (k.Id <= 0) continue;
            string n = k.Name;
            int dot = n.LastIndexOf('.');
            if (dot >= 0 && k.Sym == null) n = n[(dot + 1)..];
            BitConverter.GetBytes(Literal(n)).CopyTo(table, k.Id * 4);
        }
        int at = M.AddData(table, 4);
        c.Get(0);
        c.Mem(Op.I32Load);
        c.I32(0xFFFFFF);
        c.Emit(Op.I32And);
        c.I32(2);
        c.Emit(Op.I32Shl);
        c.Mem(Op.I32Load, (uint)at);
        c.Finish();
    }

    // ------------------------------------------------------------------ entry point

    WasmFunc? mainFunc;

    void EmitStart(List<FileNode> asts)
    {
        var start = M.Define("_start", new FuncSig([], []));
        start.Export = "_start";
        // the program: static initializers, then the top-level statements; uncaught exceptions print and exit 101
        var prog = M.Define("alice.main", new FuncSig([], []));
        var w = prog.Code!;
        WasmLabel? h = null, t = null;
        if (!Opt.PanicAbort)
        {
            h = w.Block(ValType.I32);
            t = w.TryTable(null, Exn, h);
        }
        foreach (var f in staticInitFuncs) w.Call(f);
        if (mainFunc != null) w.Call(mainFunc);
        if (!Opt.PanicAbort)
        {
            w.End(t!);
            w.Call(Rt.Func("out_flush"));
            w.Emit(Op.Return);
            w.End(h!);
            w.Call(Rt.Func("out_flush"));
            w.Call(PanicFunc);
        }
        else w.Call(Rt.Func("out_flush"));
        w.Finish();
        var c = start.Code!;
        c.Call(Rt.Func("rt_init"));
        if (usesArgv)
        {
            c.I32(ArgvSlot);
            c.Call(Wasi.ArgvFunc);
            c.Mem(Op.I32Store);
        }
        c.Call(prog);
        c.Finish();
        EmitThrowCode();
        EmitPanic();
        EmitTypeName();
        EmitDispatchers();
    }

    internal bool usesArgv;
    internal readonly List<WasmFunc> staticInitFuncs = new();
}
