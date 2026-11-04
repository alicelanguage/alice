using System.Diagnostics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;
using Alice.Compiler;
using Alice.Compiler.Backend.Il;
using ILVerify;
using Xunit.Abstractions;

namespace Alice.Tests.Backends;

/// <summary>
/// D1.4: the direct IL backend (AirToIl, ADR 0033). Programs are compiled by both backends and run; stdout must be
/// identical (differential), every IL image passes ILVerify, kernels keep the 0-byte allocation invariant, and a
/// corpus pass over benchmarks/ reports (without failing) how much compiles and runs identically under IL.
/// </summary>
[Collection("console")]
public class AirToIlTests(ITestOutputHelper output)
{
    // ------------------------------------------------------------------ harness

    sealed class Resolver : IResolver
    {
        static readonly string Shared = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        readonly Dictionary<string, PEReader?> cache = [];

        PEReader? Load(string name)
        {
            if (cache.TryGetValue(name, out var r)) return r;
            var path = name == "Alice.Runtime" ? typeof(Alice.Runtime.Prelude).Assembly.Location : Path.Combine(Shared, name + ".dll");
            return cache[name] = File.Exists(path) ? new PEReader(File.OpenRead(path)) : null;
        }

        public PEReader ResolveAssembly(AssemblyNameInfo name) => Load(name.Name)!;
        public PEReader ResolveModule(AssemblyNameInfo name, string module) => null!;
    }

    /// <summary>ILVerify over the whole image: "Type::Method: Code message" lines.</summary>
    internal static List<string> IlVerify(byte[] pe)
    {
        using var reader = new PEReader(new MemoryStream(pe));
        var md = reader.GetMetadataReader();
        var verifier = new Verifier(new Resolver(), new VerifierOptions { SanityChecks = true });
        verifier.SetSystemModuleName(new AssemblyNameInfo("System.Private.CoreLib"));
        var errors = new List<string>();
        foreach (var r in verifier.Verify(reader))
        {
            // [SkipLocalsInit], as the C# backend emits it (ADR 0033): valid, merely unverifiable
            if (r.Code == VerifierError.InitLocals) continue;
            // `stackalloc` into a Span (localloc + Span(void*, int)) is unverifiable for Roslyn's output too
            if (r.Code == VerifierError.Unverifiable && !r.Method.IsNil && UsesLocalloc(reader, md.GetMethodDefinition(r.Method))) continue;
            string where = r.Method.IsNil ? "" : md.GetString(md.GetTypeDefinition(md.GetMethodDefinition(r.Method).GetDeclaringType()).Name)
                + "::" + md.GetString(md.GetMethodDefinition(r.Method).Name);
            errors.Add(where + ": " + r.Code + " " + r.Message);
        }
        return errors;
    }

    static bool UsesLocalloc(PEReader pe, MethodDefinition m)
    {
        if (m.RelativeVirtualAddress == 0) return false;
        var il = pe.GetMethodBody(m.RelativeVirtualAddress).GetILBytes()!;
        for (int i = 0; i + 1 < il.Length; i++) if (il[i] == 0xFE && il[i + 1] == 0x0F) return true;
        return false;
    }

    static IlCompileResult Il(string src, string name = "il", bool exe = true)
    {
        var res = AirToIl.Compile([new SourceFile("main.al", src)], name, exe);
        Assert.True(res.Ok, "IL backend: " + string.Join("\n", res.Errors.Select(e => e.Message)));
        return res;
    }

    static readonly Dictionary<short, System.Reflection.Emit.OpCode> Ops =
        typeof(System.Reflection.Emit.OpCodes).GetFields().Select(f => (System.Reflection.Emit.OpCode)f.GetValue(null)!).ToDictionary(o => o.Value);

    /// <summary>Decodes every method body after serialization: opcodes known, branch targets on instruction
    /// boundaries, the last instruction ending at the body end (IlBuilder relaxes branches itself).</summary>
    internal static List<string> DecodeCheck(byte[] image)
    {
        var bad = new List<string>();
        using var pe = new PEReader(new MemoryStream(image));
        var md = pe.GetMetadataReader();
        foreach (var h in md.MethodDefinitions)
        {
            var m = md.GetMethodDefinition(h);
            if (m.RelativeVirtualAddress == 0) continue;
            var il = pe.GetMethodBody(m.RelativeVirtualAddress).GetILBytes()!;
            var starts = new HashSet<int>();
            var targets = new List<int>();
            int i = 0;
            bool ok = true;
            while (i < il.Length && ok)
            {
                starts.Add(i);
                short v = il[i] == 0xFE && i + 1 < il.Length ? unchecked((short)(0xFE00 | il[i + 1])) : il[i];
                i += il[i] == 0xFE ? 2 : 1;
                if (!Ops.TryGetValue(v, out var op)) { ok = false; break; }
                switch (op.OperandType)
                {
                    case System.Reflection.Emit.OperandType.InlineNone: break;
                    case System.Reflection.Emit.OperandType.ShortInlineBrTarget: targets.Add(i + 1 + (sbyte)il[i]); i += 1; break;
                    case System.Reflection.Emit.OperandType.InlineBrTarget: targets.Add(i + 4 + BitConverter.ToInt32(il, i)); i += 4; break;
                    case System.Reflection.Emit.OperandType.ShortInlineI or System.Reflection.Emit.OperandType.ShortInlineVar: i += 1; break;
                    case System.Reflection.Emit.OperandType.InlineVar: i += 2; break;
                    case System.Reflection.Emit.OperandType.InlineI8 or System.Reflection.Emit.OperandType.InlineR: i += 8; break;
                    case System.Reflection.Emit.OperandType.InlineSwitch:
                        {
                            int n = BitConverter.ToInt32(il, i);
                            int baseOff = i + 4 + 4 * n;
                            for (int k = 0; k < n; k++) targets.Add(baseOff + BitConverter.ToInt32(il, i + 4 + 4 * k));
                            i = baseOff;
                            break;
                        }
                    default: i += 4; break;
                }
            }
            if (!ok || i != il.Length || targets.Any(t => !starts.Contains(t))) bad.Add(md.GetString(m.Name));
        }
        return bad;
    }

    static void Verified(byte[] pe)
    {
        var decode = DecodeCheck(pe);
        Assert.True(decode.Count == 0, "IL decode check: " + string.Join(", ", decode));
        var errors = IlVerify(pe);
        Assert.True(errors.Count == 0, "ILVerify:\n" + string.Join("\n", errors));
    }

    static string RunCs(string src, params string[] args) =>
        AliceCompiler.Invoke(AliceCompiler.Load(AliceCompiler.Emit([AliceCompiler.Transpile(src)], "cs", exe: true)), args);

    static string RunIl(byte[] pe, params string[] args) => AliceCompiler.Invoke(AliceCompiler.Load(pe), args);

    /// <summary>Both backends, same stdout; the IL image is ILVerify clean.</summary>
    void Differential(string src, params string[] args)
    {
        var res = Il(src);
        Verified(res.Pe!);
        string cs = RunCs(src, args);
        string il = RunIl(res.Pe!, args);
        Assert.Equal(cs, il);
        output.WriteLine(il);
    }

    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Alice.sln"))) d = d.Parent;
        return d!.FullName;
    }

    // ------------------------------------------------------------------ programs

    public static TheoryData<string, string> Programs => new()
    {
        { "arithmetic and checked overflow", "x = 2_000_000_000\nbegin\n  y = x + x\n  puts y\nrescue e : OverflowException\n  puts \"overflow\"\nend\nputs x &+ x\nputs 7 / 2\nputs -7 % 3\nputs 1.5 * 4\nputs 10_u32 - 3_u32\nputs (1_i64 << 40) >> 3\n" },
        { "loops break continue", "s = 0\ni = 0\nwhile i < 20\n  i += 1\n  next if i % 3 == 0\n  break if i > 15\n  s += i\nend\nputs s\nfor k in 0...5\n  s -= k\nend\nputs s\n" },
        { "functions and recursion", "def fib(n : Int32) : Int32\n  n < 2 ? n : fib(n - 1) + fib(n - 2)\nend\ndef gcd(a : Int64, b : Int64) : Int64\n  b == 0 ? a : gcd(b, a % b)\nend\nputs fib(20)\nputs gcd(1071, 462)\n" },
        { "strings and interpolation", "name = \"world\"\nn = 42\nputs \"hello #{name}, #{n + 1} and #{1.5}\"\nputs name.size\nputs name.upcase\nputs \"a\" + \"b\"\nputs name[0]\n" },
        { "arrays and lists", "xs = [3, 1, 2]\ntotal = 0\nxs.each { |x| total += x }\nputs total\nl = List(Int32).new\nl << 5\nl << 6\nfor e in l\n  total += e\nend\nputs total\nys = Array(Int64).new(4)\nys[2] = 7_i64\nys[2] += 1_i64\nputs ys[2]\nputs xs.max\n" },
        { "classes and properties", "class Counter\n  getter count : Int32\n  def initialize(@start : Int32)\n    @count = @start\n  end\n  def bump(n : Int32) : Int32\n    @count += n\n    @count\n  end\nend\nc = Counter.new(3)\nc.bump(4)\nputs c.count\nputs c.bump(1)\n" },
        { "inheritance and virtual dispatch", "class Animal\n  def speak : String\n    \"...\"\n  end\n  def twice : String\n    speak + speak\n  end\nend\nclass Dog < Animal\n  def speak : String\n    \"woof\"\n  end\nend\na = Dog.new\nputs a.twice\nputs Animal.new.speak\n" },
        { "records and structs", "record struct P(x : Int32, y : Int32)\nrecord Q(a : Float64, b : String)\np = P.new(1, 2)\nputs p.x + p.y\nq = Q.new(1.5, \"z\")\nputs q.b\nputs q.a * 2.0\n" },
        { "enums and sum types", "enum Color\n  Red\n  Green\nend\nenum Shape\n  Circle(r : Float64)\n  Square(s : Float64)\nend\ndef area(s : Shape) : Float64\n  case s\n  when Shape::Circle(r) then r * r * 3.0\n  when Shape::Square(x) then x * x\n  else 0.0\n  end\nend\nputs area(Shape::Circle.new(2.0))\nputs area(Shape::Square.new(3.0))\nputs Color::Green == Color::Green\n" },
        { "exceptions", "def risky(n : Int32) : Int32\n  raise \"bad #{n}\" if n > 2\n  n\nend\nbegin\n  puts risky(1)\n  puts risky(5)\nrescue e\n  puts e.message\nensure\n  puts \"done\"\nend\n" },
        { "hash and conversions", "h = Hash(String, Int32).new\nh[\"a\"] = 1\nh[\"b\"] = 2\nputs h[\"b\"]\nputs h.size\nputs 3.7.to_i32\nputs 65.to_u8\nputs \"12\".to_i + 1\n" },
        { "closures and lambdas", "sq = ->(x : Int32) { x * x }\nputs sq.call(3)\nk = 5\nadd = ->(x : Int32) { x + k }\nk = 7\nputs add.call(1)\nxs = [1, 2, 3].map { |v| v * k }\nputs xs.sum\n" },
        { "break out of a disposable foreach", "l = List(Int32).new\nl << 1\nl << 2\nl << 3\ns = 0\nfor v in l\n  break if v == 2\n  s += v\nend\nputs s\nh = Hash(String, Int32).new\nh[\"a\"] = 1\nh[\"b\"] = 2\nfor k, v in h\n  next if v == 1\n  puts k\nend\n" },
        { "forks and parallel reductions", "def fib(n : Int32) : Int64\n  n < 2 ? n.to_i64 : fib(n - 1) + fib(n - 2)\nend\nputs fib(25)\nxs = Array(Float64).new(300000)\nfor i in 0...300000\n  xs[i] = i * 0.5\nend\nputs xs.par_sum { |x| x * 2.0 }\n" },
    };

    /// <summary>`h[k] += v` lowers to a `ref int` local over CollectionsMarshal.GetValueRefOrAddDefault; struct
    /// receivers in arrays (`ss[i].x += 3`, index evaluated once). IL only: the wasm backend has no ref locals, so
    /// this program is not in <see cref="Programs"/> (shared with WasmTests).</summary>
    [Fact]
    public void Ref_locals_and_struct_receivers() => Differential(
        "h = Hash(String, Int32).new\nh[\"a\"] = 1\nh[\"a\"] += 5\nputs h[\"a\"]\nstruct S\n  property x : Int32\n  def initialize(@x : Int32)\n  end\nend\nss = Array(S).new(3)\ndef nx(i : Int32) : Int32\n  puts \"nx\"\n  i + 1\nend\nss[nx(0)].x = 4\nss[nx(0)].x += 3\nputs ss[1].x\nputs -300_i16\n");

    [Theory, MemberData(nameof(Programs))]
    public void Programs_run_identically_under_both_backends(string name, string src)
    {
        _ = name;
        Differential(src);
    }

    [Theory]
    [InlineData("benchmarks/bend2/alice/nbody.al", "6")]
    [InlineData("benchmarks/bend2/alice/queens.al", "8")]
    [InlineData("benchmarks/bend2/alice/editdist.al", "6")]
    [InlineData("benchmarks/bend2/alice/mandelbrot.al", "6")]
    [InlineData("benchmarks/bend2/alice/hashmap.al", "6")]
    [InlineData("benchmarks/bend2/alice/raytrace.al", "6")]
    [InlineData("benchmarks/bend2/alice/bfs.al", "6")]
    [InlineData("benchmarks/bend2/alice/gameoflife.al", "6")]
    [InlineData("benchmarks/bend2/alice/kmeans.al", "6")]
    [InlineData("benchmarks/bend2/alice/lexer.al", "6")]
    [InlineData("benchmarks/bend2/alice/merkle.al", "6")]
    [InlineData("benchmarks/bend2/alice/symreg.al", "6")]
    [InlineData("benchmarks/bend2/alice/terrain.al", "6")]
    [InlineData("benchmarks/bend2/alice/tree-bitonic.al", "6")]
    [InlineData("benchmarks/bend2/alice/tree-matmul.al", "6")]
    [InlineData("benchmarks/bend2/alice/tree-radix.al", "6")]
    [InlineData("benchmarks/alice/binarytrees.al", "")]
    [InlineData("benchmarks/alice/wordfreq.al", "")]
    [InlineData("benchmarks/alice/parallel.al", "")]
    public void Benchmarks_run_identically(string file, string arg)
    {
        var path = Path.Combine(Root(), file);
        var files = AliceCompiler.LoadWithRequires([path]);
        var res = AirToIl.Compile(files, Path.GetFileNameWithoutExtension(file));
        Assert.True(res.Ok, string.Join("\n", res.Errors.Select(e => e.Message)));
        Verified(res.Pe!);
        var cs = AliceCompiler.Invoke(AliceCompiler.Load(AliceCompiler.Emit(AliceCompiler.TranspileAll(files), "cs", exe: true)), [arg]);
        Assert.Equal(Normalize(cs), Normalize(RunIl(res.Pe!, arg)));   // RESULT lines carry timings
    }

    [Fact]
    public void Image_has_a_pdb_and_sequence_points_into_the_al_file()
    {
        var res = Il("""
def f(x : Int32) : Int32
  y = x + 1
  y * 2
end
puts f(3)

""");
        using var pdb = MetadataReaderProvider.FromPortablePdbStream(new MemoryStream(res.Pdb!));
        var md = pdb.GetMetadataReader();
        Assert.Contains(md.Documents, h => md.GetString(md.GetDocument(h).Name).EndsWith("main.al"));
        var lines = md.MethodDebugInformation.SelectMany(h => md.GetMethodDebugInformation(h).GetSequencePoints()).Where(s => !s.IsHidden).Select(s => s.StartLine).ToHashSet();
        Assert.Contains(2, lines);
        Assert.Contains(5, lines);
        var locals = md.LocalVariables.Select(h => md.GetString(md.GetLocalVariable(h).Name)).ToList();
        Assert.Contains("y", locals);
    }

    [Fact]
    public void Unsupported_nodes_are_reported_with_their_kind()
    {
        var res = AirToIl.Compile([new SourceFile("main.al", "@[SoA]\nrecord struct P(x : Float64)\nputs 1\n")], "u");
        Assert.False(res.Ok);
        Assert.Contains(res.Errors, e => e.NodeKind.StartsWith("Decl:"));
        Assert.True(res.Stats.Unsupported.Count > 0);
    }

    [Fact]
    public void Disassembly_lists_methods_locals_and_sequence_points()
    {
        var res = Il("""
def f(n : Int32) : Int32
  s = 0
  i = 0
  while i < n
    s += i
    i += 1
  end
  s
end
puts f(4)

""");
        var text = IlDisassembler.Disassemble(res.Module!);
        Assert.Contains(".method public static hidebysig int32 f(int32 n)", text);
        Assert.Contains("// main.al:4:", text);
        Assert.Contains(".locals (", text);
        Assert.Contains("add.ovf", text);
    }

    // IL shape rules (/work/reports/il-shapes.md), asserted on the disassembly

    [Fact]
    public void R12_locals_are_never_zero_initialized()
    {
        var text = IlDisassembler.Disassemble(Il("""
def f(n : Int32) : Int32
  s = 0
  for i in 0...n
    s += i
  end
  s
end
puts f(3)
""").Module!);
        Assert.Contains(".locals (", text);
        Assert.DoesNotContain(".locals init", text);
    }

    [Fact]
    public void R7_self_recursion_has_no_tail_prefix()
    {
        var text = IlDisassembler.Disassemble(Il("""
def f(n : Int32, acc : Int32) : Int32
  return acc if n == 0
  f(n - 1, acc + n)
end
puts f(10, 0)
""").Module!);
        Assert.Contains("call Program::f", text);
        Assert.DoesNotContain("tail.", text);
    }

    [Fact]
    public void Select_of_one_and_zero_is_the_comparison_value()
    {
        var text = IlDisassembler.Disassemble(Il("""
def f(a : UInt32, b : UInt32) : UInt32
  a != b ? 1_u32 : 0_u32
end
puts f(1_u32, 2_u32)
""").Module!);
        int at = text.IndexOf("uint32 f(", StringComparison.Ordinal);
        string body = text[at..text.IndexOf("  }", at, StringComparison.Ordinal)];
        Assert.DoesNotContain("br", body);
        Assert.Contains("ceq", body);
    }

    [Fact]
    public void Hot_policy_is_shared_with_the_csharp_backend() => Assert.True(AirToIl.HotPolicyAvailable);

    // ------------------------------------------------------------------ allocations

    static long Measure<TA, TR>(Func<TA, TR> f, TA a)
    {
        for (int i = 0; i < 50; i++) f(a);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) f(a);
        return (GC.GetAllocatedBytesForCurrentThread() - before) / 100;
    }

    public static TheoryData<string, string, object, object> Kernels => new()
    {
        { "numeric conversions", "def f(x : Float64)\n  x.to_i32 + x.round.to_i64.to_i32 + x.floor.to_u8.to_i\nend", 2.6, 2 + 3 + 2 },
        { "wrapping ops", "def f(x : Int64)\n  (x &* 31) &+ 7 &- 1\nend", 2L, 68L },
        { "range each", "def f(n : Int32)\n  s = 0\n  (0...n).each { |i| s += i }\n  s\nend", 5, 10 },
        { "array max min sum", "def f(xs : Array(Int32))\n  xs.max + xs.min + xs.sum\nend", new[] { 3, 1, 2 }, 3 + 1 + 6 },
        { "list sum", "def f(xs : List(Int32))\n  xs.sum\nend", new List<int> { 1, 2, 3 }, 6 },
    };

    [Theory, MemberData(nameof(Kernels))]
    public void Kernels_allocate_nothing_under_il(string name, string src, object arg, object expected)
    {
        _ = name;
        var res = Il(src, "alloc", exe: false);
        Verified(res.Pe!);
        var m = AliceCompiler.Load(res.Pe!).GetType("Program")!.GetMethod("f")!;
        Assert.Equal(expected, m.Invoke(null, [arg]));
        var ta = m.GetParameters()[0].ParameterType;
        var del = m.CreateDelegate(typeof(Func<,>).MakeGenericType(ta, m.ReturnType));
        var measure = typeof(AirToIlTests).GetMethod(nameof(Measure), BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(ta, m.ReturnType);
        Assert.Equal(0L, (long)measure.Invoke(null, [del, arg])!);
    }

    // ------------------------------------------------------------------ corpus (reports, never fails)

    /// <summary>Timing lines differ run to run: digits are masked on lines that report time.</summary>
    static string Normalize(string s) => string.Join("\n", s.Split('\n').Select(l =>
        Regex.IsMatch(l, @"\b(ms|RESULT|best|time|elapsed|sec)\b", RegexOptions.IgnoreCase) ? Regex.Replace(l, @"\d+(\.\d+)?", "N") : l));

    static string RunProcess(string dll, string arg, int timeoutMs)
    {
        var psi = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(dll);
        if (arg.Length > 0) psi.ArgumentList.Add(arg);
        psi.Environment["ALICE_PAR"] = "0";
        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch (InvalidOperationException) { } return "<timeout>"; }
        return outTask.Result + (p.ExitCode != 0 ? "<exit " + p.ExitCode + ">" : "");
    }

    static void Deploy(string dir, string name, byte[] image)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".dll"), image);
        var rt = typeof(Alice.Runtime.Prelude).Assembly.Location;
        File.Copy(rt, Path.Combine(dir, Path.GetFileName(rt)), true);
        File.WriteAllText(Path.Combine(dir, name + ".runtimeconfig.json"),
            "{\"runtimeOptions\":{\"tfm\":\"net" + Environment.Version.Major + ".0\",\"framework\":{\"name\":\"Microsoft.NETCore.App\",\"version\":\"" + Environment.Version.Major + ".0.0\"}}}");
    }

    [Fact]
    public void Corpus_coverage_report()
    {
        var root = Root();
        var programs = new[] { "benchmarks/alice", "benchmarks/bend2/alice" }
            .SelectMany(d => Directory.GetFiles(Path.Combine(root, d), "*.al")).Order(StringComparer.Ordinal).ToList();
        var rows = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        var tmp = Path.Combine(Path.GetTempPath(), "alice-il-corpus-" + Guid.NewGuid().ToString("N"));
        Parallel.ForEach(programs, new ParallelOptions { MaxDegreeOfParallelism = 4 }, path =>
        {
            string rel = Path.GetRelativePath(root, path);
            string name = Path.GetFileNameWithoutExtension(path);
            string arg = rel.Contains("bend2") ? "6" : "";
            try
            {
                var files = AliceCompiler.LoadWithRequires([path]);
                var res = AirToIl.Compile(files, name);
                if (!res.Ok) { rows[rel] = "unsupported: " + res.Errors[0].NodeKind; return; }
                var verify = IlVerify(res.Pe!);
                var cs = AliceCompiler.Emit(AliceCompiler.TranspileAll(files), name, exe: true);
                Deploy(Path.Combine(tmp, rel, "cs"), name, cs);
                Deploy(Path.Combine(tmp, rel, "il"), name, res.Pe!);
                string a = Normalize(RunProcess(Path.Combine(tmp, rel, "cs", name + ".dll"), arg, 120_000));
                string b = Normalize(RunProcess(Path.Combine(tmp, rel, "il", name + ".dll"), arg, 120_000));
                rows[rel] = (a == b ? "identical" : "different") + (verify.Count > 0 ? $" (ILVerify: {verify.Count})" : "");
            }
            catch (Exception e) { rows[rel] = "error: " + e.GetType().Name; }
        });
        try { Directory.Delete(tmp, true); } catch (IOException) { }
        int same = rows.Values.Count(v => v.StartsWith("identical"));
        output.WriteLine($"IL corpus: {same}/{programs.Count} identical ({100.0 * same / programs.Count:0}%)");
        foreach (var kv in rows.OrderBy(k => k.Key, StringComparer.Ordinal)) output.WriteLine($"  {kv.Key}: {kv.Value}");
        Assert.True(programs.Count > 0);
    }

    /// <summary>`ALICE_IL_DEBUG=path.al dotnet test --filter Debug_one_file`: ILVerify findings of one program.</summary>
    [Fact]
    public void Debug_one_file()
    {
        var path = Environment.GetEnvironmentVariable("ALICE_IL_DEBUG");
        if (string.IsNullOrEmpty(path)) return;
        var res = AirToIl.Compile(AliceCompiler.LoadWithRequires([path]), Path.GetFileNameWithoutExtension(path));
        foreach (var e in res.Errors) output.WriteLine("unsupported: " + e.Message);
        if (res.Ok) foreach (var e in IlVerify(res.Pe!)) output.WriteLine(e);
        if (Environment.GetEnvironmentVariable("ALICE_IL_DUMP") == "1" && res.Module != null) output.WriteLine(IlDisassembler.Disassemble(res.Module));
        if (Environment.GetEnvironmentVariable("ALICE_IL_BYTES") is { Length: > 0 } method && res.Pe != null)
        {
            using var pe = new PEReader(new MemoryStream(res.Pe));
            var md = pe.GetMetadataReader();
            foreach (var h in md.MethodDefinitions)
            {
                var m = md.GetMethodDefinition(h);
                if (md.GetString(m.Name) != method || m.RelativeVirtualAddress == 0) continue;
                var il = pe.GetMethodBody(m.RelativeVirtualAddress).GetILBytes()!;
                output.WriteLine($"{method}: {il.Length} bytes");
                int i = 0;
                while (i < il.Length)
                {
                    int at = i;
                    short v = il[i] == 0xFE ? unchecked((short)(0xFE00 | il[i + 1])) : il[i];
                    i += il[i] == 0xFE ? 2 : 1;
                    var op = Ops[v];
                    int size = op.OperandType switch
                    {
                        System.Reflection.Emit.OperandType.InlineNone => 0,
                        System.Reflection.Emit.OperandType.ShortInlineBrTarget or System.Reflection.Emit.OperandType.ShortInlineI or System.Reflection.Emit.OperandType.ShortInlineVar => 1,
                        System.Reflection.Emit.OperandType.InlineVar => 2,
                        System.Reflection.Emit.OperandType.InlineI8 or System.Reflection.Emit.OperandType.InlineR => 8,
                        System.Reflection.Emit.OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, i),
                        _ => 4,
                    };
                    string t = op.OperandType == System.Reflection.Emit.OperandType.ShortInlineBrTarget ? " -> " + (i + 1 + (sbyte)il[i])
                        : op.OperandType == System.Reflection.Emit.OperandType.InlineBrTarget ? " -> " + (i + 4 + BitConverter.ToInt32(il, i)) : "";
                    output.WriteLine($"  {at:0000}: {op.Name}{t}");
                    i += size;
                }
            }
        }
    }

    // ------------------------------------------------------------------ CLR generic method inference (D1.11)

    [Fact]
    public void Clr_generic_method_inferred_from_array_or_list() => Differential("""
        using Alice.Runtime
        xs = [1.0, 2.0, 3.0, 4.0]
        puts Simd.Dot(xs, xs)
        ys : Array(Double) = [5.0, 6.0]
        puts Simd.Sum(ys)
        """);

    [Fact]
    public void Clr_generic_method_inferred_from_scalar() => Differential("""
        def apply<T>(x : T, f : Func(T, T)) : T
          f.(x)
        end
        puts apply(5, ->(x : Int32) { x * 2 })
        puts apply("hi", ->(s : String) { s + "!" })
        """);

    [Fact]
    public void Clr_generic_method_inferred_with_two_parameters_unifying() => Differential("""
        puts Math.Max(3, 7)
        puts Math.Min(2.5, 1.1)
        """);

    [Fact]
    public void Clr_generic_method_inferred_from_lambda() => Differential("""
        xs = [1, 2, 3, 4]
        squared = xs.Select(->(x : Int32) { x * x }).ToList()
        puts squared.Count
        """);

    [Fact]
    public void Clr_generic_method_with_constraint_inferred() => Differential("""
        using Alice.Runtime
        xs = [10.0, 20.0, 30.0]
        puts Simd.Sum(xs)
        ys = [1_i64, 2_i64, 3_i64]
        puts Simd.Sum(ys)
        """);

    [Fact]
    public void Clr_generic_method_with_nested_generic_inferred() => Differential("""
        matrix = [[1, 2], [3, 4]]
        puts matrix[0][1]
        flat = matrix.SelectMany(->(row : Array(Int32)) { row }).ToList()
        puts flat.Count
        """);

    // ------------------------------------------------------------------ two-phase inference (T from receiver/args, R from lambda)

    [Fact]
    public void Two_phase_inference_map_with_lambda() => Differential("""
        xs = [1, 2, 3]
        ys = xs.map { |x| x * 2.0 }
        puts ys[0]
        puts ys.size
        """);

    [Fact]
    public void Two_phase_inference_map_returns_tuple() => Differential("""
        xs = [1, 2, 3]
        pairs = xs.map { |x| (x, x * 2) }
        puts pairs[0].Item1
        puts pairs[0].Item2
        """);

    [Fact]
    public void Two_phase_inference_chained_map_select() => Differential("""
        xs = [1, 2, 3, 4]
        result = xs.map { |x| x * 2 }.select { |y| y > 4 }
        puts result.size
        puts result[0]
        """);

    [Fact]
    public void Two_phase_inference_aggregate_two_lambdas() => Differential("""
        xs = [1, 2, 3, 4]
        sum = xs.Aggregate(0, ->(acc : Int32, x : Int32) { acc + x })
        puts sum
        """);

    [Fact]
    public void Two_phase_inference_nested_lambda() => Differential("""
        xs = [1, 2, 3]
        nested = xs.map { |x| [x, x * 2].map { |y| y + 1 } }
        puts nested[0][0]
        puts nested.size
        """);
}
