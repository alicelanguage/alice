using System.Text;
using Alice.Compiler.Semantics;

namespace Alice.Compiler.Backend;

/// <summary>Generates the columnar store for an `@[SoA]` struct (nested `Store` class).</summary>
public static class SoAStore
{
    const string Growth = "global::Alice.Runtime.ArrayGrowth";

    public static string Emit(TypeSym s)
    {
        var cols = s.FieldOrder.Select(f => s.Fields[f]).ToList();
        string self = s.Name;
        var sb = new StringBuilder();
        sb.Append($"public sealed class Store : global::System.Collections.Generic.IEnumerable<{self}>\n{{\n");
        foreach (var c in cols) sb.Append($"    public {c.Type.Cs}[] {c.CsName} = new {c.Type.Cs}[16];\n");
        sb.Append("    int _count;\n");
        sb.Append("    public int Count => _count;\n");
        sb.Append("    public int size() => _count;\n");
        sb.Append("    public void clear() => _count = 0;\n");
        sb.Append($"    public int add({self} item)\n    {{\n        int i = _count;\n");
        sb.Append($"        if (i >= {cols[0].CsName}.Length)\n        {{\n");
        foreach (var c in cols) sb.Append($"            {Growth}.Ensure(ref {c.CsName}, i + 1);\n");
        sb.Append("        }\n");
        foreach (var c in cols) sb.Append($"        {c.CsName}[i] = item.{c.CsName};\n");
        sb.Append("        _count = i + 1;\n        return i;\n    }\n");
        string ctorArgs = string.Join(", ", cols.Select(c => $"{c.CsName}[index]"));
        sb.Append($"    public {self} this[int index]\n    {{\n");
        sb.Append($"        get {{ if ((uint)index >= (uint)_count) throw new global::System.IndexOutOfRangeException(); return new {self}({ctorArgs}); }}\n");
        sb.Append("        set { if ((uint)index >= (uint)_count) throw new global::System.IndexOutOfRangeException(); ");
        foreach (var c in cols) sb.Append($"{c.CsName}[index] = value.{c.CsName}; ");
        sb.Append("}\n    }\n");
        sb.Append("    /// <summary>O(1) swap-remove: the last row moves into the hole.</summary>\n");
        sb.Append("    public void remove_at(int index)\n    {\n        if ((uint)index >= (uint)_count) throw new global::System.IndexOutOfRangeException();\n        int last = --_count;\n");
        foreach (var c in cols) sb.Append($"        {c.CsName}[index] = {c.CsName}[last];\n");
        sb.Append("    }\n");
        sb.Append($"    public global::System.Collections.Generic.IEnumerator<{self}> GetEnumerator() {{ for (int i = 0; i < _count; i++) yield return this[i]; }}\n");
        sb.Append("    global::System.Collections.IEnumerator global::System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();\n");
        sb.Append("}\n");
        return sb.ToString();
    }
}
