using System.Reflection;
using Alice.Runtime;

namespace Alice.Tests.Runtime;

/// <summary>
/// The `Any` dispatcher of the IL backend (Alice.Runtime.Dyn, ADR 0033, backend plan D1.9): members, indexers,
/// operators, conversions and delegates resolved by runtime types, overloads by Alice's rules, nil, error messages,
/// and the per-site inline cache (monomorphic hit path allocation-free).
/// </summary>
public class DynTests
{
    public sealed class Shape
    {
        public int Sides = 3;
        public readonly int Fixed = 1;
        public string Label { get; set; } = "tri";
        public int Count { get; } = 7;
        public double TotalArea() => 1.5;
        public bool IsEmpty => false;
        public string Name() => "shape";
        public string Echo(string s) => s;
        public string Kind(int x) => "int";
        public string Kind(long x) => "long";
        public string Kind(string? x) => "string";
        public string Kind(object x) => "object";
        public double Half(double x) => x / 2;
        public string Pair(long a, object b) => "long,object";
        public string Pair(object a, long b) => "object,long";
        public int Sum(int a, int b = 10) => a + b;
        public int All(params int[] xs) => xs.Sum();
        public void Boom() => throw new InvalidOperationException("boom");
        public int this[int i] => i * 2;
        public int __set_size(int v) => Stored = v;
        public int Stored;
    }

    public sealed class Vec(int x)
    {
        public int X = x;
        public static Vec operator +(Vec a, Vec b) => new(a.X + b.X);
        public static Vec operator -(Vec a) => new(-a.X);
        public static bool operator ==(Vec? a, Vec? b) => a?.X == b?.X;
        public static bool operator !=(Vec? a, Vec? b) => !(a == b);
        public static implicit operator int(Vec v) => v.X;
        public override bool Equals(object? o) => o is Vec v && v.X == X;
        public override int GetHashCode() => X;
    }

    public sealed class Ghost
    {
        public object? method_missing(string name, object?[] args) => name + "/" + args.Length;
    }

    static DynSite Call(string name, int arity = 0, bool safe = false) => new(DynKind.Call, name, arity, safe);

    // ---- members

    [Fact]
    public void Calls_a_method_by_its_compiled_name_then_its_dotnet_spelling()
    {
        var s = new Shape();
        var site = Call("Name");
        Assert.Equal("shape", Dyn.Call(ref site, s));
        var alice = Call("total_area");
        Assert.Equal(1.5, Dyn.Call(ref alice, s));
        var pred = new DynSite(DynKind.GetMember, "empty_q");   // `empty?` lowered: IsEmpty
        Assert.Equal(false, Dyn.GetMember(ref pred, s));
    }

    [Fact]
    public void Gets_properties_fields_and_zero_argument_methods()
    {
        var s = new Shape();
        var label = new DynSite(DynKind.GetMember, "Label");
        var sides = new DynSite(DynKind.GetMember, "sides");
        var name = new DynSite(DynKind.GetMember, "name");
        Assert.Equal("tri", Dyn.GetMember(ref label, s));
        Assert.Equal(3, Dyn.GetMember(ref sides, s));
        Assert.Equal("shape", Dyn.GetMember(ref name, s));
    }

    [Fact]
    public void Sets_properties_fields_and_alice_setters_with_widening()
    {
        var s = new Shape();
        var label = new DynSite(DynKind.SetMember, "label");
        Dyn.SetMember(ref label, s, "quad");
        Assert.Equal("quad", s.Label);
        var sides = new DynSite(DynKind.SetMember, "Sides");
        Dyn.SetMember(ref sides, s, (short)4);   // Int16 widens into the Int32 field
        Assert.Equal(4, s.Sides);
        var size = new DynSite(DynKind.SetMember, "size");   // `def size=(v)` compiles to __set_size
        Dyn.SetMember(ref size, s, 9);
        Assert.Equal(9, s.Stored);
        var none = new DynSite(DynKind.SetMember, "missing");
        var e = Assert.Throws<MissingMemberException>(() => Dyn.SetMember(ref none, s, 1));
        Assert.Equal("undefined setter `missing=` for Shape", e.Message);
    }

    [Fact]
    public void Read_only_members_are_not_written()
    {
        var s = new Shape();
        var count = new DynSite(DynKind.SetMember, "Count");
        var e = Assert.Throws<MissingMemberException>(() => Dyn.SetMember(ref count, s, 1));
        Assert.Contains("`Count` of Shape is read-only", e.Message);
        var ro = new DynSite(DynKind.SetMember, "Fixed");
        Assert.Throws<MissingMemberException>(() => Dyn.SetMember(ref ro, s, 2));
    }

    [Fact]
    public void Non_public_members_are_never_reached()
    {
        var secret = new DynSite(DynKind.GetMember, "secret");
        Assert.Throws<MissingMemberException>(() => Dyn.GetMember(ref secret, new Private()));
        var hidden = Call("Hidden");
        Assert.Throws<MissingMethodException>(() => Dyn.Call(ref hidden, new Private()));
    }

#pragma warning disable CS0414, IDE0051
    sealed class Private
    {
        private int secret = 1;
        private int Hidden() => secret;
    }
#pragma warning restore CS0414, IDE0051

    // ---- overloads

    [Fact]
    public void Overloads_pick_by_runtime_argument_types()
    {
        var s = new Shape();
        string Kind(object? a) { var site = Call("Kind", 1); return (string)Dyn.Call(ref site, s, a)!; }
        Assert.Equal("int", Kind(1));
        Assert.Equal("long", Kind(1L));
        Assert.Equal("int", Kind((short)1));      // nearest widening: Int16 -> Int32
        Assert.Equal("string", Kind("a"));
        Assert.Equal("string", Kind(null));       // nil: the reference parameter, not Object
        Assert.Equal("object", Kind(1.5));
    }

    [Fact]
    public void Numeric_arguments_widen()
    {
        var site = Call("half", 1);
        Assert.Equal(1.5, Dyn.Call(ref site, new Shape(), 3));
    }

    [Fact]
    public void Ambiguous_overloads_are_an_error_naming_both()
    {
        var site = Call("Pair", 2);
        var e = Assert.Throws<AmbiguousMatchException>(() => Dyn.Call(ref site, new Shape(), 1, 2));
        Assert.Contains("ambiguous call `Pair` on Shape with (Int32, Int32)", e.Message);
        Assert.Contains("Pair(Int64, Object)", e.Message);
        Assert.Contains("Pair(Object, Int64)", e.Message);
    }

    [Fact]
    public void Optional_and_params_parameters()
    {
        var s = new Shape();
        var one = Call("sum", 1);
        Assert.Equal(11, Dyn.Call(ref one, s, 1));
        var two = Call("sum", 2);
        Assert.Equal(3, Dyn.Call(ref two, s, 1, 2));
        var all = Call("all", 3);
        Assert.Equal(6, Dyn.Call(ref all, s, 1, 2, 3));
    }

    // ---- nil and errors

    [Fact]
    public void Nil_receiver_errors_or_yields_nil_for_a_safe_call()
    {
        var site = Call("name");
        var e = Assert.Throws<NullReferenceException>(() => Dyn.Call(ref site, null));
        Assert.Equal("undefined method `name` for nil", e.Message);
        var safe = Call("name", safe: true);
        Assert.Null(Dyn.Call(ref safe, null));
        var get = new DynSite(DynKind.GetMember, "label", safe: true);
        Assert.Null(Dyn.GetMember(ref get, null));
    }

    [Fact]
    public void Undefined_method_message_names_the_type_arguments_and_candidates()
    {
        var site = Call("nope", 2);
        var e = Assert.Throws<MissingMethodException>(() => Dyn.Call(ref site, new Shape(), 1, "x"));
        Assert.Equal("undefined method `nope` for Shape (called with Int32, String)", e.Message);
        var wrong = Call("echo", 1);
        var w = Assert.Throws<MissingMethodException>(() => Dyn.Call(ref wrong, new Shape(), 5));
        Assert.Equal("undefined method `echo` for Shape (called with Int32); Shape has Echo(String)", w.Message);
    }

    [Fact]
    public void Exceptions_of_the_target_are_not_wrapped()
    {
        var site = Call("boom");
        var e = Assert.Throws<InvalidOperationException>(() => Dyn.Call(ref site, new Shape()));
        Assert.Equal("boom", e.Message);
    }

    [Fact]
    public void Unknown_messages_go_to_method_missing()
    {
        var site = Call("anything", 2);
        Assert.Equal("anything/2", Dyn.Call(ref site, new Ghost(), 1, 2));
    }

    // ---- indexers

    [Fact]
    public void Indexes_arrays_lists_dictionaries_strings_and_indexers()
    {
        var get = new DynSite(DynKind.GetIndex, "", 1);
        Assert.Equal(20, Dyn.Index(ref get, new[] { 10, 20 }, 1));
        Assert.Equal(20, Dyn.Index(ref get, new[] { 10, 20 }, 1L));
        Assert.Equal("b", Dyn.Index(ref get, new List<string> { "a", "b" }, 1));
        Assert.Equal(2, Dyn.Index(ref get, new Dictionary<string, int> { ["k"] = 2 }, "k"));
        Assert.Equal('b', Dyn.Index(ref get, "ab", 1));
        Assert.Equal(8, Dyn.Index(ref get, new Shape(), 4));
        Assert.Equal(5, get.CachedCount);
    }

    [Fact]
    public void Sets_array_elements_with_widening_and_reports_bad_indexes()
    {
        var xs = new long[2];
        var set = new DynSite(DynKind.SetIndex, "", 1);
        Dyn.SetIndex(ref set, xs, 1, 7);   // Int32 value into Array(Int64)
        Assert.Equal(7L, xs[1]);
        var d = new Dictionary<string, int>();
        Dyn.SetIndex(ref set, d, "k", 3);
        Assert.Equal(3, d["k"]);
        var get = new DynSite(DynKind.GetIndex, "", 1);
        var e = Assert.Throws<MissingMemberException>(() => Dyn.Index(ref get, 5, 0));
        Assert.Equal("Int32 has no indexer `[]` (called with Int32)", e.Message);
    }

    // ---- operators

    static object? Bin(DynOp op, object? l, object? r)
    {
        var site = new DynSite(DynKind.BinaryOp, op);
        return Dyn.BinaryOp(ref site, l, r);
    }

    [Fact]
    public void Numeric_operators_promote_like_csharp()
    {
        Assert.Equal(5, Bin(DynOp.Add, 2, 3));
        Assert.Equal(5L, Bin(DynOp.Add, 2, 3L));
        Assert.Equal(2.5, Bin(DynOp.Add, 2, 0.5));
        Assert.Equal(3, Bin(DynOp.Div, 7, 2));           // truncating, as the C# backend's dynamic
        Assert.Equal(-1, Bin(DynOp.Mod, -7, 3));
        Assert.Equal(true, Bin(DynOp.Lt, 1, 1.5));
        Assert.Equal(true, Bin(DynOp.Eq, 1, 1L));
        Assert.Equal(8, Bin(DynOp.Shl, 1, 3));
        Assert.Equal(6, Bin(DynOp.BitAnd, 7, 6));
        Assert.Equal(4L, Bin(DynOp.Add, 1u, 3));          // UInt32 with a signed Int32: Int64
        Assert.Equal(true, Bin(DynOp.BitOr, true, false));
    }

    [Fact]
    public void Integer_arithmetic_is_checked()
    {
        Assert.Throws<OverflowException>(() => Bin(DynOp.Add, int.MaxValue, 1));
        Assert.Throws<OverflowException>(() => Bin(DynOp.Mul, long.MaxValue, 2L));
        Assert.Throws<DivideByZeroException>(() => Bin(DynOp.Div, 1, 0));
        var neg = new DynSite(DynKind.UnaryOp, DynOp.Neg);
        Assert.Throws<OverflowException>(() => Dyn.UnaryOp(ref neg, int.MinValue));
    }

    [Fact]
    public void Strings_concatenate_and_compare_by_value()
    {
        Assert.Equal("a1", Bin(DynOp.Add, "a", 1));
        Assert.Equal("a", Bin(DynOp.Add, "a", null));
        Assert.Equal(true, Bin(DynOp.Eq, "ab", new string(['a', 'b'])));
    }

    [Fact]
    public void User_operators_and_reference_equality()
    {
        Assert.Equal(5, ((Vec)Bin(DynOp.Add, new Vec(2), new Vec(3))!).X);
        Assert.Equal(true, Bin(DynOp.Eq, new Vec(2), new Vec(2)));
        Assert.Equal(false, Bin(DynOp.Eq, new Vec(2), null));
        var a = new Shape();
        Assert.Equal(true, Bin(DynOp.Eq, a, a));
        Assert.Equal(false, Bin(DynOp.Eq, a, new Shape()));
        Assert.Equal(true, Bin(DynOp.Ne, a, null));
        var e = Assert.Throws<InvalidOperationException>(() => Bin(DynOp.Sub, a, 1));
        Assert.Equal("operator `-` is not defined for Shape and Int32", e.Message);
    }

    [Fact]
    public void Unary_operators()
    {
        var neg = new DynSite(DynKind.UnaryOp, DynOp.Neg);
        Assert.Equal(-3, Dyn.UnaryOp(ref neg, 3));
        Assert.Equal(-2.5, Dyn.UnaryOp(ref neg, 2.5));
        Assert.Equal(-4, ((Vec)Dyn.UnaryOp(ref neg, new Vec(4))!).X);
        var not = new DynSite(DynKind.UnaryOp, DynOp.Not);
        Assert.Equal(false, Dyn.UnaryOp(ref not, true));
        var inv = new DynSite(DynKind.UnaryOp, DynOp.BitNot);
        Assert.Equal(~5, Dyn.UnaryOp(ref inv, 5));
    }

    // ---- conversions

    [Fact]
    public void Implicit_conversions_widen_and_reject_narrowing()
    {
        var toLong = new DynSite(DynKind.Convert, typeof(long));
        Assert.Equal(3L, Dyn.Convert<long>(ref toLong, 3));
        var toInt = new DynSite(DynKind.Convert, typeof(int));
        var e = Assert.Throws<InvalidCastException>(() => Dyn.Convert<int>(ref toInt, 3L));
        Assert.Equal("cannot convert Int64 to Int32; use `.as(Int32)` for an explicit conversion", e.Message);
        Assert.Equal(4, Dyn.Convert<int>(ref toInt, new Vec(4)));   // op_Implicit
        var toNullable = new DynSite(DynKind.Convert, typeof(int?));
        Assert.Null(Dyn.Convert<int?>(ref toNullable, null));
        var n = Assert.Throws<InvalidCastException>(() => Dyn.Convert<int>(ref toInt, null));
        Assert.Equal("cannot convert nil to Int32", n.Message);
        var toBool = new DynSite(DynKind.Convert, typeof(bool));
        Assert.Throws<InvalidCastException>(() => Dyn.Convert<bool>(ref toBool, 1));
    }

    [Fact]
    public void Casts_truncate_and_check_ranges()
    {
        var cast = new DynSite(DynKind.Cast, typeof(int));
        Assert.Equal(3, Dyn.Convert<int>(ref cast, 3.9));
        Assert.Equal(-3, Dyn.Convert<int>(ref cast, -3.9));
        Assert.Throws<OverflowException>(() => Dyn.Convert<int>(ref cast, 1e12));
        Assert.Equal(7, Dyn.Convert<int>(ref cast, 7L));
    }

    // ---- delegates

    [Fact]
    public void Invokes_delegates_with_widening_and_reports_non_functions()
    {
        Func<long, long> twice = x => x * 2;
        var site = new DynSite(DynKind.Invoke, "", 1);
        Assert.Equal(6L, Dyn.Invoke(ref site, twice, 3));
        Action<string> noop = _ => { };
        var one = new DynSite(DynKind.Invoke, "", 1);
        Assert.Null(Dyn.Invoke(ref one, noop, "x"));
        var e = Assert.Throws<MissingMethodException>(() => Dyn.Invoke(ref one, 5, 1));
        Assert.StartsWith("Int32 is not callable", e.Message);
    }

    // ---- the inline cache

    [Fact]
    public void Sites_cache_per_receiver_type_and_go_megamorphic_past_the_inline_cache()
    {
        var site = new DynSite(DynKind.GetMember, "Length");
        object[] receivers = ["ab", new int[3], new long[1], new string[2], new byte[4], new char[5], new double[6]];
        for (int round = 0; round < 3; round++)
            foreach (var r in receivers) Dyn.GetMember(ref site, r);
        Assert.True(site.IsMegamorphic);
        Assert.Equal(1 + DynSite.PolySize, site.CachedCount);

        var mono = new DynSite(DynKind.GetMember, "Length");
        for (int i = 0; i < 10; i++) Assert.Equal(2, Dyn.GetMember(ref mono, "ab"));
        Assert.Equal(1, mono.MissCount);
        Assert.Equal(1, mono.CachedCount);
        Assert.False(mono.IsMegamorphic);
    }

    [Fact]
    public void Argument_types_are_part_of_the_cache_key()
    {
        var s = new Shape();
        var site = Call("Kind", 1);
        Assert.Equal("int", Dyn.Call(ref site, s, 1));
        Assert.Equal("string", Dyn.Call(ref site, s, "a"));
        Assert.Equal("int", Dyn.Call(ref site, s, 2));
        Assert.Equal(2, site.MissCount);
    }
}

/// <summary>The monomorphic hit path allocates nothing after warm-up (reference-typed arguments and results; value
/// types box, as any `object` slot does).</summary>
[Collection(IsolatedAllocationCollection.Name)]
public class DynAllocationTests
{
    public sealed class Box
    {
        public string Label { get; set; } = "x";
        public string Echo(string s) => s;
        public string Join(string a, string b) => b;
    }

    [Fact]
    public void Monomorphic_hit_path_allocates_nothing()
    {
        var b = new Box();
        object arg = "y";
        var call = new DynSite(DynKind.Call, "echo", 1);
        var call2 = new DynSite(DynKind.Call, "join", 2);
        var get = new DynSite(DynKind.GetMember, "label");
        var set = new DynSite(DynKind.SetMember, "label");
        void Body()
        {
            for (int i = 0; i < 1000; i++)
            {
                Dyn.Call(ref call, b, arg);
                Dyn.Call(ref call2, b, arg, arg);
                Dyn.GetMember(ref get, b);
                Dyn.SetMember(ref set, b, arg);
            }
        }
        Body();
        Body();   // MethodInvoker switches to its emitted invoke stub after the first calls
        Assert.Equal(0, AllocationProbe.Least(Body));
        Assert.Equal(1, call.MissCount);
        Assert.Equal(1, get.MissCount);
    }
}
