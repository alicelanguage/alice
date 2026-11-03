using Alice.Compiler;
using Alice.Compiler.Backend.Wasm;

namespace Alice.Tests.Backends;

public class AirToWasmProbe
{
    [Fact]
    public void Probe()
    {
        var src = Environment.GetEnvironmentVariable("WASM_PROBE");
        if (src == null) return;
        var files = AliceCompiler.LoadWithRequires([src]);
        if (Environment.GetEnvironmentVariable("WASM_MIR") == "1") File.WriteAllText(src + ".mir", WasmProbe.Dump(files));
        var res = AirToWasm.Compile(files);
        File.WriteAllText(src + ".log", res.Stats + "\n" + string.Join("\n", res.Errors.Select(e => e.Message)) + "\n");
        if (res.Ok) File.WriteAllBytes(src + ".wasm", res.Wasm!);
    }
}
