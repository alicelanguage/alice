using System.Text;
using Alice.Compiler;
using Alice.Compiler.Air.Passes;
using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Tests.Compiler;

/// <summary>
/// The IL backend's Synthesize pass (backend plan D1.6): declarations and MIR bodies of what Roslyn synthesizes for
/// the C# backend. Dump tests pin each member; semantic tests run the synthesized bodies with a small MIR evaluator
/// (<see cref="Eval"/>) and compare with what the C# backend prints for the same program.
/// </summary>
[Collection("console")]   // AliceCompiler.Run redirects Console.Out
public class SynthesizeTests
{
    static AirModuleDecl Synth(string src)
    {
        var ast = Parser.ParseFile(src, "main.al");
        var binder = new Binder([ast], new ClrTypes(null));
        binder.Bind();
        // these tests read the record declarations of sum types: keep them records (no arena lowering, ADR 0037)
        var pd = Alice.Compiler.Lowering.AffinePlan.Disable;
        Alice.Compiler.Lowering.AffinePlan.Disable = true;
        try { return Synthesize.Run(binder); }
        finally { Alice.Compiler.Lowering.AffinePlan.Disable = pd; }
    }

    static AirTypeDecl Type(AirModuleDecl m, string name) =>
        m.Types.Concat(m.Types.SelectMany(t => t.Nested)).First(t => t.Name == name);

    static string Dump(string src, string type) => AirDeclDump.Of(Type(Synth(src), type));

    static string Run(string src) => AliceCompiler.Run(src).Trim().ReplaceLineEndings("\n");

    // ---- dumps ----

    [Fact]
    public void Record_class_members_are_roslyns()
    {
        Assert.Equal(PointDump, Dump("record P(x : Int32)\n", "P"));
    }

    const string PointDump =
        "type class record sealed beforefieldinit P : object, global::System.IEquatable<P>\n" +
        "  field private readonly int <x>k__BackingField [BackingField]\n" +
        "  property int x get=get_x set=set_x [Getter]\n" +
        "  property global::System.Type EqualityContract get=get_EqualityContract [EqualityContract]\n" +
        "  method public specialname int get_x() [Getter]\n" +
        "    return (member <x>k__BackingField (this):P):int\n" +
        "  method public specialname initonly void set_x(int value) [Setter]\n" +
        "    assign (member <x>k__BackingField (this):P):int = (param value):int\n" +
        "    return\n" +
        "  method public specialname void .ctor(int x) [PrimaryCtor]\n" +
        "    assign (member <x>k__BackingField (this):P):int = (param x):int\n" +
        "    expr (call .ctor (base):object):void\n" +
        "    return\n" +
        "  method private specialname global::System.Type get_EqualityContract() [EqualityContract]\n" +
        "    return (typeof P):global::System.Type\n" +
        "  method private bool PrintMembers(global::System.Text.StringBuilder builder) [PrintMembers]\n" +
        "    expr (call EnsureSufficientExecutionStack (type global::System.Runtime.CompilerServices.RuntimeHelpers)):void\n" +
        "    expr (call Append (param builder):global::System.Text.StringBuilder (const \"x = \"):string):global::System.Text.StringBuilder\n" +
        "    expr (call Append (param builder):global::System.Text.StringBuilder (call ToString (member x (this):P):int):string):global::System.Text.StringBuilder\n" +
        "    return (const true):bool\n" +
        "  method public virtual override string ToString() [ToString]\n" +
        "    decl builder global::System.Text.StringBuilder = (new global::System.Text.StringBuilder):global::System.Text.StringBuilder\n" +
        "    expr (call Append (local builder):global::System.Text.StringBuilder (const \"P\"):string):global::System.Text.StringBuilder\n" +
        "    expr (call Append (local builder):global::System.Text.StringBuilder (const \" { \"):string):global::System.Text.StringBuilder\n" +
        "    if (call PrintMembers (this):P (local builder):global::System.Text.StringBuilder):bool\n" +
        "      expr (call Append (local builder):global::System.Text.StringBuilder (const ' '):char):global::System.Text.StringBuilder\n" +
        "    expr (call Append (local builder):global::System.Text.StringBuilder (const '}'):char):global::System.Text.StringBuilder\n" +
        "    return (call ToString (local builder):global::System.Text.StringBuilder):string\n" +
        "  method public virtual final bool Equals(P? other) [EqualsTyped]\n" +
        "    return (Or (Eq (cast object (this):P):object (cast object (param other):P?):object):bool (And (And (Ne (cast object (param other):P?):object (null):object):bool (call op_Equality (type global::System.Type) (member EqualityContract (this):P):global::System.Type (member EqualityContract (param other):P?):global::System.Type):bool):bool (call Equals (member Default (type global::System.Collections.Generic.EqualityComparer<int>)):global::System.Collections.Generic.EqualityComparer<int> (member <x>k__BackingField (this):P):int (member <x>k__BackingField (param other):P?):int):bool):bool):bool\n" +
        "  method public virtual override bool Equals(object? obj) [EqualsObject]\n" +
        "    return (call Equals (this):P (cond (is (param obj):object? P):bool (cast P (param obj):object?):P (null):object):P?):bool\n" +
        "  method public virtual override int GetHashCode() [GetHashCode]\n" +
        "    return (Add unchecked (Mul unchecked (call GetHashCode (member Default (type global::System.Collections.Generic.EqualityComparer<global::System.Type>)):global::System.Collections.Generic.EqualityComparer<global::System.Type> (member EqualityContract (this):P):global::System.Type):int (const -1521134295):int):int (call GetHashCode (member Default (type global::System.Collections.Generic.EqualityComparer<int>)):global::System.Collections.Generic.EqualityComparer<int> (member <x>k__BackingField (this):P):int):int):int\n" +
        "  method public static specialname bool op_Equality(P? left, P? right) [OpEquality]\n" +
        "    return (Or (Eq (cast object (param left):P?):object (cast object (param right):P?):object):bool (And (Ne (cast object (param left):P?):object (null):object):bool (call Equals (param left):P? (param right):P?):bool):bool):bool\n" +
        "  method public static specialname bool op_Inequality(P? left, P? right) [OpInequality]\n" +
        "    return (Not (call op_Equality (type P) (param left):P? (param right):P?):bool):bool\n" +
        "  method public void Deconstruct(out int x) [Deconstruct]\n" +
        "    assign (param x):out int = (member x (this):P):int\n" +
        "    return\n" +
        "  method private specialname void .ctor(P original) [CopyCtor]\n" +
        "    expr (call .ctor (base):object):void\n" +
        "    assign (member <x>k__BackingField (this):P):int = (member <x>k__BackingField (param original):P):int\n" +
        "    return\n" +
        "  method public P <Clone>$() [Clone]\n" +
        "    return (new P (this):P):P\n" +
        "  method public P __With(int x) [With]\n" +
        "    decl __c P = (call <Clone>$ (this):P):P\n" +
        "    assign (member x (local __c):P):int = (param x):int\n" +
        "    return (local __c):P\n";

    [Fact]
    public void Record_struct_has_no_contract_clone_or_base_call()
    {
        string d = Dump("record struct V(a : Float64, b : String)\n", "V");
        Assert.StartsWith("type struct record sealed readonly beforefieldinit V : global::System.ValueType, global::System.IEquatable<V>\n", d);
        Assert.DoesNotContain("EqualityContract", d);
        Assert.DoesNotContain("<Clone>$", d);
        Assert.DoesNotContain("(base)", d);
        Assert.Contains("  method public virtual final bool Equals(V other) [EqualsTyped]\n    return (And (call Equals (member Default (type global::System.Collections.Generic.EqualityComparer<double>))", d);
        Assert.Contains("  method public virtual override bool Equals(object? obj) [EqualsObject]\n    return (And (is (param obj):object? V):bool (call Equals (this):V (cast V (param obj):object?):V):bool):bool\n", d);
        // the first field's hash is not multiplied (Roslyn's record struct formula)
        Assert.Contains("    return (Add unchecked (Mul unchecked (call GetHashCode (member Default (type global::System.Collections.Generic.EqualityComparer<double>)):global::System.Collections.Generic.EqualityComparer<double> (member <a>k__BackingField (this):V):double):int (const -1521134295):int):int", d);
        // value types append ToString(), references append as object
        Assert.Contains("(call ToString (member a (this):V):double):string", d);
        Assert.Contains("(cast object (member b (this):V):string):object", d);
        Assert.Contains("  method public static specialname bool op_Equality(V left, V right) [OpEquality]\n    return (call Equals (param left):V (param right):V):bool\n", d);
        Assert.Contains("  method public V __With(double a, string b) [With]\n    decl __c V = (this):V\n", d);
        Assert.Contains("  method public void Deconstruct(out double a, out string b) [Deconstruct]\n", d);
    }

    const string ShapeSrc = "enum Shape\n  Circle(r : Float64)\n  Rect(w : Int32, h : Int32)\n  Empty\nend\n";

    [Fact]
    public void Sum_type_base_is_an_abstract_record_with_tag_and_tests()
    {
        string d = Dump(ShapeSrc, "Shape");
        Assert.StartsWith("type class record abstract beforefieldinit Shape : object, global::System.IEquatable<Shape>\n", d);
        Assert.Contains("  method private specialname void .ctor() [PrivateCtor]\n", d);
        Assert.Contains("  method protected virtual specialname global::System.Type get_EqualityContract() [EqualityContract]\n", d);
        Assert.Contains("  method protected virtual bool PrintMembers(global::System.Text.StringBuilder builder) [PrintMembers]\n    expr (call EnsureSufficientExecutionStack (type global::System.Runtime.CompilerServices.RuntimeHelpers)):void\n    return (const false):bool\n", d);
        Assert.Contains("  method public abstract virtual Shape <Clone>$() [Clone]\n", d);
        Assert.Contains("  method public abstract virtual specialname int get_Tag() [Tag]\n", d);
        Assert.Contains("  method public specialname bool get_IsRect() [IsVariant]\n    return (Eq (member Tag (this):Shape):int (const 1):int):bool\n", d);
        Assert.DoesNotContain("[DefaultCtor]", d);
        Assert.DoesNotContain("__With", d.Split("  type ")[0]);
    }

    [Fact]
    public void Sum_type_variants_are_sealed_nested_records()
    {
        var m = Synth(ShapeSrc);
        var shape = Type(m, "Shape");
        Assert.Equal(["Circle", "Rect", "Empty"], shape.Nested.Select(n => n.Name));
        Assert.All(shape.Nested, n => Assert.Same(shape, n.Enclosing));
        Assert.All(shape.Nested, n => Assert.Same(n, n.Sym.Synth));
        string rect = AirDeclDump.Of(Type(m, "Rect"));
        Assert.StartsWith("type class record sealed beforefieldinit Rect : Shape, global::System.IEquatable<Shape.Rect>\n", rect);
        Assert.Contains("  method public specialname void .ctor(int w, int h) [PrimaryCtor]\n    assign (member <w>k__BackingField (this):Shape.Rect):int = (param w):int\n    assign (member <h>k__BackingField (this):Shape.Rect):int = (param h):int\n    expr (call .ctor (base):Shape):void\n", rect);
        Assert.Contains("  method public virtual override final bool Equals(Shape? other) [EqualsBase]\n    return (call Equals (this):Shape.Rect (cast object? (param other):Shape?):object?):bool\n", rect);
        Assert.Contains("(And (call Equals (base):Shape (cast Shape? (param other):Shape.Rect?):Shape?):bool", rect);
        Assert.Contains("    return (Add unchecked (Mul unchecked (Add unchecked (Mul unchecked (call GetHashCode (base):Shape):int", rect);
        Assert.Contains("    if (call PrintMembers (base):Shape (param builder):global::System.Text.StringBuilder):bool\n      expr (call Append (param builder):global::System.Text.StringBuilder (const \", \"):string)", rect);
        Assert.Contains("  method public virtual override Shape <Clone>$() [Clone]\n    return (new Shape.Rect (this):Shape.Rect):Shape.Rect\n", rect);
        Assert.Contains("  method public virtual override specialname int get_Tag() [Tag]\n    return (const 1):int\n", rect);
        Assert.Contains("    decl __c Shape.Rect = (cast Shape.Rect (call <Clone>$ (this):Shape.Rect):Shape):Shape.Rect\n", rect);
        string empty = AirDeclDump.Of(Type(m, "Empty"));
        Assert.Contains("  field public static readonly Shape.Empty Value [UnitValue]\n", empty);
        Assert.Contains("  method private static specialname void .cctor() [TypeInitializer]\n    assign (member Value (type Shape.Empty)):Shape.Empty = (new Shape.Empty):Shape.Empty\n", empty);
        Assert.Contains("    return (call PrintMembers (base):Shape (param builder):global::System.Text.StringBuilder):bool\n", empty);
        Assert.DoesNotContain("Deconstruct", empty);
        Assert.DoesNotContain("__With", empty);
    }

    const string MoneySrc =
        "class Money\n  getter cents : Int32 = 5\n  def initialize(@c : Int32)\n  end\n" +
        "  def ==(o : Money) : Bool\n    @c == o.c\n  end\n  def <(o : Money) : Bool\n    @c < o.c\n  end\n  def <=(o : Money) : Bool\n    @c <= o.c\n  end\n" +
        "  def c : Int32\n    @c\n  end\n  def to_s : String\n    \"m\"\n  end\nend\n";

    [Fact]
    public void Operators_derive_as_the_csharp_emitter_derives_them()
    {
        string d = Dump(MoneySrc, "Money");
        Assert.Contains("  method public static specialname bool op_Inequality(Money a, Money b) [DerivedOperator]\n    return (Not (call op_Eq (param a):Money (param b):Money):bool):bool\n", d);
        Assert.Contains("  method public static specialname bool op_GreaterThan(Money a, Money b) [DerivedOperator]\n    return (call op_Lt (param b):Money (param a):Money):bool\n", d);
        Assert.Contains("  method public static specialname bool op_GreaterThanOrEqual(Money a, Money b) [DerivedOperator]\n    return (call op_Le (param b):Money (param a):Money):bool\n", d);
        Assert.DoesNotContain("op_Inequality", Dump("class M\n  def ==(o : M) : Bool\n    true\n  end\n  def !=(o : M) : Bool\n    false\n  end\nend\n", "M"));
    }

    [Fact]
    public void Auto_properties_get_a_backing_field_and_accessors()
    {
        string d = Dump(MoneySrc, "Money");
        Assert.Contains("  field private int <cents>k__BackingField [BackingField]\n", d);
        Assert.Contains("  property int cents get=get_cents set=set_cents [Getter]\n", d);
        Assert.Contains("  method public specialname int get_cents() [Getter]\n    return (member <cents>k__BackingField (this):Money):int\n", d);
        Assert.Contains("  method private specialname void set_cents(int value) [Setter]\n", d);
        // the initializer runs in the user constructor (prepended by the IL backend): no default constructor
        Assert.Contains("  field-inits\n    assign (member <cents>k__BackingField (this):Money):int = ", d);
        Assert.DoesNotContain(".ctor", d);
        Assert.Contains("  method public virtual override string ToString() user=to_s [ToStringOverride]\n", d);
        string set = Dump("class S\n  setter level : Int32\n  property name : String = \"a\"\nend\n", "S");
        Assert.Contains("  method private specialname int get_level() [Getter]\n", set);
        Assert.Contains("  method public specialname void set_level(int value) [Setter]\n", set);
        Assert.Contains("  method public specialname void set_name(string value) [Setter]\n", set);
        Assert.Contains("  method public specialname void .ctor() [DefaultCtor]\n    assign (member <name>k__BackingField (this):S):string = ", set);
    }

    [Fact]
    public void Static_initializers_run_in_the_type_initializer()
    {
        string d = Dump("module Cfg\n  LIMIT = 10\nend\n", "Cfg");
        Assert.StartsWith("type module abstract sealed beforefieldinit Cfg : object\n", d);
        Assert.Contains("  method private static specialname void .cctor() [TypeInitializer]\n    assign (member LIMIT (type Cfg)):int = ", d);
        Assert.DoesNotContain(".ctor()", d);
        var top = Synth("MAX = 7\nputs MAX\n");
        Assert.Contains("    assign (member MAX (type Program)):int = ", AirDeclDump.Of(Type(top, "Program")));
    }

    [Fact]
    public void Struct_with_initializers_gets_the_emitters_parameterless_constructor()
    {
        string d = Dump("struct Cfg\n  property depth : Int32 = 4\nend\n", "Cfg");
        Assert.Contains("  method public specialname void .ctor() [StructCtor]\n    assign (this):Cfg = (default):Cfg\n    assign (member <depth>k__BackingField (this):Cfg):int = ", d);
        Assert.DoesNotContain(".ctor", Dump("struct Pt\n  property x : Int32\nend\n", "Pt"));
    }

    [Fact]
    public void Enumerable_bridge_implements_the_non_generic_interface()
    {
        string d = Dump("class Trio < IEnumerable(Int32)\n  def GetEnumerator : IEnumerator(Int32)\n    yield 1\n  end\nend\n", "Trio");
        Assert.Contains("  method private virtual final global::System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() implements global::System.Collections.IEnumerable.GetEnumerator [EnumerableBridge]\n" +
                        "    return (call GetEnumerator (this):Trio):global::System.Collections.Generic.IEnumerator<int>\n", d);
        Assert.Contains("  method public specialname void .ctor() [DefaultCtor]\n    expr (call .ctor (base):object):void\n", d);
    }

    [Fact]
    public void Interface_accessors_are_abstract()
    {
        string d = Dump("interface IArea\n  getter area : Float64\nend\n", "IArea");
        Assert.StartsWith("type interface abstract IArea\n", d);
        Assert.Contains("  method public abstract virtual specialname double get_area() [Getter]\n", d);
        Assert.DoesNotContain("  field ", d);
    }

    [Fact]
    public void Module_initializer_runs_class_bodies_in_order()
    {
        var m = Synth("class A\n  def self.f(s : String)\n  end\n  f \"a\"\nend\nclass B\n  A.f \"b\"\nend\n");
        Assert.Equal("method private static specialname void <Module>..cctor() [ModuleInitializer]\n  expr (call __class_body (type A)):void\n  expr (call __class_body (type B)):void\n",
            AirDeclDump.Of(new AirModuleDecl { ModuleInitializer = m.ModuleInitializer }));
        Assert.Null(Synth("puts 1\n").ModuleInitializer);
    }

    [Fact]
    public void Plain_types_without_synthesized_members_are_left_out()
    {
        var m = Synth("class K\n  def initialize\n  end\nend\nmodule U\n  def self.f : Int32\n    1\n  end\nend\n");
        Assert.Empty(m.Types);
    }

    [Fact]
    public void The_csharp_backend_is_unaffected()
    {
        // the pass only runs for the IL backend: Transpile never fills TypeSym.Synth
        var ast = Parser.ParseFile("record P(x : Int32)\nputs P.new(1)\n", "main.al");
        var binder = new Binder([ast], new ClrTypes(null));
        binder.Bind();
        _ = new Alice.Compiler.Backend.Emitter(binder).EmitFile(ast);
        Assert.All(binder.Types.Values, t => Assert.Null(t.Synth));
    }

    // ---- semantics: synthesized bodies evaluated vs the C# backend ----

    const string RecordsSrc = "record Point(x : Int32, y : Int32)\nrecord struct V(a : Int32, b : String?)\nrecord N(name : String, f : Float64, o : String?, p : Point)\n" + ShapeSrc;

    [Fact]
    public void ToString_matches_the_csharp_backend()
    {
        var e = new Eval(Synth(RecordsSrc));
        var p = e.New("Point", 1, 2);
        string ours = string.Join("\n",
            e.Str(p), e.Str(e.New("V", 3, null)), e.Str(e.New("N", "a", 1.5, null, p)),
            e.Str(e.New("Shape.Circle", 2.0)), e.Str(e.New("Shape.Rect", 3, 4)), e.Str(e.Static("Shape.Empty", "Value")));
        string cs = Run(RecordsSrc + "p = Point.new(1, 2)\nputs p\nputs V.new(3, nil)\nputs N.new(\"a\", 1.5, nil, p)\nputs Shape::Circle.new(2.0)\nputs Shape::Rect.new(3, 4)\nputs Shape::Empty\n");
        Assert.Equal(cs, ours);
        Assert.Equal("Point { x = 1, y = 2 }\nV { a = 3, b =  }\nN { name = a, f = 1.5, o = , p = Point { x = 1, y = 2 } }\nCircle { r = 2 }\nRect { w = 3, h = 4 }\nEmpty { }", ours);
    }

    [Fact]
    public void Equality_matches_the_csharp_backend()
    {
        var e = new Eval(Synth(RecordsSrc));
        object a = e.New("Point", 1, 2), b = e.New("Point", 1, 2), c = e.New("Point", 1, 5);
        object c1 = e.New("Shape.Circle", 2.0), c2 = e.New("Shape.Circle", 2.0), r = e.New("Shape.Rect", 2, 0);
        object empty = e.Static("Shape.Empty", "Value")!;
        var ours = new[]
        {
            e.Op("Point", "op_Equality", a, b), e.Op("Point", "op_Inequality", a, c), e.Op("Point", "op_Equality", a, null),
            e.Op("V", "op_Equality", e.New("V", 1, "s"), e.New("V", 1, "s")), e.Op("V", "op_Equality", e.New("V", 1, "s"), e.New("V", 1, "t")),
            e.Op("Shape", "op_Equality", c1, c2), e.Op("Shape", "op_Equality", c1, r), e.Op("Shape", "op_Equality", empty, e.New("Shape.Empty")),
            e.Call(c1, "Equals", new AirType.Prim(PrimKind.Object), r),
        };
        string cs = Run(RecordsSrc +
            "a = Point.new(1, 2)\nputs a == Point.new(1, 2)\nputs a != Point.new(1, 5)\nputs a == nil\n" +
            "puts V.new(1, \"s\") == V.new(1, \"s\")\nputs V.new(1, \"s\") == V.new(1, \"t\")\n" +
            "c1 = Shape::Circle.new(2.0)\nputs c1 == Shape::Circle.new(2.0)\nputs c1 == Shape::Rect.new(2, 0)\nputs Shape::Empty == Shape::Empty.new\n" +
            "puts c1.Equals(Shape::Rect.new(2, 0))\n");
        Assert.Equal(cs, string.Join("\n", ours.Select(x => (bool)x! ? "true" : "false")));
    }

    [Fact]
    public void Record_struct_hash_matches_the_csharp_backend()
    {
        var e = new Eval(Synth(RecordsSrc));
        string ours = e.Call(e.New("V", 3, "q"), "GetHashCode", null)!.ToString()!;
        // in-process: string hashing uses the same per-process seed as the C# backend's run
        Assert.Equal(Run(RecordsSrc + "puts V.new(3, \"q\").hash\n"), ours);
        // and equal records hash equal
        Assert.Equal(e.Call(e.New("Point", 4, 5), "GetHashCode", null), e.Call(e.New("Point", 4, 5), "GetHashCode", null));
    }

    [Fact]
    public void Deconstruct_and_with_match_the_csharp_backend()
    {
        var e = new Eval(Synth(RecordsSrc));
        var outs = e.Deconstruct(e.New("Point", 7, 8));
        var w = e.Call(e.New("Point", 1, 2), "__With", null, 1, 9);
        var rw = e.Call(e.New("Shape.Rect", 1, 2), "__With", null, 5, 2);
        string ours = string.Join("\n", outs[0], outs[1], e.Str(w), e.Str(rw), e.Op("Point", "op_Equality", w, e.New("Point", 1, 9)) is true ? "true" : "false");
        Assert.Equal(Run(RecordsSrc + "x, y = Point.new(7, 8)\nputs x\nputs y\nputs Point.new(1, 9)\nputs Shape::Rect.new(5, 2)\nputs true\n"), ours);
    }

    /// <summary>The same programs as above (ToString, equality, hashing, Deconstruct, sum types, auto-properties,
    /// derived operators, field initializers) compiled by the IL backend from these declarations, against the C# backend.</summary>
    [Fact]
    public void Il_backend_prints_what_the_csharp_backend_prints()
    {
        string src = RecordsSrc +
            "class Money\n  getter cents : Int32 = 5\n  def initialize(@c : Int32)\n  end\n" +
            "  def ==(o : Money) : Bool\n    @c == o.amount\n  end\n  def <(o : Money) : Bool\n    @c < o.amount\n  end\n  def <=(o : Money) : Bool\n    @c <= o.amount\n  end\n" +
            "  def amount : Int32\n    @c\n  end\n  def to_s : String\n    \"m#{@c}\"\n  end\nend\n" +
            "class Acc\n  getter total : Int32 = 10\n  property label : String = \"acc\"\n  def initialize(k : Int32)\n    @total += k\n  end\nend\n" +
            "p = Point.new(1, 2)\nputs p\nputs V.new(3, nil)\nputs N.new(\"a\", 1.5, nil, p)\n" +
            "puts Shape::Circle.new(2.0)\nputs Shape::Rect.new(3, 4)\nputs Shape::Empty\n" +
            "a = Point.new(1, 2)\nputs a == Point.new(1, 2)\nputs a != Point.new(1, 5)\nputs a == nil\n" +
            "puts V.new(1, \"s\") == V.new(1, \"s\")\nputs V.new(1, \"s\") == V.new(1, \"t\")\n" +
            "c1 = Shape::Circle.new(2.0)\nputs c1 == Shape::Circle.new(2.0)\nputs c1 == Shape::Rect.new(2, 0)\nputs Shape::Empty == Shape::Empty.new\n" +
            "puts c1.Equals(Shape::Rect.new(2, 0))\nputs V.new(3, \"q\").hash\nputs Point.new(4, 5).hash == Point.new(4, 5).hash\n" +
            "x, y = Point.new(7, 8)\nputs x\nputs y\n" +
            "m = Money.new(3)\nputs m != Money.new(3)\nputs m > Money.new(1)\nputs m >= Money.new(4)\nputs m.cents\nputs m\n" +
            "ac = Acc.new(5)\nputs ac.total\nputs ac.label\nac.label = \"z\"\nputs ac.label\n" +
            "struct Cfg\n  property port : Int32 = 80\n  property host : String = \"localhost\"\nend\nc = Cfg.new\nputs \"#{c.host}:#{c.port}\"\n";
        var res = Alice.Compiler.Backend.Il.AirToIl.Compile([new SourceFile("main.al", src)], "synth");
        Assert.True(res.Ok, string.Join("\n", res.Errors.Select(e => e.Message)));
        var verify = Alice.Tests.Backends.AirToIlTests.IlVerify(res.Pe!);
        Assert.True(verify.Count == 0, string.Join("\n", verify));
        string il = AliceCompiler.Invoke(AliceCompiler.Load(res.Pe!), []).Trim().ReplaceLineEndings("\n");
        Assert.Equal(Run(src), il);
        Assert.Contains("Point { x = 1, y = 2 }\n", il);
    }

    /// <summary>
    /// A tree-walking evaluator for exactly the MIR the Synthesize pass builds: objects are field dictionaries, methods
    /// dispatch virtually on the runtime type (non-virtually on `base`), overloads are chosen by static argument types.
    /// </summary>
    sealed class Eval
    {
        readonly List<AirTypeDecl> types;
        readonly Dictionary<AirTypeDecl, Dictionary<string, object?>> statics = new();

        public Eval(AirModuleDecl m) => types = m.Types.Concat(m.Types.SelectMany(t => t.Nested)).ToList();

        sealed class Obj(AirTypeDecl type)
        {
            public readonly AirTypeDecl Type = type;
            public readonly Dictionary<string, object?> F = new();
        }

        sealed class Comparer;

        sealed class Ref(object? v) { public object? V = v; }

        AirTypeDecl Decl(string cs) => types.First(t => t.Self.ToCs() == cs.TrimEnd('?'));

        static IEnumerable<AirTypeDecl> Chain(AirTypeDecl t)
        {
            for (var x = t; x != null; x = x.Enclosing) yield return x;
        }

        public object New(string type, params object?[] args)
        {
            var t = Decl(type);
            var o = new Obj(t);
            var ctor = t.Methods.First(m => m.Name == ".ctor" && m.Synth != AirSynth.CopyCtor && m.Params.Count == args.Length);
            Invoke(ctor, o, args);
            return o;
        }

        public object? Static(string type, string field)
        {
            var t = Decl(type);
            return Statics(t)[field];
        }

        Dictionary<string, object?> Statics(AirTypeDecl t)
        {
            if (statics.TryGetValue(t, out var d)) return d;
            statics[t] = d = new();
            if (t.Method(".cctor") is { } cc) Invoke(cc, null, []);
            return d;
        }

        public string Str(object? o) => (string)Call(o, "ToString", null)!;

        public object? Op(string type, string op, object? l, object? r) => Invoke(Decl(type).Method(op)!, null, [l, r]);

        public object? Call(object? recv, string name, AirType? argType, params object?[] args)
        {
            var o = (Obj)recv!;
            var m = Chain(o.Type).SelectMany(t => t.Methods).First(m => m.Name == name && m.Params.Count == args.Length
                && (argType == null || m.Params[0].Type.ToCs().TrimEnd('?') == argType.ToCs()));
            return Invoke(m, o, args);
        }

        public object?[] Deconstruct(object o)
        {
            var m = ((Obj)o).Type.Method("Deconstruct")!;
            var refs = m.Params.Select(_ => new Ref(null)).ToArray();
            Invoke(m, (Obj)o, refs);
            return refs.Select(r => r.V).ToArray();
        }

        sealed class Return(object? v) : Exception { public readonly object? V = v; }

        object? Invoke(AirMethodDecl m, Obj? self, object?[] args)
        {
            var env = new Dictionary<string, object?> { ["this"] = self };
            for (int i = 0; i < m.Params.Count; i++) env[m.Params[i].Name] = args[i];
            try { Block(m.Body!, env); }
            catch (Return r) { return r.V; }
            return null;
        }

        void Block(List<MStmt> body, Dictionary<string, object?> env)
        {
            foreach (var s in body)
                switch (s)
                {
                    case MReturn r: throw new Return(r.Value != null ? E(r.Value, env) : null);
                    case MExprStmt x: E(x.E, env); break;
                    case MDecl d: env[d.Name] = d.Init != null ? E(d.Init, env) : null; break;
                    case MIf i:
                        if ((bool)E(i.Cond, env)!) Block(i.Then, env);
                        else if (i.Else != null) Block(i.Else, env);
                        break;
                    case MAssign a: Store(a.Target, E(a.Value, env), env); break;
                    default: throw new NotSupportedException(s.GetType().Name);
                }
        }

        void Store(MExpr target, object? v, Dictionary<string, object?> env)
        {
            switch (target)
            {
                case AirParamRef { Type: AirType.ByRef } pr: ((Ref)env[pr.Name]!).V = v; break;
                case AirParamRef pa: env[pa.Name] = v; break;
                case AirLocalRef l: env[l.Name] = v; break;
                case MMember { Target: AirTypeRef tr } sm: Statics(Decl(tr.Ref.ToCs()))[sm.Name] = v; break;
                case MMember mm:
                    {
                        var o = (Obj)E(mm.Target, env)!;
                        if (Chain(o.Type).Any(t => t.Fields.Any(f => f.Name == mm.Name))) o.F[mm.Name] = v;
                        else Call(o, "set_" + mm.Name, null, v);
                        break;
                    }
                default: throw new NotSupportedException(target.GetType().Name);
            }
        }

        AirMethodDecl Resolve(AirTypeDecl start, string name, List<MExpr> args)
        {
            var cands = Chain(start).SelectMany(t => t.Methods).Where(m => m.Name == name && m.Params.Count == args.Count).ToList();
            return cands.FirstOrDefault(m => m.Params.Select(p => p.Type.ToCs().TrimEnd('?')).SequenceEqual(args.Select(a => a.Type!.ToCs().TrimEnd('?'))))
                ?? cands.First();
        }

        object? E(MExpr e, Dictionary<string, object?> env)
        {
            switch (e)
            {
                case AirConst c: return c.Value;
                case AirNull: return null;
                case AirThis or AirBase: return env["this"];
                case AirParamRef pr: return env[pr.Name];
                case AirTypeOf to: return Decl(to.Of.ToCs());
                case AirLocalRef l: return env[l.Name];
                case MCast c: return E(c.E, env);
                case MCond c: return (bool)E(c.C, env)! ? E(c.A, env) : E(c.B, env);
                case MUnary { Op: AirUnOp.Not } u: return !(bool)E(u.E, env)!;
                case MIs i: return E(i.E, env) is Obj o && Chain(o.Type).Any(t => t.Self.ToCs() == i.TypeCs);
                case MBinary b:
                    {
                        if (b.Op == AirBinOp.Or) return (bool)E(b.L, env)! || (bool)E(b.R, env)!;
                        if (b.Op == AirBinOp.And) return (bool)E(b.L, env)! && (bool)E(b.R, env)!;
                        object? l = E(b.L, env), r = E(b.R, env);
                        return b.Op switch
                        {
                            AirBinOp.Eq => l is Obj || r is Obj || l == null || r == null ? ReferenceEquals(l, r) : l.Equals(r),
                            AirBinOp.Ne => !ReferenceEquals(l, r),
                            AirBinOp.Mul => unchecked((int)l! * (int)r!),
                            AirBinOp.Add => unchecked((int)l! + (int)r!),
                            _ => throw new NotSupportedException(b.Op.ToString()),
                        };
                    }
                case MNew n:
                    {
                        if (n.TypeCs == "global::System.Text.StringBuilder") return new StringBuilder();
                        var t = Decl(n.TypeCs);
                        var no = new Obj(t);
                        Invoke(Resolve(t, ".ctor", n.Args), no, n.Args.Select(a => E(a, env)).ToArray());
                        return no;
                    }
                case MMember { Target: AirTypeRef tr, Name: "Default" } when tr.Ref.ToCs().Contains("EqualityComparer"): return new Comparer();
                case MMember { Target: AirTypeRef tr } sm: return Statics(Decl(tr.Ref.ToCs()))[sm.Name];
                case MMember mm:
                    {
                        var mo = (Obj)E(mm.Target, env)!;
                        return mo.F.TryGetValue(mm.Name, out var v) ? v : Call(mo, "get_" + mm.Name, null);
                    }
                case MCall c: return CallE(c, env);
            }
            throw new NotSupportedException(e.GetType().Name);
        }

        object? CallE(MCall c, Dictionary<string, object?> env)
        {
            if (c.Target is AirTypeRef tr)
            {
                if (c.Name == "EnsureSufficientExecutionStack") return null;
                var args0 = c.Args.Select(a => E(a, env)).ToArray();
                if (c.Name == "op_Equality" && tr.Ref.ToCs() == "global::System.Type") return ReferenceEquals(args0[0], args0[1]);
                return Invoke(Resolve(Decl(tr.Ref.ToCs()), c.Name, c.Args), null, args0);
            }
            var recv = E(c.Target!, env);
            var args = c.Args.Select(a => E(a, env)).ToArray();
            switch (recv)
            {
                case StringBuilder sb when c.Name == "Append":
                    if (args[0] is Obj ao) sb.Append(Str(ao)); else sb.Append(args[0]);
                    return sb;
                case Comparer when c.Name == "Equals":
                    return args[0] is Obj x ? (bool)Call(x, "Equals", new AirType.Prim(PrimKind.Object), args[1])! : Equals(args[0], args[1]);
                case Comparer when c.Name == "GetHashCode":
                    return args[0] switch { null => 0, Obj o => Call(o, "GetHashCode", null), AirTypeDecl t => t.GetHashCode(), var v => v.GetHashCode() };
                case Obj o:
                    {
                        // `base.M(...)`: non-virtual from the base type; otherwise the most derived override of the overload
                        if (c.Target is AirBase { Type: AirType.Prim { Kind: PrimKind.Object } }) return null;   // object's constructor
                        if (c.Target is AirBase b)
                            return Invoke(Resolve(Decl(b.Type!.ToCs()), c.Name, c.Args), o, args);
                        var sig = Resolve(Decl(c.Target!.Type!.ToCs()), c.Name, c.Args);
                        var impl = Chain(o.Type).SelectMany(t => t.Methods).First(m => m.Name == sig.Name && m.Params.Select(p => p.Type.ToCs()).SequenceEqual(sig.Params.Select(p => p.Type.ToCs())) && m.Body != null);
                        return Invoke(impl, o, args);
                    }
                default:
                    if (c.Name == "ToString") return recv!.ToString();
                    throw new NotSupportedException("call " + c.Name + " on " + recv?.GetType().Name);
            }
        }
    }
}
