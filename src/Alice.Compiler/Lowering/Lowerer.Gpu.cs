using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Lowering;

/// <summary>
/// `gpu for i in a...b` (ADR 0021): the loop body becomes an ILGPU kernel run on the preferred device (CUDA, OpenCL,
/// or ILGPU's CPU accelerator). The body must be what a `par for` may be (each iteration writes only `a[i]` or its own
/// row) and GPU-safe besides: arrays of numbers and number scalars only, arithmetic, indexing and `Math.*`, no calls,
/// no allocation, no reduction. Arrays the body reads or writes are uploaded; arrays it writes are copied back.
/// </summary>
public sealed partial class Lowerer
{
    /// <summary>Set when a `gpu for` was lowered: the emitter adds the shared accelerator class once.</summary>
    public bool UsesGpu;

    MStmt GpuFor(ForStmt f)
    {
        string where = $"{File}:{f.Line}:{Math.Max(1, f.Col)}";
        CompileException No(string why) => new($"{where}: cannot run `gpu for` on the GPU: {why}");
        if (Method is { IsAsync: true } || ctx.Contains("lambda")) throw No("it cannot run inside a block or an async def");
        if (f.Iter is not RangeExpr r || f.Locals.Count != 1 || f.Locals[0].Type.Cs != "int") throw No("it needs an Int32 range, `for i in a...b`");
        var plan = new LoopPlan { LoopVar = f.Locals[0] };
        plan.Inner.Add(plan.LoopVar);
        if (!LoopBody(f.Body, plan, 0) || WalkStmts(f.Body).Any(s => s is Assign { Target: NameExpr { Sym: LocalSym c } } && c == plan.LoopVar))
            throw No("iterations are not provably independent (each may write only `a[i]` at its own index and locals declared in the body)");
        if (plan.Acc != null) throw No("reductions (`+=` into an outer variable) are not supported yet; write per-index results to an array");
        foreach (var e in WalkReads(f.Body))
            switch (e)
            {
                case CallExpr { Recv: ConstExpr { Ref.Name: "Math" or "MathF" } }: break;
                case CallExpr { Res: "ext", Args.Count: 0, Block: null } cv when GpuSafeExt.Contains(Binder.Mangle(cv.Name)): break;   // conversions, abs...
                case CallExpr { Res: "field" }: throw No($"`{((CallExpr)e).Name}` reads an object member; pass the value in a local");
                case CallExpr ce: throw No($"`{ce.Name}` is a call; GPU code may use arithmetic, indexing and Math.* only");
                case NewExpr or ArrayLit or InterpExpr or LambdaExpr: throw No("it allocates; GPU code cannot");
                case NameExpr { Res: "field" or "call" or "selfcall" } n: throw No($"`{n.Name}` is not a local");
            }

        // free locals: arrays of numbers become views, number scalars become values
        var arrays = new List<LocalSym>();
        var scalars = new List<LocalSym>();
        foreach (var n in WalkReads(f.Body).Concat(WalkExpr(r.A)).Concat(WalkExpr(r.B)).OfType<NameExpr>()
                     .Concat(WalkStmts(f.Body).OfType<Assign>().Select(a => a.Target).OfType<IndexExpr>().Select(ix => ix.Target).OfType<NameExpr>()))
        {
            if (n is not { Res: "local", Sym: LocalSym l } || plan.Inner.Contains(l) || arrays.Contains(l) || scalars.Contains(l)) continue;
            if (l.Type is { Kind: "array", Elem: { } el } && Unmanaged.Contains(el.Cs) && el.Cs is not ("bool" or "char")) arrays.Add(l);
            else if (Unmanaged.Contains(l.Type.Cs) && l.Type.Cs is not ("bool" or "char")) scalars.Add(l);
            else throw No($"`{l.Name}` is a {l.Type.Cs}; kernels take arrays of numbers and numbers");
        }
        // the body is GPU-safe: only now does the missing package matter
        if (binder.Clr.Find("ILGPU.Context", 0, []) == null) throw No("it needs the ILGPU package (alice add ILGPU)");
        var written = WalkStmts(f.Body).OfType<Assign>().Select(a => a.Target).OfType<IndexExpr>()
            .Select(ix => ix.Target).OfType<NameExpr>().Select(n => n.Sym).OfType<LocalSym>().ToHashSet();
        // body-local names must not collide with kernel parameters: they are the same C# names
        int k = ++helperN;
        string kernel = $"__gpu_kernel_{k}", field = $"__gpu_k_{k}", start = Temp("ga"), count = Temp("gn");
        string View(LocalSym a) => $"global::ILGPU.Runtime.ArrayView1D<{a.Type.Elem!.Cs}, global::ILGPU.Stride1D.Dense>";
        var ptypes = new List<string> { "global::ILGPU.Index1D", "int" };
        ptypes.AddRange(arrays.Select(View));
        ptypes.AddRange(scalars.Select(s => s.Type.Cs));

        // the kernel: i = global index + start, the body unchecked (GPU arithmetic does not trap)
        bool saved = inParallelGen;
        inParallelGen = true;   // no forks, helpers or pooled buffers inside the kernel
        var savedPre = pre;
        pre = new List<MStmt>();
        ctx.Add("loop");
        inGpuKernel = true;
        List<MStmt> body;
        try { body = Stmts(f.Body, Tail.None, null); }
        finally { inGpuKernel = false; }
        ctx.RemoveAt(ctx.Count - 1);
        body.InsertRange(0, pre);
        pre = savedPre;
        inParallelGen = saved;
        var kp = new Backend.Printer("", 2);
        foreach (var st in body) kp.Root(st);
        string ps = string.Join(", ", new[] { "global::ILGPU.Index1D __gi", "int __ga" }
            .Concat(arrays.Select(a => $"{View(a)} {a.CsName}")).Concat(scalars.Select(s => $"{s.Type.Cs} {s.CsName}")));
        Extra.Add($"private static void {kernel}({ps})\n{{\n    int {plan.LoopVar.CsName} = (int)__gi + __ga;\n    unchecked\n    {{\n{kp}    }}\n}}\n"
            + $"private static global::System.Action<{string.Join(", ", ptypes)}>? {field};\n");
        UsesGpu = true;

        // the host: bounds, upload, launch (kernel compiled once per process), copy back what the body wrote
        var host = new List<MStmt>
        {
            new MDecl { Name = start, TypeCs = "int", Init = Expr(r.A) },
            new MDecl { Name = count, TypeCs = "int", Init = new MCode($"{Backend.Printer.E(Expr(r.B))} - {start}{(r.Excl ? "" : " + 1")}") },
        };
        // ILGPU's extension classes, found by reflection at compile time: fully qualified calls need no imports
        var accType = binder.Clr.Find("ILGPU.Runtime.Accelerator", 0, []);
        var viewType = binder.Clr.Find("ILGPU.Runtime.ArrayView1D", 2, []);
        string? alloc = accType == null ? null : binder.Clr.ExtensionHost("Allocate1D", accType, p => p.IsArray);
        string? copy = viewType == null ? null : binder.Clr.ExtensionHost("CopyToCPU", viewType, p => p.IsArray);
        string? load = accType == null ? null : binder.Clr.ExtensionHost("LoadAutoGroupedStreamKernel", accType);
        if (alloc == null || copy == null || load == null) throw No("this ILGPU version lacks Allocate1D / CopyToCPU / LoadAutoGroupedStreamKernel (ILGPU 1.x is supported)");
        static MStmt Line(string code) => new MExprStmt { E = new MCode(code) };
        var launch = new List<MStmt>
        {
            Line("var __acc = global::__AliceGpu.Accelerator"),
            Line($"{field} ??= {load}.LoadAutoGroupedStreamKernel<{string.Join(", ", ptypes)}>(__acc, {kernel})"),
        };
        foreach (var a in arrays) launch.Add(Line($"using var __gb_{a.CsName.TrimStart('@')} = {alloc}.Allocate1D(__acc, {a.CsName})"));
        launch.Add(Line($"{field}({count}, {start}{string.Concat(arrays.Select(a => $", __gb_{a.CsName.TrimStart('@')}.View"))}{string.Concat(scalars.Select(sc => ", " + sc.CsName))})"));
        launch.Add(Line("__acc.Synchronize()"));
        foreach (var a in arrays.Where(written.Contains)) launch.Add(Line($"{copy}.CopyToCPU(__gb_{a.CsName.TrimStart('@')}.View, {a.CsName})"));
        host.Add(new MIf { Cond = new MCode($"{count} > 0"), Then = { new MBlock { Body = launch } } });
        return new MBlock { Body = host };
    }

    /// <summary>Number methods that lower to casts or math intrinsics ILGPU compiles.</summary>
    static readonly HashSet<string> GpuSafeExt = ["to_i", "to_i32", "to_i64", "to_f", "to_f32", "to_f64", "to_u8", "to_u16", "to_u32", "to_u64", "to_i8", "to_i16", "abs", "sqrt", "floor", "ceil", "round"];

    /// <summary>The process-wide accelerator of `gpu for` loops (emitted once per program).</summary>
    public const string GpuHost = """
        internal static class __AliceGpu
        {
            static global::ILGPU.Context? context;
            static global::ILGPU.Runtime.Accelerator? accelerator;
            static readonly object gate = new();
            /// <summary>The preferred device: a GPU when one is present, else ILGPU's CPU accelerator (ALICE_GPU=cpu forces it).</summary>
            public static global::ILGPU.Runtime.Accelerator Accelerator
            {
                get
                {
                    if (accelerator != null) return accelerator;
                    lock (gate)
                    {
                        context ??= global::ILGPU.Context.CreateDefault();
                        // ALICE_GPU=cpu forces ILGPU's CPU accelerator (CI machines, debugging a kernel without a device)
                        bool cpu = global::System.Environment.GetEnvironmentVariable("ALICE_GPU") == "cpu";
                        return accelerator ??= context.GetPreferredDevice(cpu).CreateAccelerator(context);
                    }
                }
            }
        }

        """;
}
