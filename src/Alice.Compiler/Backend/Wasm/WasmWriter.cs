using System.Text;

namespace Alice.Compiler.Backend.Wasm;

// The wasm binary format (core spec 2.0 + exception handling with exnref): a module builder that keeps functions as
// objects until Encode, so imports can be added late, unreachable functions are dropped (tree shaking: only what the
// exports, the start function and the function table reach is written) and call sites get final indices. ADR 0034.

/// <summary>Value types (binary encoding).</summary>
public enum ValType : byte { I32 = 0x7F, I64 = 0x7E, F32 = 0x7D, F64 = 0x7C, V128 = 0x7B, FuncRef = 0x70, ExnRef = 0x69 }

/// <summary>A function signature; compared by value (the type section deduplicates).</summary>
public sealed record FuncSig(ValType[] Params, ValType[] Results)
{
    public string Key => string.Join(",", Params.Select(p => (int)p)) + "->" + string.Join(",", Results.Select(r => (int)r));
    public bool Equals(FuncSig? o) => o != null && Key == o.Key;
    public override int GetHashCode() => Key.GetHashCode();
}

/// <summary>A defined or imported function. <see cref="Code"/> is null for an import.</summary>
public sealed class WasmFunc
{
    public string Name = "";
    public FuncSig Sig = null!;
    public (string Module, string Field)? Import;
    public WasmCode? Code;
    public string? Export;
    /// <summary>Final index (set by Encode; -1 when the function was not reached).</summary>
    public int Index = -1;
    /// <summary>Slot in the function table (call_indirect / closures), -1 when it has none.</summary>
    public int TableSlot = -1;
    public override string ToString() => Name;
}

public sealed class WasmGlobal
{
    public string Name = "";
    public ValType Type;
    public bool Mutable;
    public long InitI;
    public double InitF;
    public int Index;
    public string? Export;
}

/// <summary>An exception tag (wasm EH): its parameters are the payload.</summary>
public sealed class WasmTag
{
    public string Name = "";
    public FuncSig Sig = null!;
    public int Index;
}

/// <summary>A source position recorded in a body (for the source map).</summary>
public readonly record struct SourceMark(int Offset, string File, int Line, int Col);

/// <summary>Opcodes used by the emitters (core + sign-extension + saturating truncation + bulk memory + EH).</summary>
public static class Op
{
    public const byte Unreachable = 0x00, Nop = 0x01, Block = 0x02, Loop = 0x03, If = 0x04, Else = 0x05, Throw = 0x08, ThrowRef = 0x0A,
        End = 0x0B, Br = 0x0C, BrIf = 0x0D, BrTable = 0x0E, Return = 0x0F, Call = 0x10, CallIndirect = 0x11, Drop = 0x1A, Select = 0x1B,
        TryTable = 0x1F, LocalGet = 0x20, LocalSet = 0x21, LocalTee = 0x22, GlobalGet = 0x23, GlobalSet = 0x24,
        I32Load = 0x28, I64Load = 0x29, F32Load = 0x2A, F64Load = 0x2B, I32Load8S = 0x2C, I32Load8U = 0x2D, I32Load16S = 0x2E, I32Load16U = 0x2F,
        I64Load8S = 0x30, I64Load8U = 0x31, I64Load16S = 0x32, I64Load16U = 0x33, I64Load32S = 0x34, I64Load32U = 0x35,
        I32Store = 0x36, I64Store = 0x37, F32Store = 0x38, F64Store = 0x39, I32Store8 = 0x3A, I32Store16 = 0x3B, I64Store8 = 0x3C, I64Store16 = 0x3D, I64Store32 = 0x3E,
        MemorySize = 0x3F, MemoryGrow = 0x40,
        I32Const = 0x41, I64Const = 0x42, F32Const = 0x43, F64Const = 0x44,
        I32Eqz = 0x45, I32Eq = 0x46, I32Ne = 0x47, I32LtS = 0x48, I32LtU = 0x49, I32GtS = 0x4A, I32GtU = 0x4B, I32LeS = 0x4C, I32LeU = 0x4D, I32GeS = 0x4E, I32GeU = 0x4F,
        I64Eqz = 0x50, I64Eq = 0x51, I64Ne = 0x52, I64LtS = 0x53, I64LtU = 0x54, I64GtS = 0x55, I64GtU = 0x56, I64LeS = 0x57, I64LeU = 0x58, I64GeS = 0x59, I64GeU = 0x5A,
        F32Eq = 0x5B, F32Ne = 0x5C, F32Lt = 0x5D, F32Gt = 0x5E, F32Le = 0x5F, F32Ge = 0x60,
        F64Eq = 0x61, F64Ne = 0x62, F64Lt = 0x63, F64Gt = 0x64, F64Le = 0x65, F64Ge = 0x66,
        I32Clz = 0x67, I32Ctz = 0x68, I32Popcnt = 0x69, I32Add = 0x6A, I32Sub = 0x6B, I32Mul = 0x6C, I32DivS = 0x6D, I32DivU = 0x6E, I32RemS = 0x6F, I32RemU = 0x70,
        I32And = 0x71, I32Or = 0x72, I32Xor = 0x73, I32Shl = 0x74, I32ShrS = 0x75, I32ShrU = 0x76, I32Rotl = 0x77, I32Rotr = 0x78,
        I64Clz = 0x79, I64Ctz = 0x7A, I64Popcnt = 0x7B, I64Add = 0x7C, I64Sub = 0x7D, I64Mul = 0x7E, I64DivS = 0x7F, I64DivU = 0x80, I64RemS = 0x81, I64RemU = 0x82,
        I64And = 0x83, I64Or = 0x84, I64Xor = 0x85, I64Shl = 0x86, I64ShrS = 0x87, I64ShrU = 0x88, I64Rotl = 0x89, I64Rotr = 0x8A,
        F32Abs = 0x8B, F32Neg = 0x8C, F32Ceil = 0x8D, F32Floor = 0x8E, F32Trunc = 0x8F, F32Nearest = 0x90, F32Sqrt = 0x91, F32Add = 0x92, F32Sub = 0x93, F32Mul = 0x94, F32Div = 0x95, F32Min = 0x96, F32Max = 0x97, F32Copysign = 0x98,
        F64Abs = 0x99, F64Neg = 0x9A, F64Ceil = 0x9B, F64Floor = 0x9C, F64Trunc = 0x9D, F64Nearest = 0x9E, F64Sqrt = 0x9F, F64Add = 0xA0, F64Sub = 0xA1, F64Mul = 0xA2, F64Div = 0xA3, F64Min = 0xA4, F64Max = 0xA5, F64Copysign = 0xA6,
        I32WrapI64 = 0xA7, I32TruncF32S = 0xA8, I32TruncF32U = 0xA9, I32TruncF64S = 0xAA, I32TruncF64U = 0xAB, I64ExtendI32S = 0xAC, I64ExtendI32U = 0xAD,
        I64TruncF32S = 0xAE, I64TruncF32U = 0xAF, I64TruncF64S = 0xB0, I64TruncF64U = 0xB1,
        F32ConvertI32S = 0xB2, F32ConvertI32U = 0xB3, F32ConvertI64S = 0xB4, F32ConvertI64U = 0xB5, F32DemoteF64 = 0xB6,
        F64ConvertI32S = 0xB7, F64ConvertI32U = 0xB8, F64ConvertI64S = 0xB9, F64ConvertI64U = 0xBA, F64PromoteF32 = 0xBB,
        I32ReinterpretF32 = 0xBC, I64ReinterpretF64 = 0xBD, F32ReinterpretI32 = 0xBE, F64ReinterpretI64 = 0xBF,
        I32Extend8S = 0xC0, I32Extend16S = 0xC1, I64Extend8S = 0xC2, I64Extend16S = 0xC3, I64Extend32S = 0xC4,
        Prefix = 0xFC;

    // 0xFC-prefixed (saturating truncation, bulk memory)
    public const uint I32TruncSatF32S = 0, I32TruncSatF32U = 1, I32TruncSatF64S = 2, I32TruncSatF64U = 3,
        I64TruncSatF32S = 4, I64TruncSatF32U = 5, I64TruncSatF64S = 6, I64TruncSatF64U = 7, MemoryCopy = 10, MemoryFill = 11;

    /// <summary>Natural alignment (log2) of a load/store opcode.</summary>
    public static uint Align(byte op) => op switch
    {
        I32Load or F32Load or I32Store or F32Store or I64Load32S or I64Load32U or I64Store32 => 2,
        I64Load or F64Load or I64Store or F64Store => 3,
        I32Load16S or I32Load16U or I64Load16S or I64Load16U or I32Store16 or I64Store16 => 1,
        _ => 0,
    };
}

/// <summary>A structured-control label (block/loop/if/try_table); branches name it, the builder computes the depth.</summary>
public sealed class WasmLabel
{
    internal int Depth;
    public bool IsLoop;
}

/// <summary>One function body: locals and the instruction stream. Function references are fixups resolved at encode
/// time (final indices depend on tree shaking).</summary>
public sealed class WasmCode
{
    public readonly int ParamCount;
    public readonly List<ValType> Locals = new();
    public readonly List<string?> LocalNames = new();
    internal readonly List<byte> Bytes = new(64);
    internal readonly List<(int Pos, WasmFunc F)> Calls = new();
    internal readonly List<(int Pos, string File, int Line, int Col)> Marks = new();
    readonly List<WasmLabel> labels = new();

    public WasmCode(FuncSig sig, IEnumerable<string?>? paramNames = null)
    {
        ParamCount = sig.Params.Length;
        var names = paramNames?.ToList();
        for (int i = 0; i < ParamCount; i++) LocalNames.Add(names != null && i < names.Count ? names[i] : null);
    }

    public int Local(ValType t, string? name = null)
    {
        Locals.Add(t);
        LocalNames.Add(name);
        return ParamCount + Locals.Count - 1;
    }

    public ValType LocalType(int i, FuncSig sig) => i < ParamCount ? sig.Params[i] : Locals[i - ParamCount];

    public int Length => Bytes.Count;
    public int LabelDepth => labels.Count;

    public void Mark(string file, int line, int col) => Marks.Add((Bytes.Count, file, line, col));

    public void B(byte b) => Bytes.Add(b);
    public void U(uint v) => Leb.U(Bytes, v);
    public void S(long v) => Leb.S(Bytes, v);

    public void Emit(byte op) => Bytes.Add(op);
    public void Emit(byte op, uint imm) { Bytes.Add(op); Leb.U(Bytes, imm); }
    public void Prefixed(uint sub) { Bytes.Add(Op.Prefix); Leb.U(Bytes, sub); }

    public void I32(int v) { Bytes.Add(Op.I32Const); Leb.S(Bytes, v); }
    public void I64(long v) { Bytes.Add(Op.I64Const); Leb.S(Bytes, v); }
    public void F32(float v) { Bytes.Add(Op.F32Const); Bytes.AddRange(BitConverter.GetBytes(v)); }
    public void F64(double v) { Bytes.Add(Op.F64Const); Bytes.AddRange(BitConverter.GetBytes(v)); }

    public void Get(int local) => Emit(Op.LocalGet, (uint)local);
    public void Set(int local) => Emit(Op.LocalSet, (uint)local);
    public void Tee(int local) => Emit(Op.LocalTee, (uint)local);
    public void GGet(WasmGlobal g) => Emit(Op.GlobalGet, (uint)g.Index);
    public void GSet(WasmGlobal g) => Emit(Op.GlobalSet, (uint)g.Index);

    /// <summary>A load or store with its natural alignment.</summary>
    public void Mem(byte op, uint offset = 0)
    {
        Bytes.Add(op);
        Leb.U(Bytes, Op.Align(op));
        Leb.U(Bytes, offset);
    }

    public void Call(WasmFunc f)
    {
        Bytes.Add(Op.Call);
        Calls.Add((Bytes.Count, f));
    }

    public void CallIndirect(int typeIndex)
    {
        Bytes.Add(Op.CallIndirect);
        Leb.U(Bytes, (uint)typeIndex);
        Bytes.Add(0);
    }

    // block types: null = empty, a single value type, or a type index (multi-value)
    void BlockType(ValType? t)
    {
        if (t is { } v) Bytes.Add((byte)v); else Bytes.Add(0x40);
    }

    WasmLabel Open(byte op, ValType? t, bool loop = false)
    {
        Bytes.Add(op);
        BlockType(t);
        var l = new WasmLabel { Depth = labels.Count, IsLoop = loop };
        labels.Add(l);
        return l;
    }

    public WasmLabel Block(ValType? t = null) => Open(Op.Block, t);
    public WasmLabel Loop(ValType? t = null) => Open(Op.Loop, t, loop: true);
    public WasmLabel If(ValType? t = null) => Open(Op.If, t);
    public void Else() => Bytes.Add(Op.Else);

    /// <summary>`try_table` catching <paramref name="tag"/> into the enclosing label <paramref name="handler"/>.</summary>
    public WasmLabel TryTable(ValType? t, WasmTag tag, WasmLabel handler)
    {
        Bytes.Add(Op.TryTable);
        BlockType(t);
        Leb.U(Bytes, 1);
        Bytes.Add(0x00);   // catch tag label
        Leb.U(Bytes, (uint)tag.Index);
        Leb.U(Bytes, (uint)Rel(handler));
        var l = new WasmLabel { Depth = labels.Count };
        labels.Add(l);
        return l;
    }

    public void End(WasmLabel l)
    {
        if (labels.Count == 0 || labels[^1] != l) throw new InvalidOperationException("wasm: unbalanced block end");
        labels.RemoveAt(labels.Count - 1);
        Bytes.Add(Op.End);
    }

    int Rel(WasmLabel l)
    {
        int d = labels.Count - 1 - l.Depth;
        if (d < 0 || labels[l.Depth] != l) throw new InvalidOperationException("wasm: branch to a closed label");
        return d;
    }

    public void Br(WasmLabel l) => Emit(Op.Br, (uint)Rel(l));
    public void BrIf(WasmLabel l) => Emit(Op.BrIf, (uint)Rel(l));
    public void BrTable(IReadOnlyList<WasmLabel> targets, WasmLabel dflt)
    {
        Bytes.Add(Op.BrTable);
        Leb.U(Bytes, (uint)targets.Count);
        foreach (var t in targets) Leb.U(Bytes, (uint)Rel(t));
        Leb.U(Bytes, (uint)Rel(dflt));
    }

    public void Throw(WasmTag t) => Emit(Op.Throw, (uint)t.Index);

    /// <summary>Raw depth branches (the .wat reader resolves its own label names).</summary>
    public void BrDepth(byte op, uint depth) => Emit(op, depth);

    readonly List<(int Pos, WasmCode Frag)> inserts = new();

    /// <summary>A fragment (same locals, its own bytes) inserted at <paramref name="pos"/> by <see cref="Splice"/>:
    /// prologues and epilogues whose contents are known only after the body (the frame size).</summary>
    public void InsertLater(int pos, WasmCode frag) => inserts.Add((pos, frag));

    /// <summary>A code buffer for <see cref="InsertLater"/> (balanced blocks only).</summary>
    public static WasmCode Fragment() => new(new FuncSig([], []));

    public void Splice()
    {
        if (inserts.Count == 0) return;
        var ins = inserts.Select((x, i) => (x.Pos, x.Frag, i)).OrderBy(x => x.Pos).ThenBy(x => x.i).ToList();
        inserts.Clear();
        var bytes = new List<byte>(Bytes.Count + ins.Sum(x => x.Frag.Bytes.Count));
        var calls = new List<(int, WasmFunc)>();
        var marks = new List<(int, string, int, int)>();
        int k = 0, ci = 0, mi = 0;
        for (int i = 0; i <= Bytes.Count; i++)
        {
            // fixups at i belong to the instruction before (call index LEB): they come first
            while (ci < Calls.Count && Calls[ci].Pos == i) { calls.Add((bytes.Count, Calls[ci].F)); ci++; }
            while (k < ins.Count && ins[k].Pos == i)
            {
                var f = ins[k].Frag;
                int at = bytes.Count;
                foreach (var (p, g) in f.Calls) calls.Add((at + p, g));
                bytes.AddRange(f.Bytes);
                k++;
            }
            while (mi < Marks.Count && Marks[mi].Pos == i) { var m = Marks[mi]; marks.Add((bytes.Count, m.File, m.Line, m.Col)); mi++; }
            if (i < Bytes.Count) bytes.Add(Bytes[i]);
        }
        Bytes.Clear(); Bytes.AddRange(bytes);
        Calls.Clear(); Calls.AddRange(calls.OrderBy(x => x.Item1));
        Marks.Clear(); Marks.AddRange(marks);
    }

    /// <summary>The final function end (closes the implicit function block).</summary>
    public void Finish()
    {
        if (labels.Count != 0) throw new InvalidOperationException("wasm: " + labels.Count + " blocks left open");
        Bytes.Add(Op.End);
    }
}

public static class Leb
{
    public static void U(List<byte> o, ulong v)
    {
        do
        {
            byte b = (byte)(v & 0x7F);
            v >>= 7;
            if (v != 0) b |= 0x80;
            o.Add(b);
        } while (v != 0);
    }

    public static void S(List<byte> o, long v)
    {
        while (true)
        {
            byte b = (byte)(v & 0x7F);
            v >>= 7;
            bool done = v == 0 && (b & 0x40) == 0 || v == -1 && (b & 0x40) != 0;
            if (!done) b |= 0x80;
            o.Add(b);
            if (done) return;
        }
    }

    public static int SizeU(ulong v)
    {
        int n = 0;
        do { v >>= 7; n++; } while (v != 0);
        return n;
    }
}

/// <summary>The module: types, imports, functions, table, memory, globals, tags, exports, data, names, and a source map.</summary>
public sealed class WasmModule
{
    readonly List<FuncSig> types = new();
    readonly Dictionary<string, int> typeIndex = new();
    public readonly List<WasmFunc> Funcs = new();
    public readonly List<WasmGlobal> Globals = new();
    public readonly List<WasmTag> Tags = new();
    public readonly List<WasmFunc> Table = new();
    public int MemoryPages = 2;
    public string? MemoryExport = "memory";
    public WasmFunc? Start;
    /// <summary>Static data placed at <see cref="DataBase"/> (string literals, type info, statics).</summary>
    public readonly List<byte> Data = new();
    public int DataBase = 1024;
    public readonly List<(string Name, byte[] Bytes)> Custom = new();
    /// <summary>Source positions of the last encode: absolute module offsets.</summary>
    public readonly List<SourceMark> SourceMap = new();
    /// <summary>Name of the source map file (a `sourceMappingURL` custom section), when one is written.</summary>
    public string? SourceMapUrl;

    public int TypeIndex(FuncSig s)
    {
        if (typeIndex.TryGetValue(s.Key, out int i)) return i;
        types.Add(s);
        return typeIndex[s.Key] = types.Count - 1;
    }

    public WasmFunc Import(string module, string field, string name, FuncSig sig)
    {
        var f = Funcs.FirstOrDefault(x => x.Import is { } im && im.Module == module && im.Field == field);
        if (f != null) return f;
        f = new WasmFunc { Name = name, Sig = sig, Import = (module, field) };
        Funcs.Add(f);
        return f;
    }

    public WasmFunc Define(string name, FuncSig sig, IEnumerable<string?>? paramNames = null)
    {
        var f = new WasmFunc { Name = name, Sig = sig, Code = new WasmCode(sig, paramNames) };
        Funcs.Add(f);
        return f;
    }

    public WasmGlobal Global(string name, ValType t, bool mutable, long init = 0, double initF = 0)
    {
        var g = new WasmGlobal { Name = name, Type = t, Mutable = mutable, InitI = init, InitF = initF, Index = Globals.Count };
        Globals.Add(g);
        return g;
    }

    public WasmTag Tag(string name, FuncSig sig)
    {
        var t = new WasmTag { Name = name, Sig = sig, Index = Tags.Count };
        Tags.Add(t);
        return t;
    }

    /// <summary>The table slot of <paramref name="f"/> (assigned on first use; slot 0 stays null so a zero
    /// function pointer traps).</summary>
    public int Slot(WasmFunc f)
    {
        if (f.TableSlot >= 0) return f.TableSlot;
        if (Table.Count == 0) Table.Add(null!);
        Table.Add(f);
        return f.TableSlot = Table.Count - 1;
    }

    /// <summary>Appends static bytes (aligned to <paramref name="align"/>); returns their linear-memory address.</summary>
    public int AddData(ReadOnlySpan<byte> bytes, int align = 8)
    {
        while (Data.Count % align != 0) Data.Add(0);
        int at = DataBase + Data.Count;
        foreach (var b in bytes) Data.Add(b);
        return at;
    }

    public int DataEnd => DataBase + Data.Count;

    // ------------------------------------------------------------------ encoding

    static void Vec(List<byte> o, IReadOnlyCollection<byte> items) { o.AddRange(items); }

    static void Name(List<byte> o, string s)
    {
        var b = Encoding.UTF8.GetBytes(s);
        Leb.U(o, (uint)b.Length);
        o.AddRange(b);
    }

    static void Section(List<byte> o, byte id, List<byte> body)
    {
        o.Add(id);
        Leb.U(o, (uint)body.Count);
        o.AddRange(body);
    }

    /// <summary>Functions reachable from the exports, the start function and the table (the table holds closures
    /// and virtual methods, already filtered by the emitter).</summary>
    List<WasmFunc> Reachable()
    {
        var seen = new HashSet<WasmFunc>();
        var work = new Stack<WasmFunc>();
        void Add(WasmFunc? f) { if (f != null && seen.Add(f)) work.Push(f); }
        foreach (var f in Funcs) if (f.Export != null) Add(f);
        Add(Start);
        foreach (var f in Table) Add(f);
        while (work.Count > 0)
        {
            var f = work.Pop();
            if (f.Code != null) foreach (var (_, g) in f.Code.Calls) Add(g);
        }
        return Funcs.Where(seen.Contains).ToList();
    }

    public byte[] Encode(bool names = true)
    {
        var live = Reachable();
        var imports = live.Where(f => f.Import != null).ToList();
        var defs = live.Where(f => f.Import == null).ToList();
        foreach (var f in Funcs) f.Index = -1;
        int idx = 0;
        foreach (var f in imports) f.Index = idx++;
        foreach (var f in defs) f.Index = idx++;
        foreach (var f in live) TypeIndex(f.Sig);
        foreach (var t in Tags) TypeIndex(t.Sig);

        var o = new List<byte> { 0x00, 0x61, 0x73, 0x6D, 0x01, 0x00, 0x00, 0x00 };
        var s = new List<byte>();

        // 1 types
        Leb.U(s, (uint)types.Count);
        foreach (var t in types)
        {
            s.Add(0x60);
            Leb.U(s, (uint)t.Params.Length); foreach (var p in t.Params) s.Add((byte)p);
            Leb.U(s, (uint)t.Results.Length); foreach (var r in t.Results) s.Add((byte)r);
        }
        Section(o, 1, s);

        // 2 imports
        if (imports.Count > 0)
        {
            s = new();
            Leb.U(s, (uint)imports.Count);
            foreach (var f in imports)
            {
                Name(s, f.Import!.Value.Module);
                Name(s, f.Import!.Value.Field);
                s.Add(0x00);
                Leb.U(s, (uint)TypeIndex(f.Sig));
            }
            Section(o, 2, s);
        }

        // 3 functions
        s = new();
        Leb.U(s, (uint)defs.Count);
        foreach (var f in defs) Leb.U(s, (uint)TypeIndex(f.Sig));
        Section(o, 3, s);

        // 4 table
        if (Table.Count > 0)
        {
            s = new();
            Leb.U(s, 1);
            s.Add(0x70);
            s.Add(0x01);
            Leb.U(s, (uint)Table.Count);
            Leb.U(s, (uint)Table.Count);
            Section(o, 4, s);
        }

        // 5 memory
        s = new();
        Leb.U(s, 1);
        s.Add(0x00);
        Leb.U(s, (uint)Math.Max(MemoryPages, (DataEnd + 0xFFFF) >> 16));
        Section(o, 5, s);

        // 13 tags (between memory and globals)
        if (Tags.Count > 0)
        {
            s = new();
            Leb.U(s, (uint)Tags.Count);
            foreach (var t in Tags) { s.Add(0x00); Leb.U(s, (uint)TypeIndex(t.Sig)); }
            Section(o, 13, s);
        }

        // 6 globals
        if (Globals.Count > 0)
        {
            s = new();
            Leb.U(s, (uint)Globals.Count);
            foreach (var g in Globals)
            {
                s.Add((byte)g.Type);
                s.Add(g.Mutable ? (byte)1 : (byte)0);
                switch (g.Type)
                {
                    case ValType.I32: s.Add(Op.I32Const); Leb.S(s, (int)g.InitI); break;
                    case ValType.I64: s.Add(Op.I64Const); Leb.S(s, g.InitI); break;
                    case ValType.F32: s.Add(Op.F32Const); s.AddRange(BitConverter.GetBytes((float)g.InitF)); break;
                    case ValType.F64: s.Add(Op.F64Const); s.AddRange(BitConverter.GetBytes(g.InitF)); break;
                    default: throw new InvalidOperationException("wasm: global type " + g.Type);
                }
                s.Add(Op.End);
            }
            Section(o, 6, s);
        }

        // 7 exports
        var exports = live.Where(f => f.Export != null).ToList();
        var gexports = Globals.Where(g => g.Export != null).ToList();
        s = new();
        Leb.U(s, (uint)(exports.Count + gexports.Count + (MemoryExport != null ? 1 : 0)));
        foreach (var f in exports) { Name(s, f.Export!); s.Add(0x00); Leb.U(s, (uint)f.Index); }
        foreach (var g in gexports) { Name(s, g.Export!); s.Add(0x03); Leb.U(s, (uint)g.Index); }
        if (MemoryExport != null) { Name(s, MemoryExport); s.Add(0x02); Leb.U(s, 0); }
        Section(o, 7, s);

        // 8 start
        if (Start != null)
        {
            s = new();
            Leb.U(s, (uint)Start.Index);
            Section(o, 8, s);
        }

        // 9 elements: one active segment for the whole table (slot 0 is ref.null)
        if (Table.Count > 0)
        {
            s = new();
            Leb.U(s, 1);
            // flags 4: active, table 0, element expressions (allows a null slot)
            Leb.U(s, 4);
            s.Add(Op.I32Const); Leb.S(s, 0); s.Add(Op.End);
            Leb.U(s, (uint)Table.Count);
            foreach (var f in Table)
            {
                if (f == null) { s.Add(0xD0); s.Add(0x70); s.Add(Op.End); }
                else { s.Add(0xD2); Leb.U(s, (uint)f.Index); s.Add(Op.End); }
            }
            Section(o, 9, s);
        }

        // 12 data count (required by memory.init; harmless otherwise)
        if (Data.Count > 0)
        {
            s = new();
            Leb.U(s, 1);
            Section(o, 12, s);
        }

        // 10 code
        SourceMap.Clear();
        s = new();
        Leb.U(s, (uint)defs.Count);
        var bodyStarts = new List<(WasmFunc F, int Start)>();
        foreach (var f in defs)
        {
            var body = new List<byte>(f.Code!.Bytes.Count + 16);
            // locals, run-length encoded
            var runs = new List<(int N, ValType T)>();
            foreach (var t in f.Code.Locals)
                if (runs.Count > 0 && runs[^1].T == t) runs[^1] = (runs[^1].N + 1, t); else runs.Add((1, t));
            Leb.U(body, (uint)runs.Count);
            foreach (var (n, t) in runs) { Leb.U(body, (uint)n); body.Add((byte)t); }
            int codeStart = body.Count;
            var marks = f.Code.Marks;
            int mi = 0;
            var markAt = new List<(int BodyPos, string File, int Line, int Col)>();
            int ci = 0;
            var bytes = f.Code.Bytes;
            for (int i = 0; i <= bytes.Count; i++)
            {
                while (mi < marks.Count && marks[mi].Pos == i) { markAt.Add((body.Count, marks[mi].File, marks[mi].Line, marks[mi].Col)); mi++; }
                while (ci < f.Code.Calls.Count && f.Code.Calls[ci].Pos == i)
                {
                    var g = f.Code.Calls[ci].F;
                    if (g.Index < 0) throw new InvalidOperationException("wasm: call to an unreached function " + g.Name);
                    Leb.U(body, (uint)g.Index);
                    ci++;
                }
                if (i < bytes.Count) body.Add(bytes[i]);
            }
            Leb.U(s, (uint)body.Count);
            int at = s.Count;
            s.AddRange(body);
            foreach (var m in markAt) bodyStarts.Add((f, at + m.BodyPos));
            foreach (var m in markAt) SourceMap.Add(new SourceMark(at + m.BodyPos, m.File, m.Line, m.Col));
        }
        o.Add(10);
        Leb.U(o, (uint)s.Count);
        int codeBase = o.Count;
        o.AddRange(s);
        for (int i = 0; i < SourceMap.Count; i++) SourceMap[i] = SourceMap[i] with { Offset = SourceMap[i].Offset + codeBase };

        // 11 data
        if (Data.Count > 0)
        {
            s = new();
            Leb.U(s, 1);
            s.Add(0x00);
            s.Add(Op.I32Const); Leb.S(s, DataBase); s.Add(Op.End);
            Leb.U(s, (uint)Data.Count);
            s.AddRange(Data);
            Section(o, 11, s);
        }

        // custom: name section (functions and locals)
        if (names)
        {
            s = new();
            Name(s, "name");
            var sub = new List<byte>();
            Leb.U(sub, (uint)live.Count);
            foreach (var f in live.OrderBy(f => f.Index)) { Leb.U(sub, (uint)f.Index); Name(sub, f.Name); }
            s.Add(1);
            Leb.U(s, (uint)sub.Count);
            s.AddRange(sub);
            sub = new();
            var withLocals = defs.Where(f => f.Code!.LocalNames.Any(n => n != null)).ToList();
            Leb.U(sub, (uint)withLocals.Count);
            foreach (var f in withLocals)
            {
                Leb.U(sub, (uint)f.Index);
                var ln = f.Code!.LocalNames.Select((n, i) => (n, i)).Where(x => x.n != null).ToList();
                Leb.U(sub, (uint)ln.Count);
                foreach (var (n, i) in ln) { Leb.U(sub, (uint)i); Name(sub, n!); }
            }
            s.Add(2);
            Leb.U(s, (uint)sub.Count);
            s.AddRange(sub);
            o.Add(0);
            Leb.U(o, (uint)s.Count);
            o.AddRange(s);
        }
        foreach (var (n, b) in Custom)
        {
            s = new();
            Name(s, n);
            s.AddRange(b);
            o.Add(0);
            Leb.U(o, (uint)s.Count);
            o.AddRange(s);
        }
        if (SourceMapUrl != null)
        {
            s = new();
            Name(s, "sourceMappingURL");
            Name(s, SourceMapUrl);
            o.Add(0);
            Leb.U(o, (uint)s.Count);
            o.AddRange(s);
        }
        return o.ToArray();
    }

    /// <summary>A source map (v3) for the last <see cref="Encode"/>: wasm source maps use line 1 and the module
    /// byte offset as the generated column.</summary>
    public string SourceMapJson()
    {
        var files = SourceMap.Select(m => m.File).Distinct().ToList();
        var sb = new StringBuilder();
        int pc = 0, pf = 0, pl = 0, pcol = 0;
        bool first = true;
        foreach (var m in SourceMap.OrderBy(m => m.Offset))
        {
            if (!first) sb.Append(',');
            first = false;
            int f = files.IndexOf(m.File);
            Vlq(sb, m.Offset - pc); Vlq(sb, f - pf); Vlq(sb, m.Line - 1 - pl); Vlq(sb, m.Col - 1 - pcol);
            pc = m.Offset; pf = f; pl = m.Line - 1; pcol = m.Col - 1;
        }
        return "{\"version\":3,\"sources\":[" + string.Join(",", files.Select(f => "\"" + f.Replace("\\", "/").Replace("\"", "\\\"") + "\"")) + "],\"names\":[],\"mappings\":\"" + sb + "\"}";
    }

    const string B64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

    static void Vlq(StringBuilder sb, int v)
    {
        uint u = v < 0 ? ((uint)(-v) << 1) | 1 : (uint)v << 1;
        do
        {
            uint d = u & 31;
            u >>= 5;
            if (u != 0) d |= 32;
            sb.Append(B64[(int)d]);
        } while (u != 0);
    }
}
