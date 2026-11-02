using Alice.Compiler;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Tests.Compiler;

/// <summary>
/// Affine arena analysis (ADR 0037, <c>Semantics/Binder/Binder.Affine*.cs</c>): which sum types can live in a struct
/// arena, how each `case` arm reads its scrutinee (Take frees, Borrow reads in place, Keep reads a shared value), what
/// each use does (Move, Keep, BorrowArg), where an owned value dies unconsumed (Drop), and the whole-program demotion
/// to records, which `affine enum` turns into AL0240-AL0243. The pass records facts only: emitted C# is unchanged.
/// </summary>
[Collection("console")]
public class AffineTests
{
    static Binder Bind(string src, string file = "main.al")
    {
        var b = new Binder([Parser.ParseFile(src, file)], new ClrTypes(null));
        b.Bind();
        return b;
    }

    static Binder BindFile(string path) => Bind(File.ReadAllText(path), path);

    static string Root()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "Alice.sln"))) d = d.Parent;
        return d!.FullName;
    }

    static string Port(string name) => Path.Combine(Root(), "benchmarks", "bend2", "alice", name + ".al");

    static ArenaLayout Layout(Binder b, string name) => b.Affine.Layouts.Values.Single(l => l.Enum.Name == name);

    static Diagnostic Error(string src)
    {
        var e = Assert.Throws<CompileException>(() => Bind(src));
        return e.Diagnostics[0];
    }

    /// <summary>The mode of the `when` on `line` whose first pattern names `variant`.</summary>
    static ArmMode ArmAt(Binder b, int line, string variant) =>
        b.Affine.Arms.Single(kv => kv.Key.Line == line && PatternName(kv.Key.Patterns[0]).EndsWith(variant)).Value;

    static string PatternName(Expr p) => p switch { NewExpr ne => ne.TypeRef.Name, ConstExpr c => c.Ref.Name, _ => "" };

    static List<string> DropsAt(Binder b, int line) =>
        b.Affine.Drops.Where(kv => kv.Key.Line == line).SelectMany(kv => kv.Value).Select(l => l.Name).ToList();

    const string Tree = """
        enum Tree
          Leaf(v : UInt32)
          Node(l : Tree, r : Tree)
        end
        """;

    const string Total = """

        def total(t : Tree) : UInt32
          case t
          when Tree::Leaf(v) then v
          when Tree::Node(l, r) then total(l) &+ total(r)
          end
        end

        """;

    // ---- eligibility ----

    [Theory]
    [InlineData("v : UInt32", true)]
    [InlineData("v : Float64", true)]
    [InlineData("v : Bool", true)]
    [InlineData("p : P", true)]           // record struct of unmanaged fields
    [InlineData("c : Color", true)]       // plain enum
    [InlineData("s : String", false)]
    [InlineData("k : Box", false)]        // class
    [InlineData("xs : Array(Int32)", false)]
    [InlineData("o : Other", true)]       // another eligible sum type
    [InlineData("w : Words", false)]      // a sum type that is not eligible
    public void Eligibility_follows_the_payload_rule(string field, bool arena)
    {
        var b = Bind($$"""
            record struct P(x : Int32, y : Int32)
            enum Color
              Red
              Green
            end
            class Box
            end
            enum Other
              A(v : UInt32)
              B(l : Other)
            end
            enum Words
              W(s : String)
            end
            enum T
              Leaf({{field}})
              Node(l : T, r : T)
            end
            puts 1
            """);
        Assert.Equal(arena, Layout(b, "T").Arena);
    }

    [Fact]
    public void Mutual_recursion_is_eligible_and_demotion_crosses_it()
    {
        const string src = """
            enum Ev
              Zero
              Succ(o : Od)
            end
            enum Od
              One(e : Ev)
              Pair(a : Ev, b : Od)
            end
            """;
        var b = Bind(src + "\nputs 1\n");
        Assert.True(Layout(b, "Ev").Arena);
        Assert.True(Layout(b, "Od").Arena);
        // one escaping use of Od demotes Ev too: an Ev node cannot hold an Od record
        var d = Bind(src + "\nx = Od::One(Ev::Zero)\nputs x\n");
        Assert.False(Layout(d, "Od").Arena);
        Assert.False(Layout(d, "Ev").Arena);
        Assert.Contains("passed to .NET `puts`", Layout(d, "Od").Demotion);
    }

    [Fact]
    public void Packing_and_slot_layout()
    {
        var b = Bind("""
            enum M
              Emp
              Leaf(v : UInt32)
              Wide(v : UInt64)
              Real(f : Float32)
              Node(h : UInt32, l : M, r : M)
              Quad(a : M, b : M, c : M, d : M)
            end
            puts 1
            """);
        var l = Layout(b, "M");
        var v = l.Variants.ToDictionary(x => x.Sym.Name);
        Assert.True(v["Emp"].Packed);
        Assert.Null(v["Emp"].PackedField);
        Assert.True(v["Leaf"].Packed);
        Assert.Equal("uint", v["Leaf"].PackedField!.Cs);
        Assert.False(v["Wide"].Packed);   // 64 bits do not fit the payload half of the term
        Assert.True(v["Real"].Packed);
        Assert.False(v["Node"].Packed);
        Assert.Equal([0, 1, 2, 3, 4, 5], l.Variants.Select(x => x.Tag));
        // slot = union by position: Wide's UInt64, Node's UInt32 and two terms, Quad's four terms share the term fields
        Assert.Equal(4, l.Slot.Count(s => s.IsTerm));
        Assert.Equal(new[] { "ulong", "uint" }, l.Slot.Where(s => !s.IsTerm).Select(s => s.Type.Cs));
        Assert.Equal(4, v["Quad"].SlotIndex.Distinct().Count());
        Assert.All(v["Quad"].SlotIndex, i => Assert.True(l.Slot[i].IsTerm));
        Assert.Equal(v["Node"].SlotIndex[1..], v["Quad"].SlotIndex[..2]);
    }

    // ---- Take / Borrow / Move / Drop ----

    [Fact]
    public void A_case_on_a_value_never_used_again_takes_it()
    {
        var b = Bind(Tree + Total + "puts total(Tree::Node(Tree::Leaf(1_u32), Tree::Leaf(2_u32)))\n");
        Assert.True(b.Affine.IsArena(Layout(b, "Tree").Enum));
        Assert.Equal(ArmMode.Take, ArmAt(b, 7, "Leaf"));
        Assert.Equal(ArmMode.Take, ArmAt(b, 8, "Node"));
        Assert.All(b.Affine.Uses.Where(u => u.Key.Name is "l" or "r"), u => Assert.Equal(AffineOp.Move, u.Value));
    }

    [Fact]
    public void A_case_whose_arm_reuses_the_value_borrows_in_that_arm_only()
    {
        // tree-bitonic warp_node: the Leaf arm returns t itself, the Node arm consumes it
        var b = Bind(Tree + """

            def keep_or_sum(t : Tree) : Tree
              case t
              when Tree::Leaf(v) then t
              when Tree::Node(l, r) then Tree::Node(r, l)
              end
            end
            puts 1
            """);
        Assert.Equal(ArmMode.Borrow, ArmAt(b, 7, "Leaf"));
        Assert.Equal(ArmMode.Take, ArmAt(b, 8, "Node"));
    }

    [Fact]
    public void A_borrowed_scrutinee_is_dropped_where_it_dies_and_its_fields_move()
    {
        // tree-radix merge: `a` is returned by the inner Free arm, so the outer Node arm borrows; in the inner Node arm
        // `a` is dead, so it is dropped there and its fields al/ar own their nodes (moved into the recursive calls)
        var b = BindFile(Port("tree-radix"));
        Assert.Equal(ArmMode.Borrow, ArmAt(b, 37, "Node"));
        Assert.Equal(ArmMode.Take, ArmAt(b, 41, "Node"));
        Assert.Equal(["a"], DropsAt(b, 41));
        Assert.Equal(["a", "al", "ar"], DropsAt(b, 40));   // Busy arm: nothing of a is used
        Assert.Equal(["b"], DropsAt(b, 36));
        Assert.Equal(AffineOp.Move, b.Affine.Uses.Single(u => u.Key.Name == "al").Value);
    }

    [Fact]
    public void Packed_locals_copy_and_borrow_arguments_lend()
    {
        var b = Bind(Tree + """

            def size(borrow t : Tree) : UInt32
              case t
              when Tree::Leaf(v) then 1_u32
              when Tree::Node(l, r) then size(l) &+ size(r)
              end
            end
            def twice(v : UInt32) : Tree
              leaf = Tree::Leaf(v)
              Tree::Node(leaf, leaf)
            end
            t = twice(3_u32)
            puts size(t)
            """);
        Assert.True(Layout(b, "Tree").Arena);   // leaf is packed: two copies of one word, not a shared node
        Assert.Equal(ArmMode.Borrow, ArmAt(b, 7, "Leaf"));
        Assert.Equal(ArmMode.Borrow, ArmAt(b, 8, "Node"));
        Assert.Equal(AffineOp.BorrowArg, b.Affine.Uses.Single(u => u.Key.Name == "t").Value);
        Assert.Equal(["t"], DropsAt(b, 16));    // t's last use only read it: dropped after the statement
        Assert.True(b.Affine.BorrowParam(((MethodSym)b.Program.Methods["size"][0]).Params[0]));
    }

    [Fact]
    public void A_guarded_arm_borrows_and_a_value_set_on_one_path_is_not_dropped()
    {
        var b = Bind(Tree + Total + """
            def pick(t : Tree, c : Bool) : UInt32
              case t
              in Tree::Node(l, r) if c then total(l)
              when Tree::Node(l, r) then total(r)
              when Tree::Leaf(v) then v
              end
            end
            def maybe(c : Bool) : UInt32
              if c
                u = Tree::Node(Tree::Leaf(1_u32), Tree::Leaf(2_u32))
              end
              7_u32
            end
            puts pick(Tree::Leaf(1_u32), true) &+ maybe(false)
            """);
        Assert.Equal(ArmMode.Borrow, ArmAt(b, 13, "Node"));   // the guard may fail and fall to the next arm
        Assert.Equal(ArmMode.Take, ArmAt(b, 14, "Node"));
        // ...so t is not dropped at the guarded arm's entry, only after its body
        Assert.DoesNotContain(b.Affine.Drops, kv => kv.Key is When { Line: 13 } && kv.Value.Any(l => l.Name == "t"));
        Assert.Contains(b.Affine.Drops, kv => kv.Key is ExprStmt { Line: 13 } && kv.Value.Any(l => l.Name == "t"));
        Assert.Equal(["u"], DropsAt(b, 20));                   // dropped where it dies, inside the branch
        Assert.Empty(DropsAt(b, 19));                          // not after the `if`: unassigned on the other path
    }

    [Fact]
    public void Ternary_branches_each_move_once()
    {
        var b = BindFile(Port("tree-bitonic"));
        Assert.True(Layout(b, "Tree").Arena);
        Assert.Equal(4, b.Affine.Uses.Count(u => u.Key.Line == 29 && u.Value == AffineOp.Move));
    }

    // ---- Keep ----

    [Fact]
    public void Under_affine_a_read_after_a_move_keeps_the_value()
    {
        var b = Bind("affine " + Tree + Total + """
            def size(borrow t : Tree) : UInt32
              case t
              when Tree::Leaf(v) then 1_u32
              when Tree::Node(l, r) then size(l) &+ size(r)
              end
            end
            t = Tree::Node(Tree::Leaf(1_u32), Tree::Leaf(2_u32))
            a = total(t)
            case t
            when Tree::Leaf(v) then puts v
            when Tree::Node(l, r) then puts size(l)
            end
            puts a
            """);
        var t = b.Affine.Uses.Where(u => u.Key.Name == "t").OrderBy(u => u.Key.Line).ToList();
        Assert.Equal(AffineOp.Keep, t[0].Value);              // total(t): t gets a second owner
        Assert.Equal(ArmMode.Keep, ArmAt(b, 20, "Leaf"));     // the later case reads the shared node
        Assert.Equal(ArmMode.Keep, ArmAt(b, 21, "Node"));
    }

    [Fact]
    public void Without_affine_the_same_program_demotes_and_still_runs()
    {
        const string body = Total + """
            t = Tree::Node(Tree::Leaf(1_u32), Tree::Leaf(2_u32))
            a = total(t)
            b = total(t)
            puts a &+ b
            """;
        var l = Layout(Bind(Tree + body), "Tree");
        Assert.False(l.Arena);
        Assert.Equal("record (demoted: used after move at main.al:13: `t` used after it was moved at main.al:12)", l.Explain);
        Assert.Equal("6", AliceCompiler.Run(Tree + body).Trim());
    }

    // ---- errors under `affine enum` ----

    [Fact]
    public void AL0240_reference_payload()
    {
        var d = Error("affine enum T\n  A(s : String)\n  B\nend\nputs 1\n");
        Assert.Equal("AL0240", d.Code);
        Assert.Equal(2, d.Line);
        Assert.Contains("field `s` of A is a reference type", d.Message);
        Assert.Equal("AL0240", Error("enum W\n  A(s : String)\nend\naffine enum T\n  B(w : W)\nend\nputs 1\n").Code);
    }

    [Theory]
    [InlineData("puts t", "passed to .NET `puts`")]
    [InlineData("puts t == t", "compared with `==`")]
    [InlineData("puts t.hash", "`.hash` called on it")]
    [InlineData("puts t.to_s", "`.to_s` called on it")]
    [InlineData("puts \"#{t}\"", "interpolated into a string")]
    [InlineData("xs = [t]", "stored in a collection")]
    [InlineData("f = ->() { total(t) }", "captured by a block or lambda")]
    [InlineData("begin\n  puts 1\nrescue\n  puts total(t)\nend", "reaches `rescue`")]
    public void AL0241_escapes(string use, string reason)
    {
        var d = Error("affine " + Tree + Total + "t = Tree::Leaf(1_u32)\n" + use + "\n");
        Assert.Equal("AL0241", d.Code);
        Assert.Contains(reason, d.Message);
    }

    [Fact]
    public void AL0241_field_of_a_class()
    {
        var d = Error("affine " + Tree + "\nclass Holder\n  def initialize(@t : Tree)\n  end\nend\nputs 1\n");
        Assert.Equal("AL0241", d.Code);
        Assert.Contains("stored in field", d.Message);
    }

    [Fact]
    public void AL0242_use_after_consume()
    {
        var d = Error("affine " + Tree + Total + "t = Tree::Node(Tree::Leaf(1_u32), Tree::Leaf(2_u32))\na = total(t)\nb = total(t)\nputs a &+ b\n");
        Assert.Equal("AL0242", d.Code);
        Assert.Equal(13, d.Line);
        Assert.Contains("`t` read after it was consumed (moved at main.al:12)", d.Message);
        // consuming a field while the node it came from is used later
        var f = Error("affine " + Tree + Total + """
            def g(t : Tree) : UInt32
              case t
              when Tree::Leaf(v) then v
              when Tree::Node(l, r)
                x = total(l)
                x &+ total(t)
              end
            end
            puts g(Tree::Leaf(1_u32))
            """);
        Assert.Equal("AL0242", f.Code);
        Assert.Contains("field `l` is moved here while `t` is still used", f.Message);
    }

    [Fact]
    public void AL0242_use_after_move_in_a_loop()
    {
        var d = Error("affine " + Tree + Total + """
            def f(t : Tree, n : Int32) : UInt32
              s = 0_u32
              i = 0
              while i < n
                s = s &+ total(t)
                i += 1
              end
              s
            end
            puts f(Tree::Leaf(1_u32), 2)
            """);
        Assert.Equal("AL0242", d.Code);
    }

    [Fact]
    public void AL0243_borrow_consumed()
    {
        var d = Error("affine " + Tree + Total + "def keep(borrow t : Tree) : Tree\n  t\nend\nputs 1\n");
        Assert.Equal("AL0243", d.Code);
        Assert.Contains("borrowed affine value `t` is consumed", d.Message);
        var f = Error("affine " + Tree + Total + "def half(borrow t : Tree) : UInt32\n  case t\n  when Tree::Leaf(v) then v\n  when Tree::Node(l, r) then total(l)\n  end\nend\nputs 1\n");
        Assert.Equal("AL0243", f.Code);
        Assert.Contains("`l` (a field of borrowed `t`)", f.Message);
    }

    // ---- demotion reasons (inferred types) ----

    [Theory]
    [InlineData("puts t", "escapes at main.al:12: passed to .NET `puts`")]
    [InlineData("xs = [t]", "escapes at main.al:12: stored in a collection")]
    [InlineData("u : Tree? = nil", "escapes at main.al:12: nullable")]
    [InlineData("f = ->() { total(t) }", "escapes at main.al:12: ")]
    [InlineData("puts total(t) &+ total(t)", "used after move at main.al:12: `t` used after it was moved at main.al:12")]
    public void Demotion_names_the_site(string use, string why)
    {
        var l = Layout(Bind(Tree + Total + "t = Tree::Node(Tree::Leaf(1_u32), Tree::Leaf(2_u32))\n" + use + "\n"), "Tree");
        Assert.False(l.Arena);
        Assert.StartsWith("record (demoted: " + why, l.Explain);
    }

    [Fact]
    public void A_packed_value_is_copied_not_moved()
    {
        // Leaf(UInt32) lives in the term word: using it twice copies one word, so nothing is demoted
        var b = Bind(Tree + Total + "t = Tree::Leaf(1_u32)\nputs total(t) &+ total(t)\n");
        Assert.True(Layout(b, "Tree").Arena);
    }

    [Fact]
    public void Explain_lists_every_sum_type()
    {
        var b = Bind(Tree + "\nenum W\n  A(s : String)\nend\nputs 1\n");
        Assert.Equal(["Tree: arena", "W: record (demoted: not eligible at main.al:6: field `s` of A is string, not unmanaged)"], b.Affine.Explain());
    }

    // ---- the bend2 ports ----

    [Theory]
    [InlineData("tree-radix", "Arr: arena", "Map: arena")]
    [InlineData("tree-bitonic", "Tree: arena")]
    [InlineData("tree-matmul", "Mat: record (demoted: used after move at {0}:62: `a0` used after it was moved at {0}:61)",
        "Vec: record (demoted: used after move at {0}:101: `l` used after it was moved at {0}:100)")]
    [InlineData("merkle", "Mt: record (demoted: used after move at {0}:71: `l` used after it was moved at {0}:71)", "Path: arena")]
    [InlineData("symreg", "Expr: record (demoted: used after move at {0}:69: `e` used after it was moved at {0}:69)")]
    public void Bend2_ports(string port, params string[] explain)
    {
        var path = Port(port);
        Assert.Equal(explain.Select(e => string.Format(e, path)), BindFile(path).Affine.Explain());
    }

    // ---- the pass changes no output ----

    [Fact]
    public void Affine_enum_emits_the_same_csharp_as_enum()
    {
        const string body = Total + "puts total(Tree::Node(Tree::Leaf(1_u32), Tree::Leaf(2_u32)))\n";
        Assert.Equal(AliceCompiler.Transpile(Tree + body), AliceCompiler.Transpile("affine " + Tree + body));
        Assert.Equal("3", AliceCompiler.Run("affine " + Tree + body).Trim());
    }

    [Fact]
    public void Affine_is_still_a_name()
    {
        Assert.Equal("4", AliceCompiler.Run("affine = 4\nputs affine\n").Trim());
    }
}
