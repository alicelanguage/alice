// THROWAWAY SPIKE (backend plan D1.1): see README.md in this folder. Not production code.
using System.Globalization;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using Alice.Compiler.Mir;

namespace Alice.Compiler.Backend.IlSpike;

/// <summary>A MIR node or feature the spike cannot emit; <see cref="Kind"/> is the census key.</summary>
public sealed class SpikeUnsupported : Exception
{
    public string Kind { get; }
    public SpikeUnsupported(string kind, string? detail = null) : base(detail == null ? kind : kind + ": " + detail) => Kind = kind;
}

/// <summary>C# type text / member names -> reflection objects. The C# backend leaves all of this to Roslyn.</summary>
public sealed class ClrResolve
{
    static readonly string[] Usings = ["", "System.", "System.Collections.Generic.", "System.Linq.", "System.Text.", "System.IO.", "System.Threading.", "System.Threading.Tasks.", "Alice.Runtime."];

    static readonly Dictionary<string, Type> Keywords = new()
    {
        ["void"] = typeof(void), ["bool"] = typeof(bool), ["char"] = typeof(char), ["sbyte"] = typeof(sbyte), ["byte"] = typeof(byte),
        ["short"] = typeof(short), ["ushort"] = typeof(ushort), ["int"] = typeof(int), ["uint"] = typeof(uint), ["long"] = typeof(long),
        ["ulong"] = typeof(ulong), ["float"] = typeof(float), ["double"] = typeof(double), ["decimal"] = typeof(decimal),
        ["string"] = typeof(string), ["object"] = typeof(object), ["nint"] = typeof(nint), ["nuint"] = typeof(nuint),
    };

    /// <summary>Alice-declared types emitted by the spike (structs), by C# name.</summary>
    public readonly Dictionary<string, Type> Alice = new();
    public readonly Dictionary<Type, Dictionary<string, FieldInfo>> AliceFields = new();
    public readonly Dictionary<Type, ConstructorInfo> AliceCtors = new();

    readonly Dictionary<string, Type?> cache = new();
    static readonly Lazy<Dictionary<string, Type>> AllTypes = new(() =>
    {
        var d = new Dictionary<string, Type>();
        var asms = AppDomain.CurrentDomain.GetAssemblies().ToList();
        // the shared framework assemblies a generated program may name (Roslyn sees all of TPA)
        foreach (var n in new[] { "System.Runtime", "System.Console", "System.Collections", "System.Linq", "System.Runtime.Intrinsics", "System.Numerics.Vectors", "System.Memory" })
            try { asms.Add(Assembly.Load(n)); } catch (Exception) { }
        asms.Add(typeof(Alice.Runtime.Prelude).Assembly);
        foreach (var a in asms.Distinct())
        {
            if (a.IsDynamic) continue;
            Type[] ts;
            try { ts = a.GetExportedTypes(); } catch (Exception) { continue; }
            foreach (var t in ts) d.TryAdd(t.FullName!, t);
        }
        return d;
    });

    public Type Type(string cs) => TryType(cs) ?? throw new SpikeUnsupported("TypeUnresolved", cs);

    public Type? TryType(string cs)
    {
        cs = cs.Trim();
        if (cache.TryGetValue(cs, out var hit)) return hit;
        return cache[cs] = Parse(cs);
    }

    Type? Parse(string cs)
    {
        if (cs.EndsWith('?'))
        {
            var inner = TryType(cs[..^1]);
            return inner == null ? null : inner.IsValueType ? typeof(Nullable<>).MakeGenericType(inner) : inner;
        }
        if (cs.EndsWith("[]")) return TryType(cs[..^2])?.MakeArrayType();
        if (cs.EndsWith('*')) return TryType(cs[..^1])?.MakePointerType();
        if (cs.StartsWith('(') && cs.EndsWith(')'))
        {
            var items = SplitTop(cs[1..^1]).Select(p => p.Trim()).Select(p => p.Contains(' ') && !p.EndsWith(')') ? p[..p.LastIndexOf(' ')] : p).Select(TryType).ToList();
            if (items.Any(t => t == null) || items.Count is < 2 or > 7) return null;
            var open = System.Type.GetType("System.ValueTuple`" + items.Count)!;
            return open.MakeGenericType(items.ToArray()!);
        }
        if (Keywords.TryGetValue(cs, out var k)) return k;
        if (Alice.TryGetValue(cs, out var at)) return at;
        string bare = cs.StartsWith("global::") ? cs["global::".Length..] : cs;
        int lt = bare.IndexOf('<');
        if (lt > 0 && bare.EndsWith('>'))
        {
            var args = SplitTop(bare[(lt + 1)..^1]).Select(a => TryType(a)).ToList();
            if (args.Any(a => a == null)) return null;
            var open = Named(bare[..lt] + "`" + args.Count);
            return open?.MakeGenericType(args.ToArray()!);
        }
        return Named(bare);
    }

    static Type? Named(string name)
    {
        foreach (var u in Usings)
        {
            if (AllTypes.Value.TryGetValue(u + name, out var t)) return t;
            // nested: A.B -> A+B
            int dot = name.LastIndexOf('.');
            if (dot > 0 && AllTypes.Value.TryGetValue(u + name[..dot], out var outer) && outer.GetNestedType(name[(dot + 1)..]) is { } nested) return nested;
        }
        return null;
    }

    public static List<string> SplitTop(string s)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c is '<' or '(' or '[') depth++;
            else if (c is '>' or ')' or ']') depth--;
            else if (c == ',' && depth == 0) { parts.Add(s[start..i]); start = i + 1; }
        }
        parts.Add(s[start..]);
        return parts.Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
    }

    // ---- literals ----

    /// <summary>A C# literal as the lowerer prints it.</summary>
    public static bool TryLiteral(string text, out Type type, out object? value)
    {
        type = typeof(void);
        value = null;
        string s = text.Trim();
        if (s == "true" || s == "false") { type = typeof(bool); value = s == "true"; return true; }
        if (s == "null") { type = typeof(object); value = null; return true; }
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') { type = typeof(string); value = Unescape(s[1..^1]); return true; }
        if (s.Length >= 3 && s[0] == '\'' && s[^1] == '\'') { var u = Unescape(s[1..^1]); if (u.Length != 1) return false; type = typeof(char); value = u[0]; return true; }
        if (s.Length == 0) return false;
        bool neg = s[0] == '-';
        string b = (neg ? s[1..] : s).Replace("_", "");
        if (b.Length == 0 || !(char.IsDigit(b[0]) || b[0] == '.')) return false;
        string lower = b.ToLowerInvariant();
        bool hex = lower.StartsWith("0x");
        if (!hex && (lower.EndsWith('f') || lower.EndsWith('d') || lower.EndsWith('m') || lower.Contains('.') || lower.Contains('e')))
        {
            char suf = lower[^1];
            string num = char.IsLetter(suf) ? b[..^1] : b;
            if (neg) num = "-" + num;
            if (suf == 'f') { if (!float.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) return false; type = typeof(float); value = f; return true; }
            if (suf == 'm') return false;   // decimal: not in the spike
            if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out var dd)) return false;
            type = typeof(double); value = dd; return true;
        }
        string suffix = "";
        while (lower.Length > 0 && (lower[^1] == 'u' || lower[^1] == 'l')) { suffix = lower[^1] + suffix; lower = lower[..^1]; }
        System.Numerics.BigInteger v;
        if (hex) { if (!ulong.TryParse(lower[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h)) return false; v = h; }
        else if (!System.Numerics.BigInteger.TryParse(lower, NumberStyles.None, CultureInfo.InvariantCulture, out v)) return false;
        if (neg) v = -v;
        bool u8 = suffix.Contains('u'), l8 = suffix.Contains('l');
        if (!u8 && !l8 && v >= int.MinValue && v <= int.MaxValue) { type = typeof(int); value = (int)v; return true; }
        if (!l8 && v >= 0 && v <= uint.MaxValue) { type = typeof(uint); value = (uint)v; return true; }
        if (!u8 && v >= long.MinValue && v <= long.MaxValue) { type = typeof(long); value = (long)v; return true; }
        if (v >= 0 && v <= ulong.MaxValue) { type = typeof(ulong); value = (ulong)v; return true; }
        return false;
    }

    static string Unescape(string s)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
            char c = s[++i];
            switch (c)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case '0': sb.Append('\0'); break;
                case 'u': sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 4), 16)); i += 4; break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    // ---- numeric facts ----

    public static bool IsIntegral(Type t) => t == typeof(int) || t == typeof(uint) || t == typeof(long) || t == typeof(ulong) || t == typeof(short) || t == typeof(ushort) || t == typeof(byte) || t == typeof(sbyte) || t == typeof(char);
    public static bool IsUnsigned(Type t) => t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) || t == typeof(byte) || t == typeof(char);
    public static bool IsFloat(Type t) => t == typeof(float) || t == typeof(double);
    public static bool IsNumeric(Type t) => IsIntegral(t) || IsFloat(t);
    public static bool Is64(Type t) => t == typeof(long) || t == typeof(ulong);

    static readonly Dictionary<Type, Type[]> Implicit = new()
    {
        [typeof(sbyte)] = [typeof(short), typeof(int), typeof(long), typeof(float), typeof(double)],
        [typeof(byte)] = [typeof(short), typeof(ushort), typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float), typeof(double)],
        [typeof(short)] = [typeof(int), typeof(long), typeof(float), typeof(double)],
        [typeof(ushort)] = [typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float), typeof(double)],
        [typeof(int)] = [typeof(long), typeof(float), typeof(double)],
        [typeof(uint)] = [typeof(long), typeof(ulong), typeof(float), typeof(double)],
        [typeof(long)] = [typeof(float), typeof(double)],
        [typeof(ulong)] = [typeof(float), typeof(double)],
        [typeof(char)] = [typeof(ushort), typeof(int), typeof(uint), typeof(long), typeof(ulong), typeof(float), typeof(double)],
        [typeof(float)] = [typeof(double)],
    };

    public static bool ImplicitNumeric(Type from, Type to) => Implicit.TryGetValue(from, out var tos) && tos.Contains(to);

    /// <summary>Does the constant fit <paramref name="to"/> (C# implicit constant conversion of an int literal)?</summary>
    public static bool LiteralFits(object? v, Type to)
    {
        if (v is not (int or long or uint or ulong)) return false;
        var big = v switch { int i => (System.Numerics.BigInteger)i, long l => l, uint u => u, ulong ul => ul, _ => 0 };
        if (to == typeof(int)) return big >= int.MinValue && big <= int.MaxValue;
        if (to == typeof(uint)) return big >= 0 && big <= uint.MaxValue;
        if (to == typeof(long)) return big >= long.MinValue && big <= long.MaxValue;
        if (to == typeof(ulong)) return big >= 0;
        if (to == typeof(short)) return big >= short.MinValue && big <= short.MaxValue;
        if (to == typeof(ushort)) return big >= 0 && big <= ushort.MaxValue;
        if (to == typeof(byte)) return big >= 0 && big <= byte.MaxValue;
        if (to == typeof(sbyte)) return big >= sbyte.MinValue && big <= sbyte.MaxValue;
        return to == typeof(float) || to == typeof(double);
    }

    /// <summary>C# binary numeric promotion; an int literal adopts the other operand's integral type.</summary>
    public static Type Promote(Type l, Type r, object? litL, object? litR)
    {
        if (l == r) return Widen(l);
        if (litL != null && IsNumeric(r) && LiteralFits(litL, r)) return Widen(r);
        if (litR != null && IsNumeric(l) && LiteralFits(litR, l)) return Widen(l);
        if (l == typeof(double) || r == typeof(double)) return typeof(double);
        if (l == typeof(float) || r == typeof(float)) return typeof(float);
        if (l == typeof(ulong) || r == typeof(ulong)) return typeof(ulong);
        if (l == typeof(long) || r == typeof(long)) return typeof(long);
        if (l == typeof(uint) && IsSignedSmall(r) || r == typeof(uint) && IsSignedSmall(l)) return typeof(long);
        if (l == typeof(uint) || r == typeof(uint)) return typeof(uint);
        return typeof(int);
    }

    static bool IsSignedSmall(Type t) => t == typeof(int) || t == typeof(short) || t == typeof(sbyte);
    public static Type Widen(Type t) => t == typeof(byte) || t == typeof(sbyte) || t == typeof(short) || t == typeof(ushort) || t == typeof(char) ? typeof(int) : t;

    // ---- methods ----

    static readonly Lazy<List<MethodInfo>> Extensions = new(() =>
        typeof(Alice.Runtime.Prelude).Assembly.GetExportedTypes()
            .Where(t => t.IsAbstract && t.IsSealed && (t.Namespace == "Alice.Runtime"))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Where(m => m.IsDefined(typeof(ExtensionAttribute), false)).ToList());

    static readonly Lazy<List<MethodInfo>> PreludeStatics = new(() =>
        typeof(Alice.Runtime.Prelude).GetMethods(BindingFlags.Public | BindingFlags.Static).ToList());

    public static IEnumerable<MethodInfo> ExtensionsNamed(string name) => Extensions.Value.Where(m => m.Name == name);
    public static IEnumerable<MethodInfo> PreludeNamed(string name) => PreludeStatics.Value.Where(m => m.Name == name);

    /// <summary>Picks the best overload for argument types (with literal values for constant conversions);
    /// closes generic methods by inference from parameters typed T / T[].</summary>
    public static MethodBase? Pick(IEnumerable<MethodBase> cands, IReadOnlyList<Type> args, IReadOnlyList<object?> lits)
    {
        MethodBase? best = null;
        int bestScore = int.MinValue;
        foreach (var c0 in cands)
        {
            var c = c0;
            if (c is MethodInfo { IsGenericMethodDefinition: true } gm)
            {
                var inferred = new Type?[gm.GetGenericArguments().Length];
                var gps = gm.GetGenericArguments();
                var ps0 = gm.GetParameters();
                if (ps0.Length != args.Count) continue;
                for (int i = 0; i < ps0.Length; i++)
                {
                    var pt = ps0[i].ParameterType;
                    if (pt.IsGenericParameter) inferred[pt.GenericParameterPosition] ??= args[i];
                    else if (pt.IsArray && pt.GetElementType()!.IsGenericParameter && args[i].IsArray) inferred[pt.GetElementType()!.GenericParameterPosition] ??= args[i].GetElementType();
                }
                if (inferred.Any(x => x == null)) continue;
                try { c = gm.MakeGenericMethod(inferred!); }
                catch (ArgumentException) { continue; }   // constraint violated (to_i32<T> where T : Enum)
            }
            var ps = c.GetParameters();
            if (ps.Length != args.Count) continue;
            if (ps.Length > 0 && ps[^1].IsDefined(typeof(ParamArrayAttribute)) && args[^1] != ps[^1].ParameterType) continue;
            int score = 0;
            bool generic = c0 is MethodInfo { IsGenericMethodDefinition: true };
            for (int i = 0; i < ps.Length && score > int.MinValue; i++)
            {
                var pt = ps[i].ParameterType;
                int s = pt == args[i] ? 4
                    : lits[i] != null && LiteralFits(lits[i], pt) ? 3
                    : ImplicitNumeric(args[i], pt) ? 2
                    : !pt.IsByRef && pt.IsAssignableFrom(args[i]) ? 1
                    : int.MinValue;
                score = s == int.MinValue ? int.MinValue : score + s;
            }
            if (score == int.MinValue) continue;
            score = score * 2 - (generic ? 1 : 0);   // C#: a non-generic exact match beats the generic one
            if (score > bestScore) { best = c; bestScore = score; }
        }
        return best;
    }
}
