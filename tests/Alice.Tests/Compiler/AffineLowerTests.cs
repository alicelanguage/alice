using System.Reflection;
using Alice.Compiler;
using Alice.Compiler.Backend;
using Alice.Compiler.Backend.Il;
using Alice.Compiler.Lowering;
using Alice.Compiler.Mir;
using Alice.Tests.Backends;
using Xunit.Abstractions;

namespace Alice.Tests.Compiler;

/// <summary>
/// Affine arena lowering (ADR 0037 step 2, IL shapes ADR 0040 section 2) from the binder's facts: dumps of the arena
/// nodes, both backends against each other and against records (<see cref="AffinePlan.Disable"/>), with recursive and
/// shallow Drops, ILVerify on the arena IL, 0 allocations in steady state, and the five bend2 ports.
/// </summary>
[Collection("console")]
public class AffineLowerTests(ITestOutputHelper output)
{
    /// <summary>Runs <paramref name="f"/> with arenas on (the binder's facts) or off (records); recursive or shallow Drops.</summary>
    static T Affine<T>(Func<T> f, bool on = true, bool deep = true)
    {
        var (pd, pdeep) = (AffinePlan.Disable, AffinePlan.Deep);
        AffinePlan.Disable = !on;
        AffinePlan.Deep = deep;
        try { return f(); }
        finally { (AffinePlan.Disable, AffinePlan.Deep) = (pd, pdeep); }
    }

    static string RunCs(string src, bool arena, params string[] args) =>
        Affine(() => AliceCompiler.Invoke(AliceCompiler.Load(AliceCompiler.Emit([AliceCompiler.Transpile(src)], "cs", exe: true)), args), arena, AffinePlan.Deep ?? true);

    static byte[] IlImage(IReadOnlyList<SourceFile> files, string name, bool exe = true)
    {
        var res = Affine(() => AirToIl.Compile(files, name, exe), deep: AffinePlan.Deep ?? true);
        Assert.True(res.Ok, "IL backend: " + string.Join("\n", res.Errors.Select(e => e.Message)));
        var decode = AirToIlTests.DecodeCheck(res.Pe!);
        Assert.True(decode.Count == 0, "IL decode check: " + string.Join(", ", decode));
        var errors = AirToIlTests.IlVerify(res.Pe!);
        Assert.True(errors.Count == 0, "ILVerify:\n" + string.Join("\n", errors));
        return res.Pe!;
    }

    static string RunIl(string src, params string[] args) => AliceCompiler.Invoke(AliceCompiler.Load(IlImage([new SourceFile("main.al", src)], "il")), args);

    static string Dump(string src) => Affine(() => MirDump.Collect(() => AliceCompiler.Transpile(src)), deep: AffinePlan.Deep ?? true);

    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Alice.sln"))) d = d.Parent;
        return d!.FullName;
    }

    // a fork-free tree build + sum: the steady state allocates nothing (the sum's Takes feed the next build)
    const string Tree = """
enum Tree
  Leaf(v : UInt32)
  Node(l : Tree, r : Tree)
end

def build(d : Int32, x : UInt32) : Tree
  return Tree::Leaf(x) if d == 0
  l = build(d - 1, x &* 2_u32)
  Tree::Node(l, build(d - 1, x &* 2_u32 &+ 1_u32))
end

def total(t : Tree, acc : UInt32) : UInt32
  case t
  when Tree::Leaf(v) then acc &+ v
  when Tree::Node(l, r) then total(r, acc &+ total(l, 0_u32))
  end
end

def run(n : Int32) : UInt32
  t = build(n, 1_u32)
  s = total(t, 0_u32)
  s
end

puts run(ARGV.size > 0 ? ARGV[0].to_i : 10)

""";

    // in-place rebuild after a Take (tree-bitonic warp_zip), a borrowing case (the scrutinee is returned in one arm),
    // the drop of a borrowed scrutinee in a nested arm (tree-radix merge), and field-less variants
    const string Merge = """
enum M
  Free
  Busy
  Node(l : M, r : M)
end

def merge(a : M, b : M) : M
  case a
  when M::Free then b
  when M::Busy then M::Busy
  when M::Node(al, ar)
    case b
    when M::Free then a
    when M::Busy then M::Busy
    when M::Node(bl, br)
      l = merge(al, bl)
      M::Node(l, merge(ar, br))
    end
  end
end

def path(n : UInt32, d : Int32) : M
  r : M = M::Busy
  free : M = M::Free
  i = 0
  while i < d
    r = (n >> i) & 1_u32 == 0_u32 ? M::Node(r, free) : M::Node(free, r)
    i += 1
  end
  r
end

def count(m : M) : UInt32
  case m
  when M::Free then 0_u32
  when M::Busy then 1_u32
  when M::Node(l, r) then count(r) &+ count(l)
  end
end

def zip(a : M, b : M) : M
  case a
  when M::Node(a0, a1)
    case b
    when M::Node(b0, b1)
      x = M::Node(a0, b0)
      M::Node(x, M::Node(a1, b1))
    else M::Free
    end
  else M::Free
  end
end

acc = M::Free
k = 0_u32
while k < 64_u32
  acc = merge(acc, path(k &* 2654435761_u32, 8))
  k = k &+ 1_u32
end
puts count(acc)
puts count(zip(path(5_u32, 4), path(9_u32, 4)))

""";

    // every word kind through slots and packed payloads
    const string Kinds = """
enum V
  I(x : Int32)
  B(b : Bool)
  F(f : Float32)
  C(c : Char)
  S(s : Int16)
  Wide(a : Int64, d : Float64, u : UInt8, n : Int8, t : V)
  Pair(x : Int32, y : Int32)
  Nil
end

def show(v : V) : String
  case v
  when V::I(x) then "I#{x}"
  when V::B(b) then "B#{b}"
  when V::F(f) then "F#{f}"
  when V::C(c) then "C#{c}"
  when V::S(s) then "S#{s}"
  when V::Wide(a, d, u, n, t) then "W#{a},#{d},#{u},#{n}," + show(t)
  when V::Pair(x, y) then "P#{x + y}"
  when V::Nil then "N"
  end
end

puts show(V::I(-7))
puts show(V::B(true))
puts show(V::F(-1.5_f32))
puts show(V::C('z'))
puts show(V::S(-300_i16))
puts show(V::Wide(-5_000_000_000_i64, -2.25, 200_u8, -100_i8, V::Pair(-3, 10)))
puts show(V::Nil)

""";

    // `scope` in the main body: an arena region, reset at `end`
    const string Region = """
enum T
  Lf(v : UInt32)
  Nd(l : T, r : T)
end

def mk(d : Int32) : T
  return T::Lf(1_u32) if d == 0
  l = mk(d - 1)
  T::Nd(l, mk(d - 1))
end

def peek(t : T) : UInt32
  case t
  when T::Lf(v) then v
  when T::Nd(l, r) then peek(l) &+ peek(r)
  end
end

s = 0_u32
i = 0
while i < 4
  scope
    t = mk(8)
    s = s &+ peek(t) &+ peek(t)
  end
  i += 1
end
puts s

""";

    // an escaping use (string interpolation of the enum) demotes it to records
    const string Escapes = """
enum E
  A(x : Int32)
  B(l : E, r : E)
end

def size(e : E) : Int32
  case e
  when E::A(x) then x
  when E::B(l, r) then size(l) + size(r)
  end
end

e = E::B(E::A(1), E::A(2))
puts size(e)
puts "#{e}"

""";

    [Fact]
    public void Dump_shows_packed_terms_slot_allocs_and_take_or_borrow_per_arm()
    {
        string tree = Dump(Tree);
        output.WriteLine(tree);
        Assert.Contains("(term-pack Leaf 0 u32 (local x)", tree);
        Assert.Contains("(arena-alloc Tree Node 1 w0:term:(local l):ulong w1:term:(call build", tree);
        Assert.Contains("decl __tag", tree);
        Assert.Contains("(term-tag (local t)", tree);
        // `total` only reads its parameter (inferred borrow): its arms borrow, and `run` drops the whole tree after the call
        Assert.Contains("(arena-borrow Tree live (local t)", tree);
        Assert.Contains("(arena-drop Tree deep 3/3 (local t)", tree);
        Assert.Contains("(slot-word 1 term (local __slot", tree);
        Assert.Contains("(term-payload u32 (local t)", tree);
        string merge = Dump(Merge);
        output.WriteLine(merge);
        // `a` is returned in one nested arm: borrowed in its Node arm, its slot dropped where it dies
        Assert.Contains("(arena-borrow M live (local a)", merge);
        Assert.Contains("(arena-drop M deep 0/3 (local a)", merge);
        Assert.Contains("(arena-take M frees live (local b)", merge);
        Assert.Contains("(term-pack Free 0)", merge);
        string region = Dump(Region);
        Assert.Contains("(arena-region)", region);
        Assert.Contains("(arena-region-end (local __region", region);
        // a region in the program: slot reads keep the stale check
        Assert.DoesNotContain(" live ", region);
    }

    [Fact]
    public void Plan_demotes_an_escaping_enum_and_keeps_records()
    {
        string dump = Dump(Escapes);
        Assert.DoesNotContain("arena-", dump);
        Assert.Equal(RunCs(Escapes, arena: false), RunCs(Escapes, arena: true));
        var (plan, binder) = Affine(() =>
        {
            var b = new Alice.Compiler.Semantics.Binder([Alice.Compiler.Syntax.Parser.ParseFile(Tree + Escapes.Replace("puts size(e)", "puts size(e) + run(2).to_i"), "main.al")], new Alice.Compiler.Semantics.ClrTypes(null));
            b.Bind();
            return (AffinePlan.For(b)!, b);
        });
        Assert.Contains(binder.Affine.Explain(), l => l.StartsWith("E: record (demoted:"));
        Assert.Equal(["enum Tree: arena (2-word slot; Leaf packed, Node slot; recursive drop)"], plan.Explain);
    }

    [Fact]
    public void Printer_spells_runtime_calls_on_term_words()
    {
        var l = new AirSlab { Enum = "Tree", Words = 2 };
        Assert.Equal("0x8100000000000000UL", Printer.E(new AirTermPack { Variant = "B", Tag = 1 }));
        Assert.Equal("global::Alice.Runtime.Term.Pack(0, (x)).Raw", Printer.E(new AirTermPack { Variant = "L", Tag = 0, Payload = AirLeaf.Name("x") }));
        Assert.Equal("unchecked((int)(t >> 56))", Printer.E(new AirTermTag { Term = AirLeaf.Name("t") }));
        Assert.Equal("global::Alice.Runtime.Arena<(ulong, ulong)>.Alloc(1, ((a), (b))).Raw",
            Printer.E(new AirArenaAlloc { Layout = l, Variant = "Node", Tag = 1, Fields = { AirLeaf.Name("a"), AirLeaf.Name("b") }, Kinds = { WordKind.Term, WordKind.Term } }));
        Assert.Equal("global::Alice.Runtime.Arena<(ulong, ulong)>.TakeValue(t)", Printer.E(new AirArenaTake { Layout = l, Term = AirLeaf.Name("t") }));
        Assert.Equal("global::Alice.Runtime.Arena<(ulong, ulong)>.Borrow(new global::Alice.Runtime.Term(t))", Printer.E(new AirArenaBorrow { Layout = l, Term = AirLeaf.Name("t") }));
        Assert.Equal("global::Alice.Runtime.Arena<(ulong, ulong)>.Drop(t)", Printer.E(new AirArenaDrop { Layout = l, Term = AirLeaf.Name("t") }));
        Assert.Equal("unchecked((int)(uint)s.Item2)", Printer.E(new AirSlotWord { Layout = l, Slot = AirLeaf.Name("s"), Word = 1, Kind = WordKind.I32 }));
    }

    public static TheoryData<string, string, string> Programs => new()
    {
        { "tree build and sum", Tree, "10" },
        { "merge, borrow, drop, rebuild", Merge, "" },
        { "word kinds", Kinds, "" },
        { "scope region", Region, "" },
        { "demoted enum", Escapes, "" },
        { "guarded arms", Guards, "" },
    };

    // a guard that fails falls through to the next arm with the node intact (guarded arms borrow), both on a named
    // parameter and on a temporary subject
    const string Guards =
        "enum T\n  Leaf(v : Int64)\n  Node(l : T, r : T)\nend\n" +
        "def mk(d : Int32, x : Int64) : T\n  return T::Leaf(x) if d == 0\n  T::Node(mk(d - 1, x * 2), mk(d - 1, x * 2 + 1))\nend\n" +
        "def f(t : T) : Int64\n  case t\n  in T::Leaf(v) if v > 5 then v * 100\n  in T::Leaf(v) then v\n" +
        "  in T::Node(l, r) if f(l) > 1000 then f(r)\n  in T::Node(l, r) then f(l) + f(r)\n  end\nend\n" +
        "def g(d : Int32) : Int64\n  case mk(d, 1)\n  in T::Leaf(v) if v > 0 then v\n  in T::Node(l, r) if d > 2 then 7\n  in T::Node(l, r) then f(l) - f(r)\n  in T::Leaf(v) then 0\n  end\nend\n" +
        "puts f(mk(4, 1))\nputs f(mk(2, 1))\nputs g(2)\nputs g(3)\nputs g(0)\n";

    [Theory, MemberData(nameof(Programs))]
    public void Arena_programs_print_what_records_print_under_both_backends(string name, string src, string arg)
    {
        _ = name;
        string[] args = arg == "" ? [] : [arg];
        string records = RunCs(src, arena: false, args);
        Assert.Equal(records, RunCs(src, arena: true, args));
        Assert.Equal(records, RunIl(src, args));
        Assert.Equal(records, Affine(() => RunCs(src, arena: true, args), deep: false));
        Assert.Equal(records, Affine(() => RunIl(src, args), deep: false));
        output.WriteLine(records);
    }

    [Theory]
    [InlineData("tree-radix", "10")]
    [InlineData("tree-bitonic", "10")]
    [InlineData("tree-matmul", "3")]
    [InlineData("merkle", "8")]
    [InlineData("symreg", "8")]
    public void Bend2_ports_keep_their_checksums(string port, string arg)
    {
        var path = Path.Combine(Root(), "benchmarks/bend2/alice", port + ".al");
        var files = AliceCompiler.LoadWithRequires([path]);
        string records = Affine(() => AliceCompiler.Invoke(AliceCompiler.Load(AliceCompiler.Emit(AliceCompiler.TranspileAll(files), "cs", exe: true)), [arg]), on: false);
        string dump = Affine(() => MirDump.Collect(() => AliceCompiler.TranspileAll(files)));
        Assert.Contains("(arena-", dump);   // the port really runs on arenas
        string cs = Affine(() => AliceCompiler.Invoke(AliceCompiler.Load(AliceCompiler.Emit(AliceCompiler.TranspileAll(files), "cs", exe: true)), [arg]));
        string il = AliceCompiler.Invoke(AliceCompiler.Load(IlImage(files, port)), [arg]);
        Assert.Equal(records, cs);
        Assert.Equal(records, il);
        string shallow = Affine(() => AliceCompiler.Invoke(AliceCompiler.Load(AliceCompiler.Emit(AliceCompiler.TranspileAll(files), "cs", exe: true)), [arg]), deep: false);
        Assert.Equal(records, shallow);
    }

    static long Steady(Func<int, uint> run, int n, uint expect)
    {
        for (int i = 0; i < 20; i++) Assert.Equal(expect, run(n));
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++) run(n);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    static Func<int, uint> Run(Assembly asm) =>
        asm.GetType("Program")!.GetMethod("run", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.CreateDelegate<Func<int, uint>>();

    // a list built outside `scope` and consumed inside it (`inc` owns its parameter: a Take): its slots belong to the
    // outer level, so they must go back to the outer free list (Arena.Take's RegionBase check), not to the region's
    // list that the reset discards
    const string OuterTakenInScope = """
enum L
  Nil
  Cons(v : UInt32, rest : L)
end

def mk(n : Int32) : L
  return L::Nil if n == 0
  L::Cons(n.to_u32, mk(n - 1))
end

def inc(l : L, stop : Bool) : L
  return l if stop
  case l
  when L::Nil then L::Nil
  when L::Cons(v, rest) then L::Cons(v &+ 1_u32, inc(rest, false))
  end
end

def total(l : L) : UInt32
  case l
  when L::Nil then 0_u32
  when L::Cons(v, rest) then v &+ total(rest)
  end
end

s = 0_u32
i = 0
while i < 50
  l = mk(64)
  scope
    s = s &+ total(inc(l, false))
  end
  i += 1
end
puts s

""";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_outer_node_taken_inside_a_scope_is_reused_after_it_under_both_backends(bool deep)
    {
        // review chat #2800: the IL Take pushed every freed slot onto the current free list, so 50 rounds leaked
        // 50 x 64 outer slots under --backend il while the C# backend (Arena.Take) reused them
        static (string Out, uint Growth) Measure(Func<string> run)
        {
            uint before = Alice.Runtime.Arena<(ulong, ulong)>.Used;
            string o = run();
            return (o, Alice.Runtime.Arena<(ulong, ulong)>.Used - before);
        }
        var cs = Affine(() => Measure(() => RunCs(OuterTakenInScope, arena: true)), deep: deep);
        var il = Affine(() => Measure(() => RunIl(OuterTakenInScope)), deep: deep);
        Assert.Equal(cs.Out, il.Out);
        Assert.True(cs.Growth <= 64, $"C# backend: the lane grew by {cs.Growth} slots");
        Assert.True(il.Growth <= 64, $"IL backend: the lane grew by {il.Growth} slots (C#: {cs.Growth})");
    }

    [Fact]
    public void Inlined_IL_take_and_borrow_detect_a_freed_slot()
    {
        // the IL backend tests and sets the freed bit inline (lane-cursor chunk): a second Take of the same term, or a
        // Borrow after it, must still fail-stop as Arena.Take/Borrow do (INVARIANTS #9), and a reused slot is live again
        string src = OuterTakenInScope[..OuterTakenInScope.IndexOf("s = 0_u32", StringComparison.Ordinal)] +
            "def eat(l : L, stop : Bool) : L\n  return l if stop\n  case l\n  when L::Nil then L::Nil\n  when L::Cons(v, rest) then eat(rest, false)\n  end\nend\n";
        var asm = AliceCompiler.Load(IlImage([new SourceFile("main.al", src)], "freedil", exe: false));
        var p = asm.GetType("Program")!;
        object? Call(string name, params object[] args)
        {
            try { return p.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args); }
            catch (TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; }
        }
        var t = (ulong)Call("mk", 3)!;
        Assert.Equal(6u, (uint)Call("total", t)!);                   // inline Borrow of live slots
        Call("eat", t, false);                                        // inline Takes free all three
        Assert.Contains("double free", Assert.Throws<InvalidOperationException>(() => Call("eat", t, false)).Message);
        Assert.Contains("freed", Assert.Throws<InvalidOperationException>(() => Call("total", t)).Message);
        var u = (ulong)Call("mk", 3)!;                                // pops the freed slots: their bits are cleared
        Assert.Equal((uint)t, (uint)u);
        Assert.Equal(6u, (uint)Call("total", u)!);
        Call("eat", u, false);
    }

    [Fact]
    public void Tree_build_and_sum_allocates_nothing_in_steady_state()
    {
        string src = Tree.Replace("puts run(ARGV.size > 0 ? ARGV[0].to_i : 10)\n", "");
        uint expect = 0;
        for (uint x = 1u << 16; x < 2u << 16; x++) expect += x;   // leaves of build(16, 1); 70 leaking runs would outgrow the first 2^20-slot chunk
        var cs = Affine(() => AliceCompiler.Load(AliceCompiler.Emit([AliceCompiler.Transpile(src)], "alloc", exe: false)));
        Assert.Equal(0, Steady(Run(cs), 16, expect));
        var il = AliceCompiler.Load(IlImage([new SourceFile("main.al", src)], "allocil", exe: false));
        Assert.Equal(0, Steady(Run(il), 16, expect));
    }
}
