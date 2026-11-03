using Alice.Compiler.Mir;

namespace Alice.Compiler.Backend.Wasm;

/// <summary>A variable of a body: a wasm local (numbers, booleans), a frame slot (references, so the collector
/// sees them, and structs, stored inline) or a field of the closure environment (captured by a lambda).</summary>
internal sealed class Var
{
    public string Name = "";
    public WT T = null!;
    public int Local = -1;
    public int Frame = -1;
    /// <summary>Offset in the environment object of <see cref="EnvHolder"/>.</summary>
    public int EnvOff = -1;
    public Var? EnvHolder;
    /// <summary>Struct parameter passed by the caller's address (read-only unless copied).</summary>
    public int AddrLocal = -1;
    public override string ToString() => Name + ":" + T;
}

/// <summary>
/// The body of one function: walks post-optimizer AIR and emits wasm (ADR 0034). AIR is still partly C# text (`var`
/// locals, MCode leaves, calls by name), so this emitter types every expression itself (<see cref="Ty"/>: the node
/// emitted into a scratch buffer, memoized), resolving names against locals, parameters, Alice declarations and the
/// runtime intrinsics. Expressions in WasmBody.Exprs.cs, calls in WasmBody.Calls.cs, statements in WasmBody.Stmts.cs.
/// Mirrors Backend/Il/AirToIl.cs (IlBody).
/// </summary>
public sealed partial class WasmBody
{
    readonly AirToWasm c;
    readonly WasmModule M;
    internal readonly WMethod self;
    readonly WasmFunc fn;
    WasmCode code;
    readonly WasmCode body;
    readonly WClass owner;
    readonly bool isStatic;
    readonly List<Dictionary<string, Var>> scopes = [new(StringComparer.Ordinal)];
    readonly Dictionary<MExpr, WT> inferred = new(ReferenceEqualityComparer.Instance);
    int fpLocal = -1, fpEndLocal = -1;
    int frameSize;
    int sretLocal = -1, thisLocal = -1;
    bool scratch;
    int uncheckedDepth;
    Span? span;
    readonly List<int> epilogues = [];
    /// <summary>The enclosing function's environment when this body is a lambda.</summary>
    readonly LambdaInfo? lambda;
    /// <summary>The environment object of this body (captured locals), null when nothing is captured.</summary>
    Var? env;
    readonly HashSet<string> captured = new(StringComparer.Ordinal);

    internal WasmBody(AirToWasm c, WMethod self, LambdaInfo? lambda = null)
    {
        this.c = c;
        M = c.M;
        this.self = self;
        fn = self.Func;
        body = code = fn.Code!;
        owner = self.Owner;
        isStatic = self.IsStatic;
        this.lambda = lambda;
        int k = 0;
        if (self.Ret.IsStruct) sretLocal = k++;
        if (!isStatic) thisLocal = k++;
        for (int i = 0; i < self.Params.Count; i++)
        {
            var v = new Var { Name = AirToWasm.Bare(self.ParamNames[i]), T = self.Params[i] };
            if (v.T.IsStruct) v.AddrLocal = k;
            else v.Local = k;
            k++;
            scopes[0][v.Name] = v;
        }
    }

    bool Checked => uncheckedDepth == 0;

    WasmUnsupportedException No(string kind, string? detail = null) => new(kind, span, detail);

    void Seen(string kind) { if (!scratch) c.Stats.Seen(kind); }

    // ------------------------------------------------------------------ typing

    /// <summary>The natural type of <paramref name="e"/>: emitted into a scratch buffer and discarded (memoized per
    /// node). Frame slots, locals and scopes made while typing are rolled back.</summary>
    internal WT Ty(MExpr e)
    {
        if (inferred.TryGetValue(e, out var t)) return t;
        var saved = code;
        bool wasScratch = scratch;
        int fs = frameSize;
        int sd = scopes.Count;
        int ud = uncheckedDepth;
        code = new WasmCode(fn.Sig);
        // locals of the real body keep their indices in the scratch buffer
        for (int i = 0; i < body.Locals.Count; i++) code.Local(body.Locals[i]);
        scratch = true;
        scopes.Add(new(StringComparer.Ordinal));
        try { t = Raw(e); }
        finally
        {
            code = saved;
            scratch = wasScratch;
            frameSize = fs;
            while (scopes.Count > sd) scopes.RemoveAt(scopes.Count - 1);
            uncheckedDepth = ud;
        }
        inferred[e] = t;
        return t;
    }

    // ------------------------------------------------------------------ locals and the frame

    Var? Find(string name)
    {
        name = AirToWasm.Bare(name);
        for (int i = scopes.Count - 1; i >= 0; i--)
            if (scopes[i].TryGetValue(name, out var v)) return v;
        if (lambda != null) return lambda.Find(name);
        return null;
    }

    int Fp()
    {
        if (fpLocal < 0)
        {
            fpLocal = body.Local(ValType.I32, "__fp");
            fpEndLocal = body.Local(ValType.I32, "__fp_end");
            if (code != body) { code.Local(ValType.I32); code.Local(ValType.I32); }
        }
        return fpLocal;
    }

    /// <summary>A zeroed slot in this function's shadow-stack frame.</summary>
    int FrameSlot(int size, int align)
    {
        Fp();
        align = Math.Max(4, Math.Min(align, 8));
        frameSize = (frameSize + align - 1) / align * align;
        int at = frameSize;
        frameSize += Math.Max(size, 4);
        return at;
    }

    int maxFrame;

    int Local(ValType t, string? name = null) => code != body ? code.Local(t) : body.Local(t, name);

    /// <summary>A scratch local for one expression (not named).</summary>
    int Temp(ValType t) => Local(t);

    Var Declare(string name, WT t)
    {
        name = AirToWasm.Bare(name);
        var v = new Var { Name = name, T = t };
        if (captured.Contains(name) && env != null && !scratch) BindEnv(v);
        else if (t.IsStruct) { t.Cls!.Lay(); v.Frame = FrameSlot(t.Size, t.Align); Track(); }
        else if (t.IsRef) { v.Frame = FrameSlot(4, 4); Track(); }
        else v.Local = Local(t.Val, name);
        scopes[^1][name] = v;
        return v;
    }

    void Track() { if (!scratch) maxFrame = Math.Max(maxFrame, frameSize); }

    /// <summary>A frame temp for a reference or struct value (rooted while later operands allocate).</summary>
    Var FrameTemp(WT t)
    {
        var v = new Var { Name = "", T = t };
        if (t.IsStruct) { t.Cls!.Lay(); v.Frame = FrameSlot(t.Size, t.Align); }
        else v.Frame = FrameSlot(4, 4);
        Track();
        return v;
    }

    /// <summary>Pushes the address of a frame slot.</summary>
    void FrameAddr(int off)
    {
        code.Get(Fp());
        if (off != 0) { code.I32(off); code.Emit(Op.I32Add); }
    }

    // ------------------------------------------------------------------ entry points

    /// <summary>A method or main body: prologue (frame, reference parameters rooted, captured parameters moved to
    /// the environment), the statements, an implicit return.</summary>
    public void Body(List<MStmt> stmts, Action? prologue = null)
    {
        PrepareCaptures(stmts);
        RootParams();
        prologue?.Invoke();
        Stmts(stmts);
        if (self.Ret.K != WK.Void && !self.Ret.IsStruct && !EndsInReturn(stmts))
        {
            // a non-void body that falls off its end is unreachable (the binder guarantees a return)
            code.Emit(Op.Unreachable);
        }
        else if (!EndsInReturn(stmts)) Epilogue();
        Finish();
    }

    static bool EndsInReturn(List<MStmt> s) => s.Count > 0 && s[^1] is MReturn or MThrow;

    /// <summary>Reference parameters are copied into frame slots: the caller's value may be a fresh object nothing
    /// else roots. `this` too.</summary>
    void RootParams()
    {
        foreach (var v in scopes[0].Values.ToList())
        {
            if (v.T.IsRef && v.Local >= 0 && v.EnvOff < 0)
            {
                int param = v.Local;
                v.Local = -1;
                v.Frame = FrameSlot(4, 4);
                Track();
                FrameAddr(v.Frame);
                code.Get(param);
                code.Mem(Op.I32Store);
            }
        }
        if (thisLocal >= 0 && !owner.IsStruct)
        {
            thisSlot = FrameSlot(4, 4);
            Track();
            FrameAddr(thisSlot);
            code.Get(thisLocal);
            code.Mem(Op.I32Store);
        }
        // captured parameters (and `this`): the environment holds them
        if (env != null)
        {
            foreach (var v in scopes[0].Values.ToList())
                if (captured.Contains(v.Name) && v.EnvOff < 0) MoveToEnv(v);
            if (captured.Contains("this") && thisLocal >= 0)
            {
                var tv = new Var { Name = "this", T = owner.Type };
                BindEnv(tv);
                StoreVar(tv, () => { if (owner.IsStruct) throw No("Closure:captured-struct-this"); code.Get(thisLocal); return owner.Type; });
                thisEnv = tv;
            }
        }
    }

    int thisSlot = -1;
    Var? thisEnv;

    /// <summary>Pops the frame (before every return).</summary>
    void Epilogue()
    {
        if (scratch) return;
        epilogues.Add(code.Length);
    }

    void Finish()
    {
        int size = Math.Max(maxFrame, frameSize);
        size = (size + 7) & ~7;
        if (fpLocal >= 0 && size > 0)
        {
            var pro = WasmCode.Fragment();
            pro.I32(size);
            pro.Call(c.Rt.Func("frame_enter"));
            pro.Tee(fpLocal);
            pro.I32(size);
            pro.Emit(Op.I32Add);
            pro.Set(fpEndLocal);
            body.InsertLater(0, pro);
            foreach (var at in epilogues)
            {
                var ep = WasmCode.Fragment();
                ep.Get(fpEndLocal);
                ep.GSet(c.Rt.Global("sp"));
                body.InsertLater(at, ep);
            }
        }
        body.Splice();
        body.Finish();
    }

    void Mark(int line)
    {
        if (scratch || line <= 0) return;
        code.Mark(c.File, line, 1);
    }

    void MarkSpan(Span? s)
    {
        if (scratch || s is not { } v || v.Line <= 0) return;
        code.Mark(v.File.Length > 0 ? v.File : c.File, v.Line, Math.Max(1, v.Col));
    }
}
