using System.Runtime.CompilerServices;

namespace Alice.Runtime;

/// <summary>A `describe`/`context` group: hooks, examples and nested groups (registration order kept).</summary>
public sealed class SpecGroup
{
    public string Name = "";
    public int Line;
    public SpecGroup? Parent;
    public readonly List<Action> Before = new(), After = new(), BeforeAll = new(), AfterAll = new();
    public readonly List<object> Children = new();     // SpecGroup | SpecExample
    public bool Focus, Skip;

    public string FullName => Parent == null || Parent.Parent == null ? Name : Parent.FullName + " " + Name;
}

/// <summary>One `it`: body (null = pending), location from the Alice source (#line).</summary>
public sealed class SpecExample
{
    public string Name = "";
    public SpecGroup Group = null!;
    public Action? Body;
    public string File = "";
    public int Line;
    public bool Focus, Skip;
    public string? Reason;

    public string FullName => (Group.FullName + " " + Name).Trim();
}

/// <summary>Registry filled by the DSL while a test file's top-level code runs.</summary>
public static class Specs
{
    public static readonly SpecGroup Root = new();
    [ThreadStatic] static SpecGroup? current;
    static SpecGroup Current => current ?? Root;

    public static void Reset()
    {
        Root.Children.Clear();
        Root.Before.Clear(); Root.After.Clear(); Root.BeforeAll.Clear(); Root.AfterAll.Clear();
        current = null;
    }

    internal static void Group(string name, Action body, bool focus, bool skip, int line = 0)
    {
        var g = new SpecGroup { Name = name, Parent = Current, Focus = focus, Skip = skip, Line = line };
        Current.Children.Add(g);
        var saved = current;
        current = g;
        try { body(); }
        finally { current = saved; }
    }

    internal static void Example(string name, Action? body, string file, int line, bool focus, bool skip, string? reason = null) =>
        Current.Children.Add(new SpecExample { Name = name, Group = Current, Body = body, File = file, Line = line, Focus = focus, Skip = skip, Reason = reason });

    internal static SpecGroup Here => Current;
}

/// <summary>RSpec-style DSL (`describe`, `context`, `it`, hooks). Example locations come from Alice #line info.</summary>
public static partial class Prelude
{
    public static void describe(string name, Action body, [CallerLineNumber] int line = 0) => Specs.Group(name, body, false, false, line);
    public static void describe(Type type, Action body, [CallerLineNumber] int line = 0) => Specs.Group(type.Name, body, false, false, line);
    public static void context(string name, Action body, [CallerLineNumber] int line = 0) => Specs.Group(name, body, false, false, line);
    public static void fdescribe(string name, Action body, [CallerLineNumber] int line = 0) => Specs.Group(name, body, true, false, line);
    public static void xdescribe(string name, Action body, [CallerLineNumber] int line = 0) => Specs.Group(name, body, false, true, line);
    public static void fcontext(string name, Action body, [CallerLineNumber] int line = 0) => Specs.Group(name, body, true, false, line);
    public static void xcontext(string name, Action body, [CallerLineNumber] int line = 0) => Specs.Group(name, body, false, true, line);

    public static void it(string name, Action body, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) => Specs.Example(name, body, file, line, false, false);
    /// <summary>`it "todo"` without a body: pending.</summary>
    public static void it(string name, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) => Specs.Example(name, null, file, line, false, false, "not implemented yet");
    public static void fit(string name, Action body, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) => Specs.Example(name, body, file, line, true, false);
    public static void xit(string name, Action body, [CallerFilePath] string file = "", [CallerLineNumber] int line = 0) => Specs.Example(name, body, file, line, false, true, "skipped with xit");
    public static void pending(string reason) => throw new PendingException(reason);
    public static void skip(string reason) => throw new SkipException(reason);

    public static void before(Action body) => Specs.Here.Before.Add(body);
    public static void after(Action body) => Specs.Here.After.Add(body);
    public static void before_all(Action body) => Specs.Here.BeforeAll.Add(body);
    public static void after_all(Action body) => Specs.Here.AfterAll.Add(body);
}

public sealed class PendingException(string reason) : Exception(reason);
public sealed class SkipException(string reason) : Exception(reason);

/// <summary>xUnit-style: `@[Test] def adds`; fresh instance per test, `setup`/`teardown` run around it.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute(string? name = null) : Attribute
{
    public string? Name { get; } = name;
    public string? Skip { get; set; }
}

/// <summary>Data-driven test: one `@[Cases(...)]` per row (`@[Cases(1, 2, 3)]`).</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class CasesAttribute(params object?[] args) : Attribute
{
    public object?[] Args { get; } = args;
}
