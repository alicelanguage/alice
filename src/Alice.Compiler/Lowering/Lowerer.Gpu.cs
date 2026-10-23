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

    /// <summary>The reduction variable of the `gpu for` kernel being lowered: its `+=` becomes ILGPU.Atomic.Add.</summary>
    LocalSym? gpuAcc;

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
        // a reduction `t += e` into an outer number: an atomic add on the device (ILGPU.Atomic has Int32/Int64/Float32/Float64)
        if (plan.Acc != null && plan.Acc.Type.Cs is not ("int" or "long" or "float" or "double"))
            throw No($"reductions need an Int32, Int64, Float32 or Float64 accumulator (`{plan.Acc.Name}` is {plan.Acc.Type.Cs})");
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
            if (n is not { Res: "local", Sym: LocalSym l } || plan.Inner.Contains(l) || l == plan.Acc || arrays.Contains(l) || scalars.Contains(l)) continue;
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
        var acc = plan.Acc;
        string AccView() => $"global::ILGPU.Runtime.ArrayView1D<{acc!.Type.Cs}, global::ILGPU.Stride1D.Dense>";
        var ptypes = new List<string> { "global::ILGPU.Index1D", "int" };
        if (acc != null) ptypes.Add(AccView());
        ptypes.AddRange(arrays.Select(View));
        ptypes.AddRange(scalars.Select(s => s.Type.Cs));

        // the kernel: i = global index + start, the body unchecked (GPU arithmetic does not trap)
        bool saved = inParallelGen;
        inParallelGen = true;   // no forks, helpers or pooled buffers inside the kernel
        var savedPre = pre;
        pre = new List<MStmt>();
        ctx.Add("loop");
        inGpuKernel = true;
        gpuAcc = acc;
        List<MStmt> body;
        try { body = Stmts(f.Body, Tail.None, null); }
        finally { inGpuKernel = false; gpuAcc = null; }
        ctx.RemoveAt(ctx.Count - 1);
        body.InsertRange(0, pre);
        pre = savedPre;
        inParallelGen = saved;
        var kp = new Backend.Printer("", 2);
        foreach (var st in body) kp.Root(st);
        string ps = string.Join(", ", new[] { "global::ILGPU.Index1D __gi", "int __ga" }.Concat(acc != null ? [$"{AccView()} __gacc"] : [])
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
        gpuAlloc = alloc;
        gpuCopy = copy;
        foreach (var a in arrays)
            launch.Add(Line(gpuRegion != null
                ? $"var __gb_{a.CsName.TrimStart('@')} = {gpuRegion}.Get({a.CsName}, {(written.Contains(a) ? "true" : "false")})"   // resident until the region ends
                : $"using var __gb_{a.CsName.TrimStart('@')} = {alloc}.Allocate1D(__acc, {a.CsName})"));
        if (acc != null) launch.Add(Line($"using var __gb_acc = {alloc}.Allocate1D(__acc, new {acc.Type.Cs}[1])"));
        launch.Add(Line($"{field}({count}, {start}{(acc != null ? ", __gb_acc.View" : "")}{string.Concat(arrays.Select(a => $", __gb_{a.CsName.TrimStart('@')}.View"))}{string.Concat(scalars.Select(sc => ", " + sc.CsName))})"));
        if (gpuRegion == null || acc != null) launch.Add(Line("__acc.Synchronize()"));   // in a region, launches queue on one stream
        if (gpuRegion == null)
            foreach (var a in arrays.Where(written.Contains)) launch.Add(Line($"{copy}.CopyToCPU(__gb_{a.CsName.TrimStart('@')}.View, {a.CsName})"));
        if (acc != null)
        {
            launch.Add(Line($"var __gsum = new {acc.Type.Cs}[1]"));
            launch.Add(Line($"{copy}.CopyToCPU(__gb_acc.View, __gsum)"));
            launch.Add(Line($"{acc.CsName} += __gsum[0]"));
        }
        host.Add(new MIf { Cond = new MCode($"{count} > 0"), Then = { new MBlock { Body = launch } } });
        return new MBlock { Body = host };
    }

    /// <summary>Number methods that lower to casts or math intrinsics ILGPU compiles.</summary>
    static readonly HashSet<string> GpuSafeExt = ["to_i", "to_i32", "to_i64", "to_f", "to_f32", "to_f64", "to_u8", "to_u16", "to_u32", "to_u64", "to_i8", "to_i16", "abs", "sqrt", "floor", "ceil", "round"];

    string? gpuAlloc, gpuCopy;

    /// <summary>The `Region` local of the enclosing `gpu ... end`, while lowering inside one.</summary>
    string? gpuRegion;

    /// <summary>`gpu ... end`: one Region keeps the arrays of every `gpu for` inside on the device, and copies the
    /// written ones back once at `end`. A CPU use of such an array inside the region would see stale data: an error.</summary>
    MStmt GpuRegion(BeginStmt region, Tail tail, string? target)
    {
        var onDevice = new HashSet<LocalSym>();
        static IEnumerable<NameExpr> Names(IEnumerable<Node> nodes)
        {
            var r = new List<NameExpr>();
            AstWalk.Each(nodes, n => { if (n is NameExpr ne) r.Add(ne); });
            return r;
        }
        foreach (var f in WalkStmts(region.Body).OfType<ForStmt>().Where(f => f.Gpu))
            foreach (var n in Names(f.Body))
                if (n is { Res: "local", Sym: LocalSym l } && l.Type is { Kind: "array" }) onDevice.Add(l);
        void CpuUses(List<Stmt> stmts)
        {
            foreach (var st in stmts)
            {
                if (st is ForStmt { Gpu: true } gf) { foreach (var n in Names([gf.Iter])) Check(n); continue; }   // `0...xs.size` reads only the length
                if (st is WhileStmt w) { foreach (var n in Names([w.Cond])) Check(n); CpuUses(w.Body); continue; }
                if (st is IfStmt i) { foreach (var n in Names(i.Conds)) Check(n); foreach (var b in i.Bodies) CpuUses(b); if (i.Else != null) CpuUses(i.Else); continue; }
                if (st is ForStmt sf) { foreach (var n in Names([sf.Iter])) Check(n); CpuUses(sf.Body); continue; }
                foreach (var n in Names([st])) Check(n);
            }
        }
        void Check(NameExpr n)
        {
            if (n is { Res: "local", Sym: LocalSym l2 } && onDevice.Contains(l2) && !LengthOnly(n))
                throw new CompileException($"{File}:{n.Line}:{Math.Max(1, n.Col)}: `{l2.Name}` is on the GPU until the region ends; read it after `end`, or use it in a `gpu for` inside the region");
        }
        bool LengthOnly(NameExpr n) => regionLengthReads.Contains(n);
        regionLengthReads.Clear();
        AstWalk.Each(region.Body, n =>
        {
            if (n is CallExpr { Recv: NameExpr rn, Args.Count: 0 } c && Binder.Mangle(c.Name) is "size" or "length" or "Length") regionLengthReads.Add(rn);
        });
        CpuUses(region.Body);

        string r = Temp("gpu");
        var saved = gpuRegion;
        gpuRegion = r;
        List<MStmt> body;
        try { body = Stmts(region.Body, tail, target); }
        finally { gpuRegion = saved; }
        return new MUsing { Name = r, Value = new MCode("new global::__AliceGpu.Region()"), Body = body };
    }

    readonly HashSet<NameExpr> regionLengthReads = new(ReferenceEqualityComparer.Instance);

    /// <summary>The process-wide accelerator and the region type of `gpu` code (emitted once per program).</summary>
    public string GpuHost() => $$"""
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

            /// <summary>`gpu ... end`: device copies of the region's arrays, uploaded on first use, written ones copied back at the end.</summary>
            internal sealed class Region : global::System.IDisposable
            {
                readonly global::System.Collections.Generic.Dictionary<object, global::System.IDisposable> buffers = new(global::System.Collections.Generic.ReferenceEqualityComparer.Instance);
                readonly global::System.Collections.Generic.List<global::System.Action> copyBack = new();
                readonly global::System.Collections.Generic.HashSet<object> dirty = new(global::System.Collections.Generic.ReferenceEqualityComparer.Instance);

                public global::ILGPU.Runtime.MemoryBuffer1D<T, global::ILGPU.Stride1D.Dense> Get<T>(T[] array, bool written) where T : unmanaged
                {
                    if (!buffers.TryGetValue(array, out var b)) buffers[array] = b = {{gpuAlloc}}.Allocate1D(Accelerator, array);
                    var buffer = (global::ILGPU.Runtime.MemoryBuffer1D<T, global::ILGPU.Stride1D.Dense>)b;
                    if (written && dirty.Add(array)) copyBack.Add(() => {{gpuCopy}}.CopyToCPU(buffer.View, array));
                    return buffer;
                }

                public void Dispose()
                {
                    Accelerator.Synchronize();
                    foreach (var c in copyBack) c();
                    foreach (var b in buffers.Values) b.Dispose();
                }
            }
        }

        """;
}
