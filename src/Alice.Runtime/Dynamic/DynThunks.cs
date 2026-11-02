using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Alice.Runtime;

/// <summary>A resolved dynamic operation, cached in a <see cref="DynSite"/> for one set of runtime types.</summary>
internal abstract class DynThunk
{
    public abstract object? Invoke(object? target, ReadOnlySpan<object?> args);
}

/// <summary>How the arguments of a site fill a method's parameters.</summary>
internal enum CallForm : byte { Normal, Optional, Expanded }

/// <summary>An applicable method, with the conversions its arguments need (indexed by argument; null: none).</summary>
internal readonly record struct DynMatch(MethodInfo Method, CallForm Form, Type?[]? Conv);

/// <summary>
/// A method, property accessor, indexer accessor, operator or delegate `Invoke`, called through a cached
/// <see cref="MethodInvoker"/>. The conversions are fixed when the entry is created (the entry is keyed by the
/// argument types), so the common case (exact parameter types, up to four arguments) calls the invoker's
/// fixed-arity overload straight from the argument span: no array, no allocation.
/// </summary>
internal sealed class MethodThunk : DynThunk
{
    readonly MethodInvoker inv;
    readonly int n;               // parameter count
    readonly bool arg0;           // static method taking the receiver as its first argument (operators, conversions)
    readonly bool direct;         // arguments are the parameters as they are
    readonly CallForm form;
    readonly Type?[]? conv;       // per argument (receiver first when arg0)
    readonly object?[]? defaults; // per parameter, for the Optional form
    readonly Type? packArray;     // the params array type, for the Expanded form

    MethodThunk(DynMatch m, bool receiverIsArg0)
    {
        var ps = m.Method.GetParameters();
        inv = MethodInvoker.Create(m.Method);
        n = ps.Length;
        arg0 = receiverIsArg0;
        form = m.Form;
        conv = m.Conv;
        direct = !arg0 && form == CallForm.Normal && conv == null && n <= 4;
        if (form == CallForm.Optional)
        {
            defaults = new object?[n];
            for (int i = 0; i < n; i++) defaults[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : DefaultOf(ps[i].ParameterType);
        }
        if (form == CallForm.Expanded) packArray = ps[^1].ParameterType;
    }

    public static MethodThunk For(MethodInfo m, bool receiverIsArg0 = false) => new(new DynMatch(m, CallForm.Normal, null), receiverIsArg0);

    public static MethodThunk For(DynMatch m, bool receiverIsArg0 = false) => new(m, receiverIsArg0);

    [UnconditionalSuppressMessage("Trimming", "IL2067", Justification = "Default of a reflected parameter type; the parameter's type is kept with its method.")]
    static object? DefaultOf(Type t) => t.IsValueType && Nullable.GetUnderlyingType(t) == null ? RuntimeHelpers.GetUninitializedObject(t) : null;

    public override object? Invoke(object? target, ReadOnlySpan<object?> args)
    {
        try
        {
            if (direct)
                return args.Length switch
                {
                    0 => inv.Invoke(target),
                    1 => inv.Invoke(target, args[0]),
                    2 => inv.Invoke(target, args[0], args[1]),
                    3 => inv.Invoke(target, args[0], args[1], args[2]),
                    _ => inv.Invoke(target, args[0], args[1], args[2], args[3]),
                };
            return Slow(target, args);
        }
        catch (TargetInvocationException e)
        {
            throw Dyn.Unwrap(e);
        }
    }

    object? Slow(object? target, ReadOnlySpan<object?> args)
    {
        object? self = arg0 ? null : target;
        int total = args.Length + (arg0 ? 1 : 0);
        if (n <= 4 && form != CallForm.Expanded)
        {
            Args4 buf = default;
            for (int k = 0; k < total; k++) buf[k] = Arg(k, target, args);
            for (int k = total; k < n; k++) buf[k] = defaults![k];
            return n switch
            {
                0 => inv.Invoke(self),
                1 => inv.Invoke(self, buf[0]),
                2 => inv.Invoke(self, buf[0], buf[1]),
                3 => inv.Invoke(self, buf[0], buf[1], buf[2]),
                _ => inv.Invoke(self, buf[0], buf[1], buf[2], buf[3]),
            };
        }
        var full = new object?[n];
        if (form == CallForm.Expanded)
        {
            int fixedCount = n - 1;
            for (int k = 0; k < fixedCount; k++) full[k] = Arg(k, target, args);
            var pack = Array.CreateInstanceFromArrayType(packArray!, total - fixedCount);
            for (int k = fixedCount; k < total; k++) pack.SetValue(Arg(k, target, args), k - fixedCount);
            full[^1] = pack;
        }
        else
        {
            for (int k = 0; k < total; k++) full[k] = Arg(k, target, args);
            for (int k = total; k < n; k++) full[k] = defaults![k];
        }
        return inv.Invoke(self, full.AsSpan());
    }

    object? Arg(int k, object? target, ReadOnlySpan<object?> args)
    {
        object? v = arg0 ? (k == 0 ? target : args[k - 1]) : args[k];
        return conv != null && conv[k] is { } to ? Numbers.Coerce(v, to) : v;
    }

    [InlineArray(4)] struct Args4 { object? _e; }
}

internal sealed class FieldGetThunk(FieldInfo f) : DynThunk
{
    public override object? Invoke(object? target, ReadOnlySpan<object?> args) => f.GetValue(target);
}

internal sealed class FieldSetThunk(FieldInfo f) : DynThunk
{
    public override object? Invoke(object? target, ReadOnlySpan<object?> args)
    {
        f.SetValue(target, Numbers.Coerce(args[0], f.FieldType));
        return null;
    }
}

/// <summary>`a[i]` / `a[i] = v` on a .NET array (any rank; Int64 indices allowed).</summary>
internal sealed class ArrayThunk(Type elem, bool set) : DynThunk
{
    public override object? Invoke(object? target, ReadOnlySpan<object?> args)
    {
        var a = (Array)target!;
        int n = set ? args.Length - 1 : args.Length;
        if (n == 1)
        {
            int i = Index(args[0]);
            if (set) { a.SetValue(Numbers.Coerce(args[1], elem), i); return null; }
            return a is object?[] refs ? refs[i] : a.GetValue(i);
        }
        var ix = new int[n];
        for (int k = 0; k < n; k++) ix[k] = Index(args[k]);
        if (set) { a.SetValue(Numbers.Coerce(args[n], elem), ix); return null; }
        return a.GetValue(ix);
    }

    static int Index(object? i) => i switch
    {
        int v => v,
        long l => checked((int)l),
        null => throw new ArgumentNullException("index", "array index is nil"),
        _ => Numbers.TryConvert(i, typeof(int), false, out var r) ? (int)r! : throw new ArgumentException($"array index must be an integer, got {Dyn.Alice(i.GetType())}"),
    };
}

/// <summary>A runtime `define_method` body (reached like `send` reaches it).</summary>
internal sealed class DefinedThunk(Delegate d) : DynThunk
{
    public override object? Invoke(object? target, ReadOnlySpan<object?> args)
    {
        try { return d.DynamicInvoke(args.ToArray()); }
        catch (TargetInvocationException e) { throw Dyn.Unwrap(e); }
    }
}

/// <summary>Ruby's `method_missing(name, args)` for a message the receiver does not define.</summary>
internal sealed class MissingThunk(MethodInfo mm, string name) : DynThunk
{
    public override object? Invoke(object? target, ReadOnlySpan<object?> args)
    {
        try { return mm.Invoke(target, [name, args.ToArray()]); }
        catch (TargetInvocationException e) { throw Dyn.Unwrap(e); }
    }
}

/// <summary>`==` / `!=` without a user operator: reference equality for classes, Equals for structs (C# `dynamic`
/// compares two objects by reference too).</summary>
internal sealed class EqualityThunk(bool negate) : DynThunk
{
    public override object? Invoke(object? target, ReadOnlySpan<object?> args)
    {
        object? r = args[0];
        bool eq = target == null || r == null || !target.GetType().IsValueType ? ReferenceEquals(target, r) : target.Equals(r);
        return eq != negate;
    }
}

/// <summary>`String + x` / `x + String`: concatenation, nil as "" (C# string `+`).</summary>
internal sealed class ConcatThunk : DynThunk
{
    public static readonly ConcatThunk Instance = new();

    public override object? Invoke(object? target, ReadOnlySpan<object?> args) => string.Concat(target, args[0]);
}
