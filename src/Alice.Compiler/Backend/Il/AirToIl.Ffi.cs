using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Alice.Compiler.Semantics;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Backend.Il;

/// <summary>
/// D1.10: FFI in IL (ADR 0018, ADR 0033). The same shapes Backend/Emitter.cs writes as C#, emitted directly:
/// <list type="bullet">
/// <item>`lib` `fun`: a blittable P/Invoke (ImplMap row: EntryPoint, ExactSpelling, CallingConvention WinApi, CharSet
/// none, SetLastError false, PreserveSig; `[SuppressGCTransition]` for `@[Fast]`) behind an inlined wrapper:
/// String/String? as UTF-8 through <see cref="Alice.Runtime.Utf8Arg"/> on a `localloc` buffer (try/finally Dispose),
/// arrays of numbers pinned through a `pinned T&amp;` local, `out`/`ref` as pinned byrefs, Bool as a byte,
/// String returns borrowed (<see cref="Alice.Runtime.Utf8Arg.Decode"/>), callbacks as `delegate* unmanaged[Cdecl]`.
/// Every `fun` of a lib that declares any callback parameter brackets its C call with
/// <see cref="Alice.Runtime.NativeCallback.Enter"/> and <see cref="Alice.Runtime.NativeCallback.Rethrow(int)"/>: C may
/// run a stored callback from a `fun` that takes none.</item>
/// <item>callbacks: a static `[UnmanagedCallersOnly(CallConvs = [CallConvCdecl])]` trampoline (the declared calling
/// convention) that calls the managed
/// body inside try/catch (<see cref="Alice.Runtime.NativeCallback.Fail"/>), passed as `ldftn` + `NativeFn(nint)`.</item>
/// <item>`@[Native]`: `calli` through a function pointer resolved by <see cref="Alice.Runtime.NativeLibs.Export"/> in
/// the static constructor of a nested holder class, with the managed body as the fallback branch.</item>
/// <item>`unsafe` pointer operations: ldind/stind/ldobj/stobj, element addressing, pointer differences, `sizeof`.</item>
/// </list>
/// Independent of AirToIl: types other than primitives, pointers, arrays, strings and function pointers come from the
/// caller's <c>resolve</c> (C# spelling -> IL type, e.g. a user struct `Timespec`).
/// </summary>
public sealed partial class AirToIlFfi
{
    const TypeAttributes StaticClass = TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class | TypeAttributes.BeforeFieldInit;
    const MethodAttributes PublicStatic = MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig;
    const MethodAttributes PrivateStatic = MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig;

    /// <summary>`DllImport(lib, EntryPoint = e, ExactSpelling = true)`: what the C# backend's attribute compiles to.</summary>
    public const MethodImportAttributes ImportFlags = MethodImportAttributes.ExactSpelling | MethodImportAttributes.CallingConventionWinApi;

    readonly Func<string, IlType?>? resolve;
    int callbackN;

    public AirToIlFfi(IlModule module, Func<string, IlType?>? resolve = null)
    {
        Module = module;
        this.resolve = resolve;
    }

    public IlModule Module { get; }

    /// <summary>Every `lib` `fun` -> the method Alice code calls (the wrapper, or the import itself when blittable).</summary>
    public Dictionary<MethodSym, IlMethodDef> Funs { get; } = [];

    /// <summary>Every `fun` -> its P/Invoke row (the same method as <see cref="Funs"/> when no wrapper is needed).</summary>
    public Dictionary<MethodSym, IlMethodDef> Imports { get; } = [];

    static string Clr(string csName) => csName.TrimStart('@');

    // ------------------------------------------------------------------ runtime members

    IlMethod Rt(Type t, string name, params Type[] ps) => Module.Import(t.GetMethod(name, ps) ?? throw new MissingMethodException(t.FullName, name));
    IlMethod Utf8Ctor => Module.Import(typeof(Alice.Runtime.Utf8Arg).GetConstructor([typeof(string), typeof(Span<byte>)])!);
    IlMethod Utf8Pointer => Module.Import(typeof(Alice.Runtime.Utf8Arg).GetProperty(nameof(Alice.Runtime.Utf8Arg.Pointer))!.GetMethod!);
    IlMethod Utf8Dispose => Rt(typeof(Alice.Runtime.Utf8Arg), nameof(Alice.Runtime.Utf8Arg.Dispose));
    IlMethod Utf8Decode => Module.Import(typeof(Alice.Runtime.Utf8Arg).GetMethod(nameof(Alice.Runtime.Utf8Arg.Decode))!);
    IlMethod Utf8DecodeNonNull => Module.Import(typeof(Alice.Runtime.Utf8Arg).GetMethod(nameof(Alice.Runtime.Utf8Arg.DecodeNonNull))!);
    IlMethod SpanCtor => Module.Import(typeof(Span<byte>).GetConstructor([typeof(void*), typeof(int)])!);
    IlMethod NativeFnAddress => Module.Import(typeof(Alice.Runtime.NativeFn).GetProperty(nameof(Alice.Runtime.NativeFn.Address))!.GetMethod!);
    IlMethod NativeFnCtor => Module.Import(typeof(Alice.Runtime.NativeFn).GetConstructor([typeof(nint)])!);
    IlMethod Rethrow => Rt(typeof(Alice.Runtime.NativeCallback), nameof(Alice.Runtime.NativeCallback.Rethrow), typeof(int));
    IlMethod Enter => Rt(typeof(Alice.Runtime.NativeCallback), nameof(Alice.Runtime.NativeCallback.Enter));
    IlMethod Fail => Rt(typeof(Alice.Runtime.NativeCallback), nameof(Alice.Runtime.NativeCallback.Fail), typeof(Exception));
    IlMethod Export => Rt(typeof(Alice.Runtime.NativeLibs), nameof(Alice.Runtime.NativeLibs.Export), typeof(string), typeof(string));
    IlType NativeFnType => Module.Import(typeof(Alice.Runtime.NativeFn));

    IlMethod AttrCtor(Type t) => Module.Import(t.GetConstructor(Type.EmptyTypes)!);

    // ------------------------------------------------------------------ types (C# spelling, as AType.Cs has it)

    /// <summary>The IL type of a C# type spelling: primitives, `T*`, `T[]`, `string?`, `delegate* unmanaged[Cdecl]&lt;...&gt;`,
    /// `global::Alice.Runtime.NativeFn`; anything else through the resolver.</summary>
    public IlType Map(string cs)
    {
        cs = cs.Trim();
        switch (cs)
        {
            case "void": return IlType.Void;
            case "bool": return IlType.Bool;
            case "char": return IlType.Char;
            case "sbyte": return IlType.SByte;
            case "byte": return IlType.Byte;
            case "short": return IlType.Int16;
            case "ushort": return IlType.UInt16;
            case "int": return IlType.Int32;
            case "uint": return IlType.UInt32;
            case "long": return IlType.Int64;
            case "ulong": return IlType.UInt64;
            case "float": return IlType.Single;
            case "double": return IlType.Double;
            case "nint": return IlType.IntPtr;
            case "nuint": return IlType.UIntPtr;
            case "string" or "string?": return IlType.String;
            case "object" or "object?": return IlType.Object;
            case "global::Alice.Runtime.NativeFn": return NativeFnType;
        }
        if (cs.StartsWith("delegate*")) return IlType.FnPtr(FnPtrSig(cs));
        if (cs.EndsWith("[]?")) return Map(cs[..^3]).SzArray();
        if (cs.EndsWith("[]")) return Map(cs[..^2]).SzArray();
        if (cs.EndsWith('*')) return Map(cs[..^1]).Pointer();
        return resolve?.Invoke(cs) ?? throw new NotSupportedException("FFI: no IL type for " + cs);
    }

    /// <summary>`delegate* unmanaged[Cdecl]&lt;A..., R&gt;` (CDecl), `delegate* unmanaged&lt;&gt;` (platform default),
    /// `delegate* unmanaged[SuppressGCTransition]&lt;&gt;` (default + modopt on the return), `delegate*&lt;&gt;` (managed).</summary>
    public MethodSig FnPtrSig(string cs)
    {
        int lt = cs.IndexOf('<');
        string head = cs[..lt].Replace(" ", "");
        var parts = SplitTop(cs[(lt + 1)..^1]).Select(Map).ToList();
        var ret = parts[^1];
        var conv = SignatureCallingConvention.Default;
        if (head.StartsWith("delegate*unmanaged"))
        {
            string mods = head["delegate*unmanaged".Length..].Trim('[', ']');
            conv = mods switch
            {
                "Cdecl" => SignatureCallingConvention.CDecl,
                "Stdcall" => SignatureCallingConvention.StdCall,
                "Thiscall" => SignatureCallingConvention.ThisCall,
                "Fastcall" => SignatureCallingConvention.FastCall,
                _ => SignatureCallingConvention.Unmanaged,
            };
            if (mods == "SuppressGCTransition") ret = ret.WithModifier(Module.Import(typeof(CallConvSuppressGCTransition)), required: false);
            else if (mods.Length > 0 && conv == SignatureCallingConvention.Unmanaged) throw new NotSupportedException("FFI: calling convention " + mods);
        }
        return new MethodSig(ret, parts[..^1], callingConvention: conv);
    }

    static List<string> SplitTop(string s)
    {
        var r = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '<') depth++;
            else if (s[i] == '>') depth--;
            else if (s[i] == ',' && depth == 0) { r.Add(s[start..i]); start = i + 1; }
        }
        r.Add(s[start..]);
        return r;
    }

    static bool BlittableCs(string cs) => cs is "bool" or "byte" or "sbyte" or "short" or "ushort" or "int" or "uint" or "long" or "ulong" or "float" or "double" or "nint" or "nuint" or "char";

    // ------------------------------------------------------------------ (a) lib / fun

    enum PassKind { Same, Bool, Fn, Utf8, Array, ByRef }

    sealed record Pass(ParamSym P, PassKind Kind, IlType Public, IlType Raw, IlType? Elem);

    /// <summary>How one parameter crosses (the IL twin of Emitter.FfiParam/FfiByRef). Types without a blittable
    /// conversion (blittable structs, delegates) cross as themselves: the runtime marshaller, as in the C# backend.</summary>
    Pass Param(ParamSym p)
    {
        string cs = p.Type.Cs;
        if (p.Mod != "")
        {
            var t = Map(cs);
            return new(p, PassKind.ByRef, t.ByRef(), t.Pointer(), t);
        }
        if (cs == "bool") return new(p, PassKind.Bool, IlType.Bool, IlType.Byte, null);
        if (cs.StartsWith("delegate* unmanaged")) return new(p, PassKind.Fn, NativeFnType, Map(cs), null);
        if (cs is "string" or "string?") return new(p, PassKind.Utf8, IlType.String, IlType.Byte.Pointer(), null);
        string arr = cs.EndsWith("[]?") ? cs[..^3] : cs.EndsWith("[]") ? cs[..^2] : "";
        if (arr.Length > 0 && BlittableCs(arr) && arr != "bool")
        {
            var e = Map(arr);
            return new(p, PassKind.Array, e.SzArray(), e.Pointer(), e);
        }
        var same = Map(cs);
        return new(p, PassKind.Same, same, same, null);
    }

    enum RetKind { Same, Bool, Utf8, Utf8NonNull }

    (RetKind Kind, IlType Public, IlType Raw) Return(MethodSym m)
    {
        string cs = m.Ret.IsVoid ? "void" : m.Ret.Cs;
        return cs switch
        {
            "bool" => (RetKind.Bool, IlType.Bool, IlType.Byte),
            "string?" => (RetKind.Utf8, IlType.String, IlType.Byte.Pointer()),
            "string" => (RetKind.Utf8NonNull, IlType.String, IlType.Byte.Pointer()),
            _ => (RetKind.Same, Map(cs), Map(cs)),
        };
    }

    /// <summary>A `lib` block as a static class (public, abstract sealed) with one import (+ wrapper) per `fun`.</summary>
    public IlTypeDef EmitLib(LibDecl l, string ns = "")
    {
        var t = Module.DefineType(ns, l.Name, StaticClass);
        EmitFuns(l, t);
        return t;
    }

    /// <summary>The `fun`s of <paramref name="l"/> into an existing type.</summary>
    public void EmitFuns(LibDecl l, IlTypeDef t)
    {
        var s = (TypeSym)l.Sym!;
        // C may keep a callback and run it from any fun of the lib, so every fun of such a lib rethrows (review F5)
        bool libCallbacks = l.Funs.Any(f => s.Methods[f.Name][0].Params.Any(p => Param(p).Kind == PassKind.Fn));
        foreach (var f in l.Funs) EmitFun(t, l.LibName, f, s.Methods[f.Name][0], libCallbacks);
    }

    /// <param name="libCallbacks">the lib declares a callback parameter on some fun: bracket this C call with
    /// Enter/Rethrow even if this fun takes no callback.</param>
    public IlMethodDef EmitFun(IlTypeDef t, string libName, FunDecl f, MethodSym m, bool libCallbacks = false)
    {
        var parts = m.Params.Select(Param).ToList();
        var (rk, retPublic, retRaw) = Return(m);
        bool callbacks = libCallbacks || parts.Any(x => x.Kind == PassKind.Fn);
        bool direct = !callbacks && parts.All(x => x.Kind == PassKind.Same) && rk == RetKind.Same;
        string name = Clr(m.CsName);

        var import = t.DefineMethod(direct ? name : "__raw_" + name, (direct ? PublicStatic : PrivateStatic) | MethodAttributes.PinvokeImpl,
            retRaw, [.. parts.Select(x => x.Raw)], implAttributes: MethodImplAttributes.PreserveSig);
        import.PInvoke = new IlPInvoke(libName, f.Entry, ImportFlags);
        if (f.Attrs.Contains("Fast")) import.CustomAttributes.Add(new IlCustomAttribute(AttrCtor(typeof(SuppressGCTransitionAttribute)), []));
        for (int i = 0; i < parts.Count; i++) import.DefineParameter(i + 1, Clr(parts[i].P.CsName));
        Imports[m] = import;
        if (direct) return Funs[m] = import;

        var w = t.DefineMethod(name, PublicStatic, retPublic, [.. parts.Select(x => x.Public)], implAttributes: MethodImplAttributes.AggressiveInlining);
        for (int i = 0; i < parts.Count; i++)
            w.DefineParameter(i + 1, Clr(parts[i].P.CsName), parts[i].P.Mod == "out" ? ParameterAttributes.Out : ParameterAttributes.None);
        var il = w.Il;
        il.InitLocals = false;   // the C# backend's [module: SkipLocalsInit]: the stack buffer is not zeroed per call

        // 1. stack buffers first: `localloc` must not sit inside a protected region's handler, and C# allocates them anyway
        var spans = new Dictionary<int, IlLocal>();
        var spanType = Module.Import(typeof(Span<byte>));
        for (int i = 0; i < parts.Count; i++)
        {
            if (parts[i].Kind != PassKind.Utf8) continue;
            il.EmitLoadConst(Alice.Runtime.Utf8Arg.StackBytes);
            il.Emit(ILOpCode.Conv_u);
            il.Emit(ILOpCode.Localloc);
            il.EmitLoadConst(Alice.Runtime.Utf8Arg.StackBytes);
            il.EmitNewObj(SpanCtor);
            il.EmitStoreLocal(spans[i] = il.DeclareLocal(spanType, "__s_" + Clr(parts[i].P.CsName)));
        }
        // 2. `using var __u_x = new Utf8Arg(x, buffer)`: one try/finally per string, nested in parameter order
        var utf8 = new Dictionary<int, IlLocal>();
        var u8Type = Module.Import(typeof(Alice.Runtime.Utf8Arg));
        foreach (var (i, span) in spans)
        {
            var u = utf8[i] = il.DeclareLocal(u8Type, "__u_" + Clr(parts[i].P.CsName));
            il.EmitLoadLocalAddress(u);
            il.EmitLoadArg(i);
            il.EmitLoadLocal(span);
            il.EmitCall(Utf8Ctor);
            il.BeginTry();
        }
        // 3. `fixed`: arrays (null or empty -> NULL, as C# does) and out/ref byrefs
        var ptrs = new Dictionary<int, IlLocal>();
        var pins = new List<IlLocal>();
        for (int i = 0; i < parts.Count; i++)
        {
            var x = parts[i];
            if (x.Kind is not (PassKind.Array or PassKind.ByRef)) continue;
            var pin = il.DeclareLocal(x.Elem!.ByRef(), "__pin_" + Clr(x.P.CsName), pinned: true);
            var p = ptrs[i] = il.DeclareLocal(x.Raw, "__p_" + Clr(x.P.CsName));
            pins.Add(pin);
            if (x.Kind == PassKind.ByRef)
            {
                if (x.P.Mod == "out") { il.EmitLoadArg(i); il.EmitInitObj(x.Elem); }   // `x = default` before its address is taken
                il.EmitLoadArg(i);
                il.EmitStoreLocal(pin);
                il.EmitLoadLocal(pin);
                il.Emit(ILOpCode.Conv_u);
                il.EmitStoreLocal(p);
                continue;
            }
            EmitFixedArray(il, i, x.Elem, pin, p);
        }
        // 4. the call, `var __d = NativeCallback.Enter()` first when a callback may run during it
        IlLocal? depth = null;
        if (callbacks)
        {
            il.EmitCall(Enter);
            il.EmitStoreLocal(depth = il.DeclareLocal(IlType.Int32, "__d"));
        }
        for (int i = 0; i < parts.Count; i++)
        {
            var x = parts[i];
            switch (x.Kind)
            {
                case PassKind.Same: il.EmitLoadArg(i); break;
                case PassKind.Bool: il.EmitLoadArg(i); il.EmitLoadConst(0); il.Emit(ILOpCode.Cgt_un); break;   // b ? 1 : 0
                case PassKind.Fn: il.EmitLoadArgAddress(i); il.EmitCall(NativeFnAddress); break;
                case PassKind.Utf8: il.EmitLoadLocalAddress(utf8[i]); il.EmitCall(Utf8Pointer); break;
                default: il.EmitLoadLocal(ptrs[i]); break;
            }
        }
        il.EmitCall(import);
        switch (rk)
        {
            case RetKind.Bool: il.EmitLoadConst(0); il.Emit(ILOpCode.Cgt_un); break;   // != 0 (C bool)
            case RetKind.Utf8: il.EmitCall(Utf8Decode); break;
            case RetKind.Utf8NonNull: il.EmitLoadConst(f.Name); il.EmitCall(Utf8DecodeNonNull); break;
        }
        bool returns = retPublic is not IlType.PrimType { Code: PrimitiveTypeCode.Void };
        IlLocal? result = null;
        if (returns && (callbacks || spans.Count > 0 || pins.Count > 0)) il.EmitStoreLocal(result = il.DeclareLocal(retPublic, "__r"));
        foreach (var pin in pins) { il.EmitLoadConst(0); il.Emit(ILOpCode.Conv_u); il.EmitStoreLocal(pin); }   // end of `fixed`
        if (callbacks)   // an exception raised in an Alice callback resurfaces here, after the C frames are gone
        {
            il.EmitLoadLocal(depth!);
            il.EmitCall(Rethrow);
        }
        // 5. close the `using`s innermost first
        foreach (var i in utf8.Keys.Reverse())
        {
            il.BeginFinally();
            il.EmitLoadLocalAddress(utf8[i]);
            il.EmitCall(Utf8Dispose);
            il.EndTry();
        }
        if (result != null) il.EmitLoadLocal(result);
        il.EmitReturn();
        return Funs[m] = w;
    }

    // ------------------------------------------------------------------ (b) callbacks

    /// <summary>
    /// A lambda passed to a `Callback(...)` parameter: <paramref name="body"/> is its lifted static method (emitted by
    /// the caller). Returns the `[UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]` trampoline
    /// `__cb_N(args) { try { return body(args); } catch (Exception e) { NativeCallback.Fail(e); return default; } }`.
    /// <paramref name="conv"/> is the parameter's declared convention (<see cref="FnPtrSig"/>): CDecl, StdCall,
    /// ThisCall and FastCall name their CallConv type; Unmanaged/Default leave CallConvs out (the platform default).
    /// </summary>
    public IlMethodDef EmitCallback(IlTypeDef owner, IlMethodDef body, string? name = null, SignatureCallingConvention conv = SignatureCallingConvention.CDecl)
    {
        var sig = body.Signature;
        var cb = owner.DefineMethod(name ?? $"__cb_{++callbackN}", PrivateStatic, sig.Return, [.. sig.Parameters]);
        Type? callConv = conv switch
        {
            SignatureCallingConvention.CDecl => typeof(CallConvCdecl),
            SignatureCallingConvention.StdCall => typeof(CallConvStdcall),
            SignatureCallingConvention.ThisCall => typeof(CallConvThiscall),
            SignatureCallingConvention.FastCall => typeof(CallConvFastcall),
            SignatureCallingConvention.Unmanaged or SignatureCallingConvention.Default => null,
            _ => throw new NotSupportedException($"FFI: callback calling convention {conv}"),
        };
        cb.CustomAttributes.Add(new IlCustomAttribute(AttrCtor(typeof(UnmanagedCallersOnlyAttribute)), [],
            callConv == null ? [] : [new IlNamedArgument(nameof(UnmanagedCallersOnlyAttribute.CallConvs), Module.Import(typeof(Type)).SzArray(),
                new[] { callConv.FullName }, IsField: true)]));
        var il = cb.Il;
        bool returns = sig.ReturnsValue;
        var r = returns ? il.DeclareLocal(sig.Return, "__r") : null;
        il.BeginTry();
        for (int i = 0; i < sig.Parameters.Length; i++) il.EmitLoadArg(i);
        il.EmitCall(body);
        if (r != null) il.EmitStoreLocal(r);
        il.BeginCatch(Module.Import(typeof(Exception)));
        il.EmitCall(Fail);
        if (r != null) { il.EmitLoadLocalAddress(r); il.EmitInitObj(sig.Return); }   // `return default` to C
        il.EndTry();
        if (r != null) il.EmitLoadLocal(r);
        il.EmitReturn();
        return cb;
    }

    /// <summary>`new NativeFn((nint)(delegate* unmanaged[Cdecl]&lt;...&gt;)&amp;cb)`: the safe value Alice code passes.</summary>
    public void EmitLoadCallback(IlBuilder il, IlMethodDef callback)
    {
        il.EmitLoadFunction(callback);
        il.EmitNewObj(NativeFnCtor);
    }

    // ------------------------------------------------------------------ (c) @[Native]

    /// <summary>Name of the managed fallback the caller emits for an `@[Native]` method (same signature).</summary>
    public static string NativeManagedName(MethodSym m) => "__managed_" + Clr(m.CsName);

    /// <summary>
    /// `@[Native]` <paramref name="m"/>: registers its C version with <paramref name="c"/> and emits
    /// `name(args) => __np != null &amp;&amp; arrays distinct ? calli __np(pinned args, lengths) : __managed_name(args)`.
    /// The pointer lives in a nested holder class `__np_name` whose `.cctor` calls NativeLibs.Export (lazy, once).
    /// </summary>
    public IlMethodDef EmitNative(IlTypeDef owner, MethodDecl d, MethodSym m, IlMethodDef managed, CEmitter c, string file, string nativeLib)
    {
        c.Add(d, m, file);
        var cps = c.Params(m, d);
        var sig = managed.Signature;
        var native = new List<IlType>();
        for (int i = 0; i < cps.Count; i++)
        {
            if (cps[i].Array) { native.Add(((IlType.SzArrayType)sig.Parameters[i]).Element.Pointer()); native.Add(IlType.Int32); }
            else native.Add(sig.Parameters[i]);
        }
        var ret = sig.Return;
        if (CEmitter.SkipsGcTransition(d)) ret = ret.WithModifier(Module.Import(typeof(CallConvSuppressGCTransition)), required: false);
        var fpSig = new MethodSig(ret, native, callingConvention: SignatureCallingConvention.Unmanaged);
        string name = Clr(m.CsName);

        var holder = owner.DefineNestedType("__np_" + name, TypeAttributes.NestedPrivate | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class | TypeAttributes.BeforeFieldInit);
        var fp = holder.DefineField("Ptr", IlType.FnPtr(fpSig), FieldAttributes.Assembly | FieldAttributes.Static | FieldAttributes.InitOnly);
        var cctor = holder.DefineConstructor(MethodAttributes.Private | MethodAttributes.HideBySig, isStatic: true).Il;
        cctor.EmitLoadConst(nativeLib);
        cctor.EmitLoadConst(CEmitter.Symbol(m));
        cctor.EmitCall(Export);
        cctor.EmitStoreField(fp);
        cctor.EmitReturn();

        var w = owner.DefineMethod(name, PublicStatic, sig.Return, [.. sig.Parameters]);
        for (int i = 0; i < m.Params.Count; i++) w.DefineParameter(i + 1, Clr(m.Params[i].CsName));
        var il = w.Il;
        var fallback = il.DefineLabel();
        il.EmitLoadField(fp);
        il.EmitBranch(fallback, IlCond.False);
        var arrays = Enumerable.Range(0, cps.Count).Where(i => cps[i].Array).ToList();
        for (int a = 0; a < arrays.Count; a++)
            for (int b = a + 1; b < arrays.Count; b++)   // aliased arrays: C sees `restrict`-style copies, so take the managed path
            {
                il.EmitLoadArg(arrays[a]);
                il.EmitLoadArg(arrays[b]);
                il.EmitBranch(fallback, IlCond.Eq);
            }
        var ptrs = new Dictionary<int, IlLocal>();
        foreach (var i in arrays)
        {
            var elem = ((IlType.SzArrayType)sig.Parameters[i]).Element;
            var pin = il.DeclareLocal(elem.ByRef(), "__pin_" + cps[i].Name, pinned: true);
            var p = ptrs[i] = il.DeclareLocal(elem.Pointer(), "__p_" + cps[i].Name);
            EmitFixedArray(il, i, elem, pin, p);
        }
        for (int i = 0; i < cps.Count; i++)
        {
            if (!cps[i].Array) { il.EmitLoadArg(i); continue; }
            il.EmitLoadLocal(ptrs[i]);
            il.EmitLoadArg(i);   // `a.Length` (null raises, as in C#)
            il.Emit(ILOpCode.Ldlen);
            il.Emit(ILOpCode.Conv_i4);
        }
        il.EmitLoadField(fp);
        il.EmitCallIndirect(fpSig);
        il.EmitReturn();
        il.MarkLabel(fallback);
        for (int i = 0; i < sig.Parameters.Length; i++) il.EmitLoadArg(i);
        il.EmitCall(managed);
        il.EmitReturn();
        return w;
    }

    /// <summary>`fixed (T* p = arg)`: NULL for a null or empty array, else the pinned first element.</summary>
    static void EmitFixedArray(IlBuilder il, int arg, IlType elem, IlLocal pin, IlLocal p)
    {
        var none = il.DefineLabel();
        var done = il.DefineLabel();
        il.EmitLoadArg(arg);
        il.EmitBranch(none, IlCond.False);
        il.EmitLoadArg(arg);
        il.Emit(ILOpCode.Ldlen);
        il.Emit(ILOpCode.Conv_i4);
        il.EmitBranch(none, IlCond.False);
        il.EmitLoadArg(arg);
        il.EmitLoadConst(0);
        il.Emit(ILOpCode.Ldelema, elem);
        il.EmitStoreLocal(pin);
        il.EmitLoadLocal(pin);
        il.Emit(ILOpCode.Conv_u);
        il.EmitStoreLocal(p);
        il.EmitBranch(done);
        il.MarkLabel(none);
        il.EmitLoadConst(0);
        il.Emit(ILOpCode.Conv_u);
        il.EmitStoreLocal(p);
        il.MarkLabel(done);
    }

    // ------------------------------------------------------------------ (d) unsafe pointer operations

    /// <summary>`sizeof(T)`: a constant for primitives and pointers' fixed sizes, the `sizeof` opcode otherwise.</summary>
    public static void EmitSizeOf(IlBuilder il, IlType t)
    {
        int? n = t switch
        {
            IlType.PrimType { Code: PrimitiveTypeCode.Boolean or PrimitiveTypeCode.Byte or PrimitiveTypeCode.SByte } => 1,
            IlType.PrimType { Code: PrimitiveTypeCode.Char or PrimitiveTypeCode.Int16 or PrimitiveTypeCode.UInt16 } => 2,
            IlType.PrimType { Code: PrimitiveTypeCode.Int32 or PrimitiveTypeCode.UInt32 or PrimitiveTypeCode.Single } => 4,
            IlType.PrimType { Code: PrimitiveTypeCode.Int64 or PrimitiveTypeCode.UInt64 or PrimitiveTypeCode.Double } => 8,
            _ => null,
        };
        if (n is { } k) il.EmitLoadConst(k);
        else il.Emit(ILOpCode.Sizeof, t);
    }

    /// <summary>`*p` / `p[0]`: [ptr] -> [value].</summary>
    public static void EmitLoadIndirect(IlBuilder il, IlType elem)
    {
        if (IndirectOp(elem, load: true) is { } op) il.Emit(op);
        else il.Emit(ILOpCode.Ldobj, elem);
    }

    /// <summary>`*p = v`: [ptr, value] -> [].</summary>
    public static void EmitStoreIndirect(IlBuilder il, IlType elem)
    {
        if (IndirectOp(elem, load: false) is { } op) il.Emit(op);
        else il.Emit(ILOpCode.Stobj, elem);
    }

    static ILOpCode? IndirectOp(IlType t, bool load) => t switch
    {
        IlType.PrimType p => p.Code switch
        {
            PrimitiveTypeCode.Boolean or PrimitiveTypeCode.Byte => load ? ILOpCode.Ldind_u1 : ILOpCode.Stind_i1,
            PrimitiveTypeCode.SByte => load ? ILOpCode.Ldind_i1 : ILOpCode.Stind_i1,
            PrimitiveTypeCode.Char or PrimitiveTypeCode.UInt16 => load ? ILOpCode.Ldind_u2 : ILOpCode.Stind_i2,
            PrimitiveTypeCode.Int16 => load ? ILOpCode.Ldind_i2 : ILOpCode.Stind_i2,
            PrimitiveTypeCode.Int32 => load ? ILOpCode.Ldind_i4 : ILOpCode.Stind_i4,
            PrimitiveTypeCode.UInt32 => load ? ILOpCode.Ldind_u4 : ILOpCode.Stind_i4,
            PrimitiveTypeCode.Int64 or PrimitiveTypeCode.UInt64 => load ? ILOpCode.Ldind_i8 : ILOpCode.Stind_i8,
            PrimitiveTypeCode.Single => load ? ILOpCode.Ldind_r4 : ILOpCode.Stind_r4,
            PrimitiveTypeCode.Double => load ? ILOpCode.Ldind_r8 : ILOpCode.Stind_r8,
            PrimitiveTypeCode.IntPtr or PrimitiveTypeCode.UIntPtr => load ? ILOpCode.Ldind_i : ILOpCode.Stind_i,
            PrimitiveTypeCode.String or PrimitiveTypeCode.Object => load ? ILOpCode.Ldind_ref : ILOpCode.Stind_ref,
            _ => null,
        },
        IlType.PointerType or IlType.FnPtrType => load ? ILOpCode.Ldind_i : ILOpCode.Stind_i,
        _ => null,
    };

    /// <summary>`p + i` / `&amp;p[i]`: [ptr, index] -> [ptr + index * sizeof(T)] (C# scaling: index widened to native int,
    /// unsigned indexes zero-extended).</summary>
    public static void EmitElementAddress(IlBuilder il, IlType elem, bool unsignedIndex = false)
    {
        il.Emit(unsignedIndex ? ILOpCode.Conv_u : ILOpCode.Conv_i);
        if (elem is not IlType.PrimType { Code: PrimitiveTypeCode.Boolean or PrimitiveTypeCode.Byte or PrimitiveTypeCode.SByte })
        {
            EmitSizeOf(il, elem);
            il.Emit(ILOpCode.Mul);
        }
        il.Emit(ILOpCode.Add);
    }

    /// <summary>`p - q` in elements (C#: a `long`): [ptr, ptr] -> [int64].</summary>
    public static void EmitPointerDifference(IlBuilder il, IlType elem)
    {
        il.Emit(ILOpCode.Sub);
        EmitSizeOf(il, elem);
        il.Emit(ILOpCode.Div);
        il.Emit(ILOpCode.Conv_i8);
    }
}
