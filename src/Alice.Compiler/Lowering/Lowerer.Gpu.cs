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
                case CallExpr cm when IsClrMath(cm): break;
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
        // read/write sets: an array the body only overwrites at `a[i]` (every write a top-level `a[i] = e`, no read)
        // needs no upload, and only the launch's range [start, start + count) comes back
        var read = WalkReads(f.Body).OfType<NameExpr>().Select(n => n.Sym).OfType<LocalSym>()
            .Concat(WalkStmts(f.Body).OfType<Assign>().Where(a => a.Op != "=").Select(a => a.Target).OfType<IndexExpr>()
                .Select(ix => ix.Target).OfType<NameExpr>().Select(n => n.Sym).OfType<LocalSym>()).ToHashSet();
        bool AtI(Stmt st, LocalSym a) => st is Assign { Op: "=", Target: IndexExpr { Target: NameExpr { Sym: LocalSym t }, Args: [{ Value: NameExpr { Sym: LocalSym ix } }] } } && t == a && ix == plan.LoopVar;
        bool WritesAt(Stmt st, LocalSym a) => st is Assign { Target: IndexExpr { Target: NameExpr { Sym: LocalSym t } } } && t == a;
        var overwritten = arrays.Where(a => written.Contains(a) && !read.Contains(a)
            && f.Body.Any(st => AtI(st, a)) && WalkStmts(f.Body).Where(st => WritesAt(st, a)).All(st => f.Body.Contains(st) && AtI(st, a))).ToHashSet();
        // the whole array, `0...a.size`: in a region its device copy is then complete without an upload
        bool Whole(LocalSym a) => r is { Excl: true, A: LitExpr { Kind: "int", Text: "0" }, B: CallExpr { Recv: NameExpr { Sym: LocalSym rb }, Args.Count: 0 } sz }
            && rb == a && Binder.Mangle(sz.Name) is "size" or "length" or "Length";
        // body-local names must not collide with kernel parameters: they are the same C# names
        int k = ++helperN;
        string kernel = $"__gpu_kernel_{k}", field = $"__gpu_k_{k}", group = $"__gpu_g_{k}", cap = $"__gpu_c_{k}", scalar = $"__gpu_s_{k}", start = Temp("ga"), count = Temp("gn");
        string View(LocalSym a) => $"global::ILGPU.Runtime.ArrayView1D<{a.Type.Elem!.Cs}, global::ILGPU.Stride1D.Dense>";
        var acc = plan.Acc;
        string AccView() => $"global::ILGPU.Runtime.ArrayView1D<{acc!.Type.Cs}, global::ILGPU.Stride1D.Dense>";
        var ptypes = new List<string> { "int", "int" };
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
        string ps = string.Join(", ", new[] { "int __ga", "int __gn" }.Concat(acc != null ? [$"{AccView()} __gacc"] : [])
            .Concat(arrays.Select(a => $"{View(a)} {a.CsName}")).Concat(scalars.Select(s => $"{s.Type.Cs} {s.CsName}")));
        // explicitly grouped: a grid-stride loop over the launch's range, so a grid capped at the device's width covers
        // any count. A reduction adds into a register; the group then reduces (warp shuffles, then shared memory over
        // the warps' partials) and its first thread does the one atomic add of the group
        var kb = new System.Text.StringBuilder();
        kb.Append($"private static void {kernel}({ps})\n{{\n");
        if (acc != null) kb.Append($"    {acc.Type.Cs} __gr = 0;\n");
        kb.Append("    int __gs = global::ILGPU.Grid.DimX * global::ILGPU.Group.DimX;\n");
        kb.Append("    for (int __gt = global::ILGPU.Grid.GlobalIndex.X; __gt < __gn; __gt = __gn - __gt > __gs ? __gt + __gs : __gn)\n    {\n");
        kb.Append($"        int {plan.LoopVar.CsName} = __gt + __ga;\n        unchecked\n        {{\n{kp}        }}\n    }}\n");
        if (acc != null)
            kb.Append($$"""
                    unchecked
                    {
                        for (int __gd = global::ILGPU.Warp.WarpSize / 2; __gd > 0; __gd >>= 1) __gr += global::ILGPU.Warp.ShuffleDown(__gr, __gd);
                        var __gsh = global::ILGPU.SharedMemory.GetDynamic<{{acc.Type.Cs}}>();
                        if (global::ILGPU.Warp.IsFirstLane) __gsh[global::ILGPU.Warp.WarpIdx] = __gr;
                        global::ILGPU.Group.Barrier();
                        for (int __gw = global::ILGPU.Group.DimX / global::ILGPU.Warp.WarpSize / 2; __gw > 0; __gw >>= 1)
                        {
                            if (global::ILGPU.Group.IdxX < __gw) __gsh[global::ILGPU.Group.IdxX] += __gsh[global::ILGPU.Group.IdxX + __gw];
                            global::ILGPU.Group.Barrier();
                        }
                        if (global::ILGPU.Group.IdxX == 0) global::ILGPU.Atomic.Add(ref __gacc[0], __gsh[0]);
                    }

                """);
        kb.Append("}\n");
        kb.Append($"private static global::System.Action<global::ILGPU.KernelConfig, {string.Join(", ", ptypes)}>? {field};\n");
        kb.Append($"private static int {group}, {cap};\n");
        // the device accumulator, allocated once per thread (two threads may launch the same loop at once)
        if (acc != null) kb.Append($"[global::System.ThreadStatic] private static global::ILGPU.Runtime.MemoryBuffer1D<{acc.Type.Cs}, global::ILGPU.Stride1D.Dense>? {scalar};\n");
        Extra.Add(kb.ToString());
        UsesGpu = true;

        // the host: bounds, upload, launch (kernel compiled once per process), copy back what the body wrote
        var host = new List<MStmt>
        {
            new MDecl { Name = start, TypeCs = "int", Init = Expr(r.A) },
            new MDecl { Name = count, TypeCs = "int", Init = new AirTripCount { End = Expr(r.B), Start = new AirLocalRef(start, AirLeaf.Int), Inclusive = !r.Excl, EndAsWritten = true, Type = AirLeaf.Int } },
        };
        // ILGPU's extension classes, found by reflection at compile time: fully qualified calls need no imports
        var accType = binder.Clr.Find("ILGPU.Runtime.Accelerator", 0, []);
        var viewType = binder.Clr.Find("ILGPU.Runtime.ArrayView1D", 2, []);
        string? alloc = accType == null ? null : binder.Clr.ExtensionHost("Allocate1D", accType, p => p.IsArray);
        string? copy = viewType == null ? null : binder.Clr.ExtensionHost("CopyToCPU", viewType, p => p.IsArray);
        string? load = accType == null ? null : binder.Clr.ExtensionHost("LoadStreamKernel", accType);
        string? zero = viewType == null ? null : binder.Clr.ExtensionHost("MemSetToZero", viewType);
        if (alloc == null || copy == null || load == null || zero == null || binder.Clr.Find("ILGPU.Runtime.KernelUtil", 0, []) == null)
            throw No("this ILGPU version lacks Allocate1D / CopyToCPU / MemSetToZero / LoadStreamKernel / KernelUtil (ILGPU 1.x is supported)");
        // the ILGPU host statements, through the extension hosts found above
        static AirLocalRef L(string name) => new(name);
        static MMember ViewOf(string buffer) => new() { Target = L(buffer), Name = "View" };
        static MStmt Do(MExpr e) => new MExprStmt { E = e };
        var gpuApi = AirTypeRef.Named("global::__AliceGpu");
        var launch = new List<MStmt>
        {
            new MDecl { Name = "__acc", Init = new MMember { Target = gpuApi, Name = "Accelerator" } },
            new MAssign { Target = new AirFieldRef(field), Op = "??=", Value = new MCall { Target = AirTypeRef.Named(load), Name = $"LoadStreamKernel<{string.Join(", ", ptypes)}>", Args = { L("__acc"), new AirMethodGroup(null, kernel) } } },
            new MDecl { Name = "__gg", TypeCs = "int", Init = new MCall { Target = AirTypeRef.Named("global::__AliceGpu"), Name = "Grid", Args = { new AirFieldRef(field), new MArg { Prefix = "ref ", E = new AirFieldRef(group) }, new MArg { Prefix = "ref ", E = new AirFieldRef(cap) }, new AirLocalRef(count, AirLeaf.Int) } } },
        };
        gpuAlloc = alloc;
        gpuCopy = copy;
        foreach (var a in arrays)
        {
            string gb = "__gb_" + a.CsName.TrimStart('@');
            var arr = new AirLocalRef(a.CsName, a.Type.ToAir());
            if (gpuRegion != null)   // resident until the region ends
                launch.Add(new MDecl { Name = gb, Init = new MCall { Target = L(gpuRegion), Name = "Get", Args = { arr, AirConst.Bool(!(overwritten.Contains(a) && Whole(a))), AirConst.Bool(written.Contains(a)) } } });
            else if (overwritten.Contains(a))   // write-only: no upload
                launch.Add(new MDecl { Using = true, Name = gb, Init = new MCall { Target = L("__acc"), Name = $"Allocate1D<{a.Type.Elem!.Cs}>", Args = { new MMember { Target = arr, Name = "Length" } } } });
            else
                launch.Add(new MDecl { Using = true, Name = gb, Init = new MCall { Target = AirTypeRef.Named(alloc), Name = "Allocate1D", Args = { L("__acc"), arr } } });
        }
        if (acc != null)
        {
            launch.Add(new MDecl { Name = "__gb_acc", Init = new AirAssignExpr { Target = new AirFieldRef(scalar), Op = "??=", Value = new MCall { Target = L("__acc"), Name = $"Allocate1D<{acc.Type.Cs}>", Args = { AirConst.Int(1) } } } });
            launch.Add(Do(new MCall { Target = AirTypeRef.Named(zero), Name = "MemSetToZero", Args = { ViewOf("__gb_acc") } }));
        }
        // the reduction's shared memory holds one partial per warp
        var config = new MNew { TypeCs = "global::ILGPU.KernelConfig", Args = { L("__gg"), new AirFieldRef(group) } };
        if (acc != null)
            config.Args.Add(new MCall { Target = AirTypeRef.Named("global::ILGPU.SharedMemoryConfig"), Name = $"RequestDynamic<{acc.Type.Cs}>", Args = { new MBinary { Op = AirBinOp.Div, L = new AirFieldRef(group), R = new MMember { Target = L("__acc"), Name = "WarpSize" } } } });
        var run = new MInvoke { Target = new AirFieldRef(field), Args = { config, new AirLocalRef(start, AirLeaf.Int), new AirLocalRef(count, AirLeaf.Int) } };
        if (acc != null) run.Args.Add(ViewOf("__gb_acc"));
        run.Args.AddRange(arrays.Select(a => (MExpr)ViewOf("__gb_" + a.CsName.TrimStart('@'))));
        run.Args.AddRange(scalars.Select(sc => (MExpr)new AirLocalRef(sc.CsName, sc.Type.ToAir())));
        launch.Add(Do(run));
        if (gpuRegion == null || acc != null) launch.Add(Do(new MCall { Target = L("__acc"), Name = "Synchronize" }));   // in a region, launches queue on one stream
        if (gpuRegion == null)
            foreach (var a in arrays.Where(written.Contains))
            {
                string gb = "__gb_" + a.CsName.TrimStart('@');
                var arr = new AirLocalRef(a.CsName, a.Type.ToAir());
                launch.Add(Do(overwritten.Contains(a)
                    ? new MCall   // only the range came from the device
                    {
                        Target = AirTypeRef.Named(copy), Name = "CopyToCPU",
                        Args = { new MCall { Target = ViewOf(gb), Name = "SubView", Args = { new AirLocalRef(start, AirLeaf.Int), new AirLocalRef(count, AirLeaf.Int) } }, new MArg { Prefix = "ref ", E = new MIndex { Target = arr, Args = { new AirLocalRef(start, AirLeaf.Int) } } }, new AirLocalRef(count, AirLeaf.Int) },
                    }
                    : new MCall { Target = AirTypeRef.Named(copy), Name = "CopyToCPU", Args = { ViewOf(gb), arr } }));
            }
        if (acc != null)
        {
            launch.Add(new MDecl { Name = "__gsum", TypeCs = acc.Type.Cs, Init = AirConst.Int(0) });
            launch.Add(Do(new MCall { Target = AirTypeRef.Named(copy), Name = "CopyToCPU", Args = { ViewOf("__gb_acc"), new MArg { Prefix = "ref ", E = L("__gsum") }, AirConst.Int(1) } }));
            launch.Add(new MAssign { Target = new AirLocalRef(acc.CsName, acc.Type.ToAir()), Op = "+=", Value = L("__gsum") });
        }
        host.Add(new MIf { Cond = new MBinary { Op = AirBinOp.Gt, L = new AirLocalRef(count, AirLeaf.Int), R = AirConst.Int(0), Type = AirLeaf.Bool }, Then = { new MBlock { Body = launch } } });
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
        return new MUsing { Name = r, Value = new MNew { TypeCs = "global::__AliceGpu.Region" }, Body = body };
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

            /// <summary>The grid of a launch over <paramref name="count"/> iterations. The group size comes from ILGPU's
            /// occupancy estimate once per kernel (a power of two, a multiple of the warp size, within the device's
            /// limits); the grid is capped at what the device runs at once (multiprocessors x resident groups x 2), and
            /// the kernel's grid-stride loop covers the rest.</summary>
            public static int Grid<TDelegate>(TDelegate kernel, ref int group, ref int cap, int count) where TDelegate : global::System.Delegate
            {
                var a = Accelerator;
                if (group == 0)
                {
                    var k = global::ILGPU.Runtime.KernelUtil.GetKernel(kernel);
                    int warp = global::System.Math.Max(1, a.WarpSize), g;
                    try { g = a.EstimateGroupSize(k); } catch (global::System.Exception) { g = 256; }   // OpenCL has no estimate
                    g = global::System.Math.Clamp(g, warp, global::System.Math.Max(warp, a.MaxNumThreadsPerGroup));
                    while ((g & (g - 1)) != 0) g &= g - 1;
                    if (g < warp) g = warp;
                    int resident;
                    try { resident = a.EstimateMaxActiveGroupsPerMultiprocessor(k, g, g / warp * 8); }
                    catch (global::System.Exception) { resident = a.MaxNumThreadsPerMultiprocessor / g; }
                    cap = global::System.Math.Max(1, a.NumMultiprocessors) * global::System.Math.Max(1, resident) * 2;
                    group = g;
                }
                return (int)global::System.Math.Clamp(((long)count + group - 1) / group, 1, cap);
            }

            /// <summary>`gpu ... end`: device copies of the region's arrays, uploaded on first use, written ones copied back at the end.</summary>
            internal sealed class Region : global::System.IDisposable
            {
                readonly global::System.Collections.Generic.Dictionary<object, global::System.IDisposable> buffers = new(global::System.Collections.Generic.ReferenceEqualityComparer.Instance);
                readonly global::System.Collections.Generic.List<global::System.Action> copyBack = new();
                readonly global::System.Collections.Generic.HashSet<object> dirty = new(global::System.Collections.Generic.ReferenceEqualityComparer.Instance);

                /// <summary>The device copy of <paramref name="array"/>. On first use it is uploaded, unless the loop
                /// overwrites the whole array without reading it (<paramref name="upload"/> false). Copies are valid on
                /// the device from then on; written ones go back to the host once, at the end.</summary>
                public global::ILGPU.Runtime.MemoryBuffer1D<T, global::ILGPU.Stride1D.Dense> Get<T>(T[] array, bool upload, bool written) where T : unmanaged
                {
                    if (!buffers.TryGetValue(array, out var b))
                        buffers[array] = b = upload ? {{gpuAlloc}}.Allocate1D(Accelerator, array) : Accelerator.Allocate1D<T>(array.Length);
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
