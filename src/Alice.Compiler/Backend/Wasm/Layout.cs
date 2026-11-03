using Alice.Compiler.Mir;
using Alice.Compiler.Semantics;

namespace Alice.Compiler.Backend.Wasm;

/// <summary>Kinds of wasm-level Alice types.</summary>
public enum WK
{
    Void, Bool, I8, U8, I16, U16, Char, I32, U32, I64, U64, F32, F64,
    /// <summary>UTF-8 string object.</summary>
    Str,
    /// <summary>One-dimensional array (also Span/ReadOnlySpan/owned buffers: same layout).</summary>
    Array,
    /// <summary>Instance of an Alice class, record, sum type or a runtime exception class (heap object).</summary>
    Class,
    /// <summary>Alice struct / record struct: stored inline, handled by address.</summary>
    Struct,
    /// <summary>Plain enum: its underlying integer.</summary>
    Enum,
    /// <summary>The `nil` literal (a null reference).</summary>
    Null,
    /// <summary>A closure (`Func`/`Action`/`Proc`): [header][table slot][environment].</summary>
    Func,
    /// <summary>`System.Collections.Generic.List&lt;T&gt;`: [header][count][items array].</summary>
    List,
    /// <summary>`System.Collections.Generic.Dictionary&lt;K, V&gt;`: open addressing (AirToWasm.Dict.cs).</summary>
    Dict,
    /// <summary>A string being built (`DefaultInterpolatedStringHandler`, `StringBuilder`): a string local.</summary>
    Builder,
    /// <summary>A static type in expression position (no value).</summary>
    TypeRef,
}

/// <summary>A wasm-level Alice type: how a value is represented (wasm value type, memory size) and what it is.</summary>
public sealed class WT
{
    public WK K;
    public WT? Elem;
    public WClass? Cls;
    /// <summary>Closures: parameter types and result.</summary>
    public WT[] Params = [];
    public WT? Ret;
    /// <summary>The C# name (diagnostics, exception type names).</summary>
    public string Name = "";
    /// <summary>`T?` of a value type (struct form [has][value] is not modeled: nullable numbers are rejected).</summary>
    public bool Nullable;

    WT(WK k, string name) { K = k; Name = name; }

    public static readonly WT Void = new(WK.Void, "void"), Bool = new(WK.Bool, "bool"), I8 = new(WK.I8, "sbyte"), U8 = new(WK.U8, "byte"),
        I16 = new(WK.I16, "short"), U16 = new(WK.U16, "ushort"), Char = new(WK.Char, "char"), I32 = new(WK.I32, "int"), U32 = new(WK.U32, "uint"),
        I64 = new(WK.I64, "long"), U64 = new(WK.U64, "ulong"), F32 = new(WK.F32, "float"), F64 = new(WK.F64, "double"),
        Str = new(WK.Str, "string"), Null = new(WK.Null, "null"), Builder = new(WK.Builder, "System.Runtime.CompilerServices.DefaultInterpolatedStringHandler");

    /// <summary>`Stopwatch` (its start, monotonic nanoseconds) and `TimeSpan` (nanoseconds): an i64 underneath,
    /// told apart by identity. Only `StartNew`, `Elapsed*` and `TimeSpan.Total*` are modeled (no Stop/Restart).</summary>
    public static readonly WT Stopwatch = new(WK.I64, "System.Diagnostics.Stopwatch"), TimeSpan = new(WK.I64, "System.TimeSpan");

    static readonly Dictionary<string, WT> arrays = new();

    public static WT ArrayOf(WT elem)
    {
        string key = elem.Key;
        lock (arrays)
        {
            if (!arrays.TryGetValue(key, out var a)) arrays[key] = a = new WT(WK.Array, elem.Name + "[]") { Elem = elem };
            return a;
        }
    }

    public WT? KeyT;

    public static WT DictOf(WT k, WT v) => new(WK.Dict, "System.Collections.Generic.Dictionary<" + k.Name + ", " + v.Name + ">") { KeyT = k, Elem = v };

    public static WT ListOf(WT elem) => new(WK.List, "System.Collections.Generic.List<" + elem.Name + ">") { Elem = elem };

    public static WT Of(WClass c) => c.Type;

    internal static WT ForClass(WClass c) => new(c.IsStruct ? WK.Struct : c.IsEnum ? WK.Enum : WK.Class, c.Name) { Cls = c };

    public static WT FuncOf(WT[] ps, WT ret) => new(WK.Func, "Func<" + string.Join(",", ps.Select(p => p.Name).Append(ret.Name)) + ">") { Params = ps, Ret = ret };

    public static WT TypeRefOf(string name, WClass? c = null) => new(WK.TypeRef, name) { Cls = c };

    public string Key => K switch
    {
        WK.Array => "[" + Elem!.Key,
        WK.List => "L" + Elem!.Key,
        WK.Dict => "D" + KeyT!.Key + "," + Elem!.Key,
        WK.Class or WK.Struct or WK.Enum => "C" + Cls!.Name,
        WK.Func => "F(" + string.Join(",", Params.Select(p => p.Key)) + ")" + Ret!.Key,
        _ => K.ToString(),
    };

    public bool Same(WT o) => Key == o.Key;

    /// <summary>Held as a GC reference (an i32 pointer to a heap or static object).</summary>
    public bool IsRef => K is WK.Str or WK.Array or WK.Class or WK.Null or WK.Func or WK.List or WK.Dict or WK.Builder;

    public bool IsStruct => K == WK.Struct;
    public bool IsInt => K is WK.I8 or WK.U8 or WK.I16 or WK.U16 or WK.Char or WK.I32 or WK.U32 or WK.I64 or WK.U64 or WK.Enum;
    public bool IsFloat => K is WK.F32 or WK.F64;
    public bool IsNumeric => IsInt || IsFloat;
    public bool Is64 => K is WK.I64 or WK.U64 || K == WK.Enum && Cls!.EnumUnderlying!.Is64;
    public bool IsUnsigned => K is WK.U8 or WK.U16 or WK.Char or WK.U32 or WK.U64 || K == WK.Enum && Cls!.EnumUnderlying!.IsUnsigned;
    /// <summary>The integer type arithmetic happens in (C# promotes small integers to int).</summary>
    public WT Widened => K switch
    {
        WK.I8 or WK.U8 or WK.I16 or WK.U16 or WK.Char or WK.Bool => I32,
        WK.Enum => Cls!.EnumUnderlying!.Widened,
        _ => this,
    };

    /// <summary>The wasm value type of a value of this type (structs are addresses).</summary>
    public ValType Val => K switch
    {
        WK.I64 or WK.U64 => ValType.I64,
        WK.F32 => ValType.F32,
        WK.F64 => ValType.F64,
        WK.Enum => Cls!.EnumUnderlying!.Val,
        _ => ValType.I32,
    };

    /// <summary>Bytes in memory (fields, array elements).</summary>
    public int Size => K switch
    {
        WK.Bool or WK.I8 or WK.U8 => 1,
        WK.I16 or WK.U16 or WK.Char => 2,
        WK.I64 or WK.U64 or WK.F64 => 8,
        WK.Struct => Cls!.Size,
        WK.Enum => Cls!.EnumUnderlying!.Size,
        WK.Void => 0,
        _ => 4,
    };

    public int Align => K == WK.Struct ? Cls!.Align : Math.Max(1, Size);

    /// <summary>Load opcode from memory for this type.</summary>
    public byte Load => K switch
    {
        WK.Bool or WK.U8 => Op.I32Load8U,
        WK.I8 => Op.I32Load8S,
        WK.I16 => Op.I32Load16S,
        WK.U16 or WK.Char => Op.I32Load16U,
        WK.I64 or WK.U64 => Op.I64Load,
        WK.F32 => Op.F32Load,
        WK.F64 => Op.F64Load,
        WK.Enum => Cls!.EnumUnderlying!.Load,
        _ => Op.I32Load,
    };

    public byte Store => K switch
    {
        WK.Bool or WK.U8 or WK.I8 => Op.I32Store8,
        WK.I16 or WK.U16 or WK.Char => Op.I32Store16,
        WK.I64 or WK.U64 => Op.I64Store,
        WK.F32 => Op.F32Store,
        WK.F64 => Op.F64Store,
        WK.Enum => Cls!.EnumUnderlying!.Store,
        _ => Op.I32Store,
    };

    /// <summary>Array header type: 2 number array (noscan), 3 reference array, 4 struct array (scanned).</summary>
    public int ArrayKind => IsRef ? 3 : K == WK.Struct ? 4 : 2;

    public override string ToString() => Name;
}

public sealed class WField
{
    public string Name = "";
    public WT Type = null!;
    public int Offset;
    public bool IsStatic;
    /// <summary>Static fields live in the statics area at this absolute address.</summary>
    public int Address;
    public FieldSym? Sym;
    /// <summary>An enum member or `const`: its value.</summary>
    public object? Literal;
}

public sealed class WMethod
{
    public string Name = "";
    public WClass Owner = null!;
    public MethodSym? Sym;
    public Syntax.MethodDecl? Decl;
    public Instance? Inst;
    public bool IsStatic, IsCtor, IsVirtual, IsAbstract;
    public List<WT> Params = new();
    public List<string> ParamNames = new();
    public WT Ret = WT.Void;
    public WasmFunc Func = null!;
    /// <summary>Default values of optional parameters (lowered expressions).</summary>
    public MExpr?[] Defaults = [];
    /// <summary>The dispatcher of a virtual method (switch on the receiver's type id).</summary>
    public WasmFunc? Dispatch;
    /// <summary>A built-in body (record ToString, exception constructors) emitted by the backend.</summary>
    public Action<WasmBody>? Synthetic;
    public override string ToString() => Owner.Name + "." + Name;
}

/// <summary>An Alice type (or a runtime class such as System.Exception) and its memory layout. Objects are
/// [type id][0][fields...]; structs are their fields only.</summary>
public sealed class WClass
{
    public string Name = "";
    public TypeSym? Sym;
    public WClass? Base;
    public bool IsStruct, IsEnum, IsInterface, IsStatic, IsRecord, IsAdt, IsVariant, IsAbstract;
    public WT? EnumUnderlying;
    public readonly List<WField> Fields = new();
    public readonly Dictionary<string, WField> FieldsByName = new(StringComparer.Ordinal);
    public readonly List<WMethod> Methods = new();
    public readonly List<WMethod> Ctors = new();
    public readonly List<WClass> Interfaces = new();
    public readonly List<WClass> Subclasses = new();
    /// <summary>Type id (classes: preorder over the class tree, so a subtree is the id range [Id, IdEnd]).</summary>
    public int Id, IdEnd;
    public int Size, Align = 1;
    bool laidOut;
    WT? type;
    public WT Type => type ??= WT.ForClass(this);
    /// <summary>The static `Value` of a unit sum-type variant.</summary>
    public WField? UnitValue;

    public override string ToString() => Name;

    /// <summary>Object header bytes before the first field (structs have none).</summary>
    public int HeaderSize => IsStruct ? 0 : 8;

    public WField? FindField(string name)
    {
        for (var c = this; c != null; c = c.Base)
            if (c.FieldsByName.TryGetValue(name, out var f)) return f;
        return null;
    }

    public IEnumerable<WMethod> FindMethods(string name)
    {
        for (var c = this; c != null; c = c.Base)
            foreach (var m in c.Methods)
                if (m.Name == name) yield return m;
        foreach (var i in AllInterfaces())
            foreach (var m in i.Methods)
                if (m.Name == name) yield return m;
    }

    public IEnumerable<WClass> AllInterfaces()
    {
        var seen = new HashSet<WClass>();
        for (var c = this; c != null; c = c.Base)
            foreach (var i in c.Interfaces)
                if (seen.Add(i)) yield return i;
    }

    public bool IsSubclassOf(WClass other)
    {
        for (var c = this; c != null; c = c.Base) if (c == other) return true;
        return AllInterfaces().Contains(other);
    }

    public WField AddField(string name, WT t, bool isStatic = false, FieldSym? sym = null)
    {
        if (laidOut && !isStatic) throw new InvalidOperationException("wasm: field added after layout: " + Name + "." + name);
        var f = new WField { Name = name, Type = t, IsStatic = isStatic, Sym = sym };
        Fields.Add(f);
        FieldsByName[name] = f;
        return f;
    }

    /// <summary>Field offsets: the base class's fields first, each field at its natural alignment.</summary>
    public void Lay()
    {
        if (laidOut) return;
        Base?.Lay();
        int off = Base != null ? Base.Size : HeaderSize;
        Align = Base?.Align ?? (IsStruct ? 1 : 8);
        foreach (var f in Fields)
        {
            if (f.IsStatic || f.Literal != null) continue;
            if (f.Type.IsStruct) f.Type.Cls!.Lay();
            int a = f.Type.Align;
            off = (off + a - 1) / a * a;
            f.Offset = off;
            off += f.Type.Size;
            Align = Math.Max(Align, a);
        }
        Size = (off + Align - 1) / Align * Align;
        if (IsStruct && Size == 0) Size = Align = 1;
        laidOut = true;
    }
}
