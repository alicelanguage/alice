using System.Globalization;
using System.Text;

namespace Alice.Compiler.Backend.Wasm;

/// <summary>
/// Assembles the hand-written runtime (src/Alice.Core.Wasm/*.wat) into a <see cref="WasmModule"/>: the subset of the
/// wasm text format the runtime uses (functions, globals, imports; folded and flat instructions; named locals,
/// labels, globals and functions). `(import "alice" ...)` functions are bound to compiler-generated functions
/// (<see cref="Bind"/>); wasi imports become module imports; `(memory)` is ignored (the backend owns the layout).
/// The same file is a valid module for wabt.
/// </summary>
public sealed class WatReader
{
    readonly WasmModule m;
    public readonly Dictionary<string, WasmFunc> Funcs = new(StringComparer.Ordinal);
    public readonly Dictionary<string, WasmGlobal> Globals = new(StringComparer.Ordinal);
    readonly Dictionary<string, Func<WasmFunc>> hostFuncs = new(StringComparer.Ordinal);
    readonly List<(WasmFunc F, Sx Node)> bodies = new();

    public WatReader(WasmModule m) => this.m = m;

    /// <summary>`(import "alice" "<paramref name="name"/>" ...)` calls the function <paramref name="make"/> returns.</summary>
    public void Bind(string name, Func<WasmFunc> make) => hostFuncs[name] = make;

    // ------------------------------------------------------------------ s-expressions

    sealed class Sx
    {
        public string? Atom;
        public bool Str;
        public List<Sx> Items = new();
        public bool IsList => Atom == null;
        public string Head => Items.Count > 0 && Items[0].Atom is { } a ? a : "";
        public override string ToString() => Atom ?? "(" + string.Join(" ", Items) + ")";
    }

    static List<Sx> Parse(string text)
    {
        var stack = new Stack<Sx>();
        var top = new Sx();
        stack.Push(top);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == ';' && i + 1 < text.Length && text[i + 1] == ';') { while (i < text.Length && text[i] != '\n') i++; continue; }
            if (c == '(' && i + 1 < text.Length && text[i + 1] == ';')
            {
                int e = text.IndexOf(";)", i + 2, StringComparison.Ordinal);
                i = e < 0 ? text.Length : e + 2;
                continue;
            }
            if (c == '(') { var l = new Sx(); stack.Peek().Items.Add(l); stack.Push(l); i++; continue; }
            if (c == ')') { stack.Pop(); i++; continue; }
            if (c == '"')
            {
                var sb = new StringBuilder();
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length) { sb.Append(text[i + 1] switch { 'n' => '\n', 't' => '\t', _ => text[i + 1] }); i += 2; }
                    else sb.Append(text[i++]);
                }
                i++;
                stack.Peek().Items.Add(new Sx { Atom = sb.ToString(), Str = true });
                continue;
            }
            int s = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '(' && text[i] != ')') i++;
            stack.Peek().Items.Add(new Sx { Atom = text[s..i] });
        }
        return top.Items;
    }

    static ValType Vt(string s) => s switch
    {
        "i32" => ValType.I32, "i64" => ValType.I64, "f32" => ValType.F32, "f64" => ValType.F64, "v128" => ValType.V128,
        "funcref" => ValType.FuncRef, "exnref" => ValType.ExnRef,
        _ => throw new FormatException("wat: unknown value type " + s),
    };

    // ------------------------------------------------------------------ module fields

    /// <summary>Declares the fields of <paramref name="text"/>; bodies are assembled by <see cref="Finish"/> once
    /// every function of every runtime file is known.</summary>
    public void Load(string text)
    {
        var top = Parse(text);
        var fields = top.Count == 1 && top[0].Head == "module" ? top[0].Items.Skip(1).ToList() : top;
        foreach (var f in fields)
        {
            switch (f.Head)
            {
                case "import": Import(f); break;
                case "global": Global(f); break;
                case "func": DeclareFunc(f); break;
                case "memory" or "type" or "export" or "data" or "tag": break;
                default: throw new FormatException("wat: unsupported field " + f.Head);
            }
        }
    }

    (List<(string? Name, ValType T)> Ps, List<ValType> Rs) Signature(IEnumerable<Sx> items)
    {
        var ps = new List<(string?, ValType)>();
        var rs = new List<ValType>();
        foreach (var x in items)
        {
            if (!x.IsList) continue;
            if (x.Head == "param")
            {
                if (x.Items.Count == 3 && x.Items[1].Atom!.StartsWith('$')) ps.Add((x.Items[1].Atom, Vt(x.Items[2].Atom!)));
                else foreach (var t in x.Items.Skip(1)) ps.Add((null, Vt(t.Atom!)));
            }
            else if (x.Head == "result") foreach (var t in x.Items.Skip(1)) rs.Add(Vt(t.Atom!));
        }
        return (ps, rs);
    }

    void Import(Sx f)
    {
        string mod = f.Items[1].Atom!, field = f.Items[2].Atom!;
        var d = f.Items[3];
        if (d.Head != "func") throw new FormatException("wat: only function imports");
        string name = d.Items[1].Atom!;
        var (ps, rs) = Signature(d.Items);
        var sig = new FuncSig(ps.Select(p => p.T).ToArray(), rs.ToArray());
        if (mod == "alice")
        {
            if (!hostFuncs.TryGetValue(field, out var make)) throw new FormatException("wat: no host function alice." + field);
            pendingHost[name] = (make, sig);
            return;
        }
        Funcs[name] = m.Import(mod, field, name.TrimStart('$'), sig);
    }

    readonly Dictionary<string, (Func<WasmFunc> Make, FuncSig Sig)> pendingHost = new(StringComparer.Ordinal);

    void Global(Sx f)
    {
        string name = f.Items[1].Atom!;
        var t = f.Items[2];
        bool mut = t.IsList && t.Head == "mut";
        var vt = Vt(mut ? t.Items[1].Atom! : t.Atom!);
        var init = f.Items[3];
        long iv = 0;
        double fv = 0;
        if (init.Head is "i32.const" or "i64.const") iv = Int(init.Items[1].Atom!);
        else if (init.Head is "f32.const" or "f64.const") fv = double.Parse(init.Items[1].Atom!, CultureInfo.InvariantCulture);
        Globals[name] = m.Global(name.TrimStart('$'), vt, mut, iv, fv);
    }

    void DeclareFunc(Sx f)
    {
        string name = f.Items[1].Atom!;
        var (ps, rs) = Signature(f.Items);
        var fn = m.Define(name.TrimStart('$'), new FuncSig(ps.Select(p => p.T).ToArray(), rs.ToArray()), ps.Select(p => p.Name?.TrimStart('$')));
        foreach (var x in f.Items)
            if (x.IsList && x.Head == "export") fn.Export = x.Items[1].Atom;
        Funcs[name] = fn;
        bodies.Add((fn, f));
    }

    /// <summary>Assembles every declared body (call after all <see cref="Load"/>s and <see cref="Bind"/>s).</summary>
    public void Finish()
    {
        foreach (var (n, (make, _)) in pendingHost) Funcs[n] = make();
        foreach (var (fn, f) in bodies) new Body(this, fn, f).Run();
        bodies.Clear();
    }

    public WasmFunc Func(string name) => Funcs.TryGetValue("$" + name, out var f) ? f : throw new KeyNotFoundException("runtime function $" + name);
    public WasmGlobal Global(string name) => Globals.TryGetValue("$" + name, out var g) ? g : throw new KeyNotFoundException("runtime global $" + name);

    static long Int(string s)
    {
        s = s.Replace("_", "");
        bool neg = s.StartsWith('-');
        if (neg || s.StartsWith('+')) s = s[1..];
        ulong v = s.StartsWith("0x") ? ulong.Parse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) : ulong.Parse(s, CultureInfo.InvariantCulture);
        return neg ? -(long)v : (long)v;
    }

    // ------------------------------------------------------------------ bodies

    sealed class Body
    {
        readonly WatReader r;
        readonly WasmFunc fn;
        readonly Sx node;
        readonly WasmCode c;
        readonly Dictionary<string, int> locals = new(StringComparer.Ordinal);
        readonly List<string?> labels = new();

        public Body(WatReader r, WasmFunc fn, Sx node)
        {
            this.r = r;
            this.fn = fn;
            this.node = node;
            c = fn.Code!;
        }

        public void Run()
        {
            int pi = 0;
            var instrs = new List<Sx>();
            foreach (var x in node.Items.Skip(2))
            {
                if (x.IsList && x.Head == "param")
                {
                    if (x.Items.Count == 3 && x.Items[1].Atom!.StartsWith('$')) locals[x.Items[1].Atom!] = pi++;
                    else pi += x.Items.Count - 1;
                }
                else if (x.IsList && x.Head == "local")
                {
                    if (x.Items.Count == 3 && x.Items[1].Atom!.StartsWith('$')) locals[x.Items[1].Atom!] = c.Local(Vt(x.Items[2].Atom!), x.Items[1].Atom![1..]);
                    else foreach (var t in x.Items.Skip(1)) c.Local(Vt(t.Atom!));
                }
                else if (x.IsList && x.Head is "result" or "export") { }
                else instrs.Add(x);
            }
            Seq(instrs, 0);
            c.Finish();
        }

        int LocalIx(Sx a) => a.Atom!.StartsWith('$') ? locals.TryGetValue(a.Atom!, out int i) ? i : throw new FormatException($"wat: {fn.Name}: unknown local {a.Atom}") : (int)Int(a.Atom!);

        uint LabelDepth(Sx a)
        {
            if (!a.Atom!.StartsWith('$')) return (uint)Int(a.Atom!);
            for (int i = labels.Count - 1; i >= 0; i--)
                if (labels[i] == a.Atom) return (uint)(labels.Count - 1 - i);
            throw new FormatException($"wat: {fn.Name}: unknown label {a.Atom}");
        }

        WasmFunc FuncRef(Sx a) => r.Funcs.TryGetValue(a.Atom!, out var f) ? f : throw new FormatException($"wat: {fn.Name}: unknown function {a.Atom}");
        WasmGlobal GlobalRef(Sx a) => r.Globals.TryGetValue(a.Atom!, out var g) ? g : throw new FormatException($"wat: {fn.Name}: unknown global {a.Atom}");

        /// <summary>Flat instruction sequence starting at <paramref name="i"/> (folded expressions are list items).</summary>
        void Seq(List<Sx> xs, int i)
        {
            while (i < xs.Count)
            {
                var x = xs[i];
                if (x.IsList) { Folded(x); i++; continue; }
                string op = x.Atom!;
                i++;
                switch (op)
                {
                    case "block" or "loop" or "if":
                        {
                            string? label = null;
                            ValType? bt = null;
                            if (i < xs.Count && xs[i].Atom is { } la && la.StartsWith('$')) { label = la; i++; }
                            if (i < xs.Count && xs[i].IsList && xs[i].Head == "result") { bt = Vt(xs[i].Items[1].Atom!); i++; }
                            c.B(op == "block" ? Op.Block : op == "loop" ? Op.Loop : Op.If);
                            c.B(bt is { } v ? (byte)v : (byte)0x40);
                            labels.Add(label);
                            break;
                        }
                    case "else": c.B(Op.Else); break;
                    case "end":
                        labels.RemoveAt(labels.Count - 1);
                        c.B(Op.End);
                        if (i < xs.Count && xs[i].Atom is { } el && el.StartsWith('$')) i++;
                        break;
                    default:
                        {
                            int n = Immediates(op);
                            Plain(op, xs.GetRange(i, n));
                            i += n;
                            break;
                        }
                }
            }
        }

        static readonly HashSet<string> MemOps = new(StringComparer.Ordinal)
        {
            "i32.load", "i64.load", "f32.load", "f64.load", "i32.load8_s", "i32.load8_u", "i32.load16_s", "i32.load16_u",
            "i64.load8_s", "i64.load8_u", "i64.load16_s", "i64.load16_u", "i64.load32_s", "i64.load32_u",
            "i32.store", "i64.store", "f32.store", "f64.store", "i32.store8", "i32.store16", "i64.store8", "i64.store16", "i64.store32",
        };

        static int Immediates(string op) => op switch
        {
            "local.get" or "local.set" or "local.tee" or "global.get" or "global.set" or "br" or "br_if" or "call"
                or "i32.const" or "i64.const" or "f32.const" or "f64.const" => 1,
            _ => 0,
        };

        void Folded(Sx x)
        {
            string op = x.Head;
            var rest = x.Items.Skip(1).ToList();
            switch (op)
            {
                case "block" or "loop":
                    {
                        int k = 0;
                        string? label = null;
                        ValType? bt = null;
                        if (k < rest.Count && rest[k].Atom is { } la && la.StartsWith('$')) { label = la; k++; }
                        if (k < rest.Count && rest[k].IsList && rest[k].Head == "result") { bt = Vt(rest[k].Items[1].Atom!); k++; }
                        c.B(op == "block" ? Op.Block : Op.Loop);
                        c.B(bt is { } v ? (byte)v : (byte)0x40);
                        labels.Add(label);
                        Seq(rest, k);
                        labels.RemoveAt(labels.Count - 1);
                        c.B(Op.End);
                        return;
                    }
                case "if":
                    {
                        int k = 0;
                        string? label = null;
                        ValType? bt = null;
                        if (k < rest.Count && rest[k].Atom is { } la && la.StartsWith('$')) { label = la; k++; }
                        if (k < rest.Count && rest[k].IsList && rest[k].Head == "result") { bt = Vt(rest[k].Items[1].Atom!); k++; }
                        Sx? then = null, els = null;
                        for (; k < rest.Count; k++)
                        {
                            if (rest[k].IsList && rest[k].Head == "then") then = rest[k];
                            else if (rest[k].IsList && rest[k].Head == "else") els = rest[k];
                            else Folded(rest[k]);   // condition
                        }
                        c.B(Op.If);
                        c.B(bt is { } v ? (byte)v : (byte)0x40);
                        labels.Add(label);
                        if (then != null) Seq(then.Items.Skip(1).ToList(), 0);
                        if (els != null) { c.B(Op.Else); Seq(els.Items.Skip(1).ToList(), 0); }
                        labels.RemoveAt(labels.Count - 1);
                        c.B(Op.End);
                        return;
                    }
            }
            int n = Immediates(op);
            if (MemOps.Contains(op)) n = rest.TakeWhile(a => a.Atom is { } s && (s.StartsWith("offset=") || s.StartsWith("align="))).Count();
            foreach (var a in rest.Skip(n)) Folded(a);
            Plain(op, rest.Take(n).ToList());
        }

        void Plain(string op, List<Sx> imm)
        {
            if (MemOps.Contains(op))
            {
                uint off = 0;
                foreach (var a in imm) if (a.Atom!.StartsWith("offset=")) off = (uint)Int(a.Atom[7..]);
                c.Mem(Opcode(op), off);
                return;
            }
            switch (op)
            {
                case "local.get": c.Get(LocalIx(imm[0])); return;
                case "local.set": c.Set(LocalIx(imm[0])); return;
                case "local.tee": c.Tee(LocalIx(imm[0])); return;
                case "global.get": c.GGet(GlobalRef(imm[0])); return;
                case "global.set": c.GSet(GlobalRef(imm[0])); return;
                case "br": c.BrDepth(Op.Br, LabelDepth(imm[0])); return;
                case "br_if": c.BrDepth(Op.BrIf, LabelDepth(imm[0])); return;
                case "call": c.Call(FuncRef(imm[0])); return;
                case "i32.const": c.I32(unchecked((int)Int(imm[0].Atom!))); return;
                case "i64.const": c.I64(Int(imm[0].Atom!)); return;
                case "f32.const": c.F32(float.Parse(imm[0].Atom!, CultureInfo.InvariantCulture)); return;
                case "f64.const": c.F64(double.Parse(imm[0].Atom!, CultureInfo.InvariantCulture)); return;
                case "memory.copy": c.Prefixed(Op.MemoryCopy); c.B(0); c.B(0); return;
                case "memory.fill": c.Prefixed(Op.MemoryFill); c.B(0); return;
                case "memory.size": c.B(Op.MemorySize); c.B(0); return;
                case "memory.grow": c.B(Op.MemoryGrow); c.B(0); return;
                case "i32.trunc_sat_f32_s": c.Prefixed(Op.I32TruncSatF32S); return;
                case "i32.trunc_sat_f32_u": c.Prefixed(Op.I32TruncSatF32U); return;
                case "i32.trunc_sat_f64_s": c.Prefixed(Op.I32TruncSatF64S); return;
                case "i32.trunc_sat_f64_u": c.Prefixed(Op.I32TruncSatF64U); return;
                case "i64.trunc_sat_f32_s": c.Prefixed(Op.I64TruncSatF32S); return;
                case "i64.trunc_sat_f32_u": c.Prefixed(Op.I64TruncSatF32U); return;
                case "i64.trunc_sat_f64_s": c.Prefixed(Op.I64TruncSatF64S); return;
                case "i64.trunc_sat_f64_u": c.Prefixed(Op.I64TruncSatF64U); return;
            }
            c.B(Opcode(op));
        }

        static readonly Dictionary<string, byte> Ops = BuildOps();

        static Dictionary<string, byte> BuildOps()
        {
            var d = new Dictionary<string, byte>(StringComparer.Ordinal);
            foreach (var f in typeof(Op).GetFields())
            {
                if (f.FieldType != typeof(byte)) continue;
                d[WatName(f.Name)] = (byte)f.GetValue(null)!;
            }
            return d;
        }

        /// <summary>`I32Load8U` -> `i32.load8_u`, `I64ExtendI32S` -> `i64.extend_i32_s`, `F32ConvertI32U` -> `f32.convert_i32_u`.</summary>
        static string WatName(string cs)
        {
            var parts = new List<string>();
            var cur = new StringBuilder();
            foreach (char ch in cs)
            {
                if (char.IsUpper(ch) && cur.Length > 0 && !(char.IsDigit(cur[^1]) && false)) { parts.Add(cur.ToString()); cur.Clear(); }
                cur.Append(char.ToLowerInvariant(ch));
            }
            parts.Add(cur.ToString());
            // parts: i32 load8 u / i64 extend i32 s / i32 lt s / f64 promote f32
            if (parts[0] is "i32" or "i64" or "f32" or "f64")
            {
                string head = parts[0] + "." + parts[1];
                var tail = parts.Skip(2).ToList();
                return tail.Count == 0 ? head : head + "_" + string.Join("_", tail);
            }
            return parts[0] switch
            {
                "unreachable" or "nop" or "drop" or "select" or "return" => parts[0],
                _ => string.Join("_", parts),
            };
        }

        byte Opcode(string op) => Ops.TryGetValue(op, out var b) ? b : throw new FormatException($"wat: {fn.Name}: unknown instruction {op}");
    }
}
