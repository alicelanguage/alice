using Alice.Compiler;
using Alice.Compiler.Backend;
using Alice.Compiler.Mir;

namespace Alice.Tests.Compiler;

/// <summary>
/// MCode removal batch 2 (ADR 0032, backend plan D0.5): the fused iterators and idiom rewrites build typed AIR nodes,
/// and those nodes print the C# the text form printed.
/// </summary>
public class MirDumpIteratorsTests
{
    [Fact]
    public void Fused_pipeline_uses_typed_locals_and_literals()
    {
        const string src = "def f(xs : Array(Int32)) : Int32\n  xs.map { |x| x + 1 }.select { |x| x > 2 }.count\nend\nputs f([1, 2, 3])\n";
        string dump = MirDump.Collect(() => AliceCompiler.Transpile(src));
        Assert.Contains("decl __r", dump);
        Assert.Contains(" int = (const 0):int", dump);
        Assert.Contains("(Inc postfix (local __r", dump);
        Assert.Contains("= (local __e", dump);   // the loop element is a typed local, not text
    }

    [Fact]
    public void Word_count_split_is_typed()
    {
        const string src =
            "def f(text : String) : Int32\n  counts = Dictionary(String, Int32).new\n  for w in text.Split(\" \")\n" +
            "    counts[w] = counts.GetValueOrDefault(w) + 1\n  end\n  counts.Count\nend\nputs f(\"a b a\")\n";
        string dump = MirDump.Collect(() => AliceCompiler.Transpile(src));
        Assert.Contains("(span-split (local text):string (const ' '):char)", dump);
        Assert.Contains("(alternate-lookup global::System.ReadOnlySpan<char> (local counts):global::System.Collections.Generic.Dictionary<string, int>)", dump);
        Assert.Contains("assign (ref-or-add-default (local __alt", dump);
        Assert.Contains("(par-guard (member Length (local text):string):int 65536):bool", dump);
    }

    [Fact]
    public void Nodes_print_the_former_text()
    {
        Assert.Equal("-1", Printer.E(AirConst.Of(-1)));
        Assert.Equal("5L", Printer.E(AirConst.Of(5L)));
        Assert.Equal("'\\''", Printer.E(AirConst.Of('\'')));
        Assert.Equal("\"a\\\"b\\n\"", Printer.E(AirConst.Of("a\"b\n")));
        Assert.Equal("default!", Printer.E(new AirDefault(null, forgiving: true)));
        Assert.Equal("default(long)", Printer.E(new AirDefault(new AirType.Prim(PrimKind.Long))));
        Assert.Equal("global::System.Math.Max(0, (b) - (a) + 1)", Printer.E(new AirRangeLength { Lo = new AirLocalRef("a"), Hi = new AirLocalRef("b") }));
        Assert.Equal("global::System.Math.Max(0, (b) - (a))", Printer.E(new AirRangeLength { Lo = new AirLocalRef("a"), Hi = new AirLocalRef("b"), Exclusive = true }));
        Assert.Equal("stackalloc long[2]", Printer.E(new AirStackBuffer { ElemCs = "long", Length = 2 }));
        Assert.Equal("ref global::System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(d, k)",
            Printer.E(new MArg { Prefix = "ref ", E = new AirRefOrNullRef { Dict = new AirLocalRef("d"), Key = new AirLocalRef("k") } }));
        // a negative literal is not atomic: `(-1).abs` must keep its parentheses
        Assert.Equal("(-1).Abs()", Printer.E(new MCall { Target = AirConst.Of(-1), Name = "Abs" }));
        Assert.Equal("global::System.Math.Min(n, 3)", Printer.E(new MCall { Target = AirTypeRef.Named("global::System.Math"), Name = "Min", Args = { new AirLocalRef("n"), AirConst.Of(3) } }));
    }
}
