using System.Reflection;
using Alice.Compiler.Syntax;

namespace Alice.Compiler.Semantics;

/// <summary>
/// Process isolation (ADR 0024), BEAM-style but checked at compile time. Two processes never share a
/// mutable object:
/// - AL0230: an actor's message type must be sendable: a value that cannot be mutated through an alias
///   (numbers, Bool, Char, String, enums, structs and records of sendable fields, tuples of them,
///   System.Collections.Immutable types) or a thread-safe handle (another actor, a Channel, Atomic...).
/// - AL0231: an actor's `init` and handler blocks may not capture locals of non-sendable type. The state of
///   `Actor(S, T)` is built inside the actor by `init`, so it may be anything: it is owned by the process.
/// Writes to captured state stay AL0202 (actor blocks are concurrent code).
/// </summary>
public sealed partial class Binder
{
    readonly HashSet<LambdaExpr> isolatedLambdas = new();
    readonly List<int> isolation = new();   // spawnDepth inside each enclosing actor block

    static bool IsActorType(AType t) =>
        t.Clr is Type c && typeof(Alice.Runtime.ProcessCore).IsAssignableFrom(c)
        || t.OpenClr is Type o && typeof(Alice.Runtime.ProcessCore).IsAssignableFrom(o);

    /// <summary>Actors, supervisors and exit reasons are safe to share between threads.</summary>
    static bool ActorThreadSafe(AType t) => IsActorType(t) || t.Clr == typeof(Alice.Runtime.ExitReason);

    /// <summary>`p.on_exit { ... }` runs on the exiting process's thread.</summary>
    static bool IsActorConcurrentCall(CallExpr c, AType? recv) => c.Name == "on_exit" && recv != null && IsActorType(recv);

    void CheckActorNew(NewExpr ne, AType t)
    {
        if (!IsActorType(t) || t.Args.Count == 0) return;
        var msg = t.Args[^1];
        if (!Sendable(msg, new HashSet<object>(), out string why))
            throw Err(ne, $"message type `{msg.Cs}` is not sendable between actors: {why}; send numbers, String, enums, records or structs of sendable fields, tuples, System.Collections.Immutable types or actor handles");
        foreach (var a in ne.Args)
            if (a.Value is LambdaExpr lam) isolatedLambdas.Add(lam);
    }

    void EnterIsolation(LambdaExpr lam, bool conc)
    {
        if (conc && isolatedLambdas.Contains(lam)) isolation.Add(spawnDepth);
        else isolation.Add(-1);
    }

    void LeaveIsolation() => isolation.RemoveAt(isolation.Count - 1);

    /// <summary>Reading a local from inside an actor block: it must not alias mutable state outside it.</summary>
    void CheckIsolatedRead(NameExpr n, LocalSym l)
    {
        for (int i = isolation.Count - 1; i >= 0; i--)
        {
            int depth = isolation[i];
            if (depth < 0 || l.SpawnDepth >= depth) continue;
            if (l.Type.Kind is "unknown" or "dynamic") return;
            if (ActorThreadSafe(l.Type) || ThreadSafe(l.Type) || Sendable(l.Type, new HashSet<object>(), out _)) return;
            throw Err(n, $"actor is not isolated: it captures `{l.Name}` ({l.Type.Cs}), mutable state shared with the code outside it; build it inside `init` (Actor(S, T)), send it as a message of a sendable type, or share it through Mutex(T), Atomic or another actor");
        }
    }

    static readonly HashSet<Type> SendableClr =
    [
        typeof(string), typeof(decimal), typeof(DateTime), typeof(DateTimeOffset), typeof(TimeSpan), typeof(Guid),
        typeof(System.Numerics.BigInteger), typeof(Half), typeof(Int128), typeof(UInt128), typeof(Alice.Runtime.ExitReason),
    ];

    bool Sendable(AType t, HashSet<object> seen, out string why)
    {
        why = "";
        if (t.Kind is "unknown" or "dynamic" or "param" or "null") return true;
        if (t.Kind == "func") { why = "a block can capture mutable state"; return false; }
        if (t.Kind == "array") { why = "arrays are mutable"; return false; }
        if (ActorThreadSafe(t) || ThreadSafe(t)) return true;
        if (t.Kind == "tuple" || IsImmutableCollection(t.Clr ?? t.OpenClr))
        {
            foreach (var a in t.Args) if (!Sendable(a, seen, out why)) return false;
            return true;
        }
        if (t.Sym is TypeSym s) return SymSendable(s, seen, out why);
        if (t.Clr is Type c) return ClrSendable(c, seen, out why);
        why = $"`{t.Cs}` may be mutable";
        return false;
    }

    static bool IsImmutableCollection(Type? c) => c?.Namespace == "System.Collections.Immutable";

    bool SymSendable(TypeSym s, HashSet<object> seen, out string why)
    {
        why = "";
        if (!seen.Add(s)) return true;
        if (s.Kind == "enum")
        {
            foreach (var v in s.Variants) if (!SymSendable(v, seen, out why)) return false;
            return true;
        }
        if (s.Kind is not ("struct" or "class" or "record"))
        {
            why = $"`{s.Name}` is a{(s.Kind == "interface" ? "n" : "")} {s.Kind}";
            return false;
        }
        // a class is sendable when nothing can change it after construction (records, getter-only classes)
        for (var b = s; s.Kind is "class" or "record" && b != null; b = b.BaseClass)
        {
            if (b.Methods.Values.SelectMany(m => m).Any(m => m.Mutates && !m.IsCtor))
            {
                why = $"`{b.Name}` has methods that assign its fields";
                return false;
            }
            if (b.Fields.Values.FirstOrDefault(f => !f.IsStatic && f.Kind is "setter" or "property") is { } w)
            {
                why = $"`{b.Name}` has a writable property `{w.Name}`";
                return false;
            }
        }
        return FieldsSendable(s, seen, out why);
    }

    bool FieldsSendable(TypeSym s, HashSet<object> seen, out string why)
    {
        why = "";
        for (var b = s; b != null; b = b.BaseClass)
            foreach (var f in b.Fields.Values)
            {
                if (f.IsStatic || f.Kind is "const" or "enum-member") continue;
                if (!Sendable(f.Type, seen, out why))
                {
                    why = $"field `{f.Name}` of `{s.Name}`: {why}";
                    return false;
                }
            }
        return true;
    }

    static bool ClrSendable(Type c, HashSet<object> seen, out string why)
    {
        why = "";
        if (c.IsPrimitive || c.IsEnum || SendableClr.Contains(c) || c.IsPointer) return true;
        if (c.IsArray) { why = "arrays are mutable"; return false; }
        if (!seen.Add(c)) return true;
        if (IsImmutableCollection(c))
        {
            foreach (var a in c.GetGenericArguments()) if (!ClrSendable(a, seen, out why)) return false;
            return true;
        }
        if (c.IsValueType)
        {
            // a struct is copied on send; it is sendable when nothing it holds is shared mutable state
            foreach (var f in c.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (!ClrSendable(f.FieldType, seen, out why)) return false;
            return true;
        }
        if (IsClrRecordLike(c))
        {
            foreach (var f in c.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (!ClrSendable(f.FieldType, seen, out why)) return false;
            return true;
        }
        why = $"`{c.Name}` is a mutable .NET class";
        return false;
    }

    /// <summary>A .NET class whose instance fields are all readonly and whose properties have no public setter (init-only is fine).</summary>
    static bool IsClrRecordLike(Type c)
    {
        if (c.IsInterface || c.IsAbstract || c == typeof(object)) return false;
        for (var b = c; b != null && b != typeof(object); b = b.BaseType)
            foreach (var f in b.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                if (!f.IsInitOnly) return false;
        return true;
    }
}
