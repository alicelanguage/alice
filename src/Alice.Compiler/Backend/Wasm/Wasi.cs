namespace Alice.Compiler.Backend.Wasm;

/// <summary>
/// WASI preview 1 behind the Alice IO/Env/Time surface (ADR 0034): `fd_write` (puts/print, in rt.wat), `args_get`
/// (ARGV), `clock_time_get` (monotonic and wall clock), `random_get`, `proc_exit`. Plus the Node runner that hosts a
/// module with node:wasi (wasmtime runs it as is).
/// </summary>
public sealed class Wasi(AirToWasm c)
{
    const string P1 = "wasi_snapshot_preview1";

    WasmFunc Import(string field, ValType[] ps, ValType[] rs) => c.M.Import(P1, field, "wasi." + field, new FuncSig(ps, rs));

    WasmFunc? argv;

    /// <summary>`() -> String[]`: the program arguments without argv[0] (C# `Main(string[] args)` semantics).</summary>
    internal WasmFunc ArgvFunc
    {
        get
        {
            if (argv != null) return argv;
            var sizes = Import("args_sizes_get", [ValType.I32, ValType.I32], [ValType.I32]);
            var get = Import("args_get", [ValType.I32, ValType.I32], [ValType.I32]);
            var f = argv = c.M.Define("alice.argv", new FuncSig([], [ValType.I32]));
            var b = f.Code!;
            int fr = b.Local(ValType.I32, "frame"), argc = b.Local(ValType.I32, "argc"), bufsz = b.Local(ValType.I32, "bufsz");
            int ptrs = b.Local(ValType.I32, "ptrs"), buf = b.Local(ValType.I32, "buf"), arr = b.Local(ValType.I32, "arr"), i = b.Local(ValType.I32, "i");
            int s = b.Local(ValType.I32, "s"), len = b.Local(ValType.I32, "len"), total = b.Local(ValType.I32, "total");
            // sizes into a 16-byte frame
            b.I32(16); b.Call(c.Rt.Func("frame_enter")); b.Set(fr);
            b.Get(fr); b.Get(fr); b.I32(4); b.Emit(Op.I32Add); b.Call(sizes); b.Emit(Op.Drop);
            b.Get(fr); b.Mem(Op.I32Load); b.Set(argc);
            b.Get(fr); b.Mem(Op.I32Load, 4); b.Set(bufsz);
            // pointers and bytes into a frame of 4*argc + bufsz (rounded)
            b.Get(argc); b.I32(2); b.Emit(Op.I32Shl); b.Get(bufsz); b.Emit(Op.I32Add); b.I32(7); b.Emit(Op.I32Add); b.I32(-8); b.Emit(Op.I32And); b.Tee(total);
            b.Call(c.Rt.Func("frame_enter")); b.Set(ptrs);
            b.Get(ptrs); b.Get(argc); b.I32(2); b.Emit(Op.I32Shl); b.Emit(Op.I32Add); b.Set(buf);
            b.Get(ptrs); b.Get(buf); b.Call(get); b.Emit(Op.Drop);
            // String[argc - 1]
            b.Get(argc); b.I32(1); b.Emit(Op.I32Sub); b.Tee(argc);
            b.I32(0); b.Get(argc); b.I32(0); b.Emit(Op.I32GtS); b.Emit(Op.Select);
            b.I32(4); b.I32(3); b.Call(c.Rt.Func("array_new")); b.Set(arr);
            // root the array while the strings are allocated: it lives in the 16-byte frame
            b.Get(fr); b.Get(arr); b.Mem(Op.I32Store, 8);
            var done = b.Block();
            var loop = b.Loop();
            b.Get(i); b.Get(argc); b.Emit(Op.I32GeS); b.BrIf(done);
            // s = argv[i + 1]; len = strlen(s)
            b.Get(ptrs); b.Get(i); b.I32(2); b.Emit(Op.I32Shl); b.Emit(Op.I32Add); b.Mem(Op.I32Load, 4); b.Set(s);
            b.I32(0); b.Set(len);
            var ldone = b.Block();
            var lloop = b.Loop();
            b.Get(s); b.Get(len); b.Emit(Op.I32Add); b.Mem(Op.I32Load8U); b.Emit(Op.I32Eqz); b.BrIf(ldone);
            b.Get(len); b.I32(1); b.Emit(Op.I32Add); b.Set(len);
            b.Br(lloop);
            b.End(lloop);
            b.End(ldone);
            // arr[i] = new string
            b.Get(arr); b.Get(i); b.I32(2); b.Emit(Op.I32Shl); b.Emit(Op.I32Add);
            b.Get(len); b.Call(c.Rt.Func("str_new")); b.Tee(total);
            b.Mem(Op.I32Store, 8);
            b.Get(total); b.I32(8); b.Emit(Op.I32Add); b.Get(s); b.Get(len); b.Call(c.Rt.Func("memcpy"));
            b.Get(i); b.I32(1); b.Emit(Op.I32Add); b.Set(i);
            b.Br(loop);
            b.End(loop);
            b.End(done);
            // pop both frames
            b.Get(fr); b.I32(16); b.Emit(Op.I32Add); b.GSet(c.Rt.Global("sp"));
            b.Get(arr);
            b.Finish();
            return f;
        }
    }

    WasmFunc? clock;

    /// <summary>`(clock id) -> i64` nanoseconds (0 realtime, 1 monotonic).</summary>
    internal WasmFunc ClockFunc
    {
        get
        {
            if (clock != null) return clock;
            var get = Import("clock_time_get", [ValType.I32, ValType.I64, ValType.I32], [ValType.I32]);
            var f = clock = c.M.Define("alice.clock", new FuncSig([ValType.I32], [ValType.I64]), ["id"]);
            var b = f.Code!;
            int fr = b.Local(ValType.I32, "frame");
            b.I32(8); b.Call(c.Rt.Func("frame_enter")); b.Set(fr);
            b.Get(0); b.I64(1000); b.Get(fr); b.Call(get); b.Emit(Op.Drop);
            b.Get(fr); b.I32(8); b.Emit(Op.I32Add); b.GSet(c.Rt.Global("sp"));
            b.Get(fr); b.Mem(Op.I64Load);
            b.Finish();
            return f;
        }
    }

    WasmFunc? random;

    /// <summary>`() -> i64`: 8 random bytes from the host.</summary>
    internal WasmFunc RandomFunc
    {
        get
        {
            if (random != null) return random;
            var get = Import("random_get", [ValType.I32, ValType.I32], [ValType.I32]);
            var f = random = c.M.Define("alice.random", new FuncSig([], [ValType.I64]));
            var b = f.Code!;
            int fr = b.Local(ValType.I32, "frame");
            b.I32(8); b.Call(c.Rt.Func("frame_enter")); b.Set(fr);
            b.Get(fr); b.I32(8); b.Call(get); b.Emit(Op.Drop);
            b.Get(fr); b.I32(8); b.Emit(Op.I32Add); b.GSet(c.Rt.Global("sp"));
            b.Get(fr); b.Mem(Op.I64Load);
            b.Finish();
            return f;
        }
    }

    /// <summary>An ES module that runs <paramref name="wasmFile"/> under Node's WASI (arguments pass through; the
    /// exit code is the program's). `ALICE_TTFC=1` prints instantiate-to-first-call milliseconds on stderr.</summary>
    public static string NodeRunner(string wasmFile) => $$"""
        // generated by alice: runs {{Path.GetFileName(wasmFile)}} with node:wasi (ADR 0034)
        import { readFileSync } from "node:fs";
        import { WASI } from "node:wasi";
        import { argv, env, exit } from "node:process";
        const t0 = performance.now();
        const wasi = new WASI({ version: "preview1", args: [{{Js(Path.GetFileName(wasmFile))}}, ...argv.slice(2)], env, returnOnExit: true });
        const { instance } = await WebAssembly.instantiate(readFileSync(new URL({{Js(Path.GetFileName(wasmFile))}}, import.meta.url)), wasi.getImportObject());
        if (env.ALICE_TTFC === "1") console.error("ttfc_ms " + (performance.now() - t0).toFixed(3));
        exit(wasi.start(instance));
        """;

    static string Js(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
