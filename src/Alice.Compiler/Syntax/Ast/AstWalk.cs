using System.Collections;
using System.Reflection;

namespace Alice.Compiler.Syntax;

/// <summary>Generic pre-order walk over AST nodes (fields holding nodes or node lists); for declaration-time scans.</summary>
public static class AstWalk
{
    static readonly Dictionary<Type, FieldInfo[]> Fields = new();

    public static void Each(IEnumerable<Node> roots, Action<Node> f)
    {
        foreach (var r in roots) Each(r, f);
    }

    public static void Each(Node? n, Action<Node> f)
    {
        if (n == null) return;
        f(n);
        if (n is MethodDecl or TypeDecl) return;    // nested definitions are scanned on their own
        FieldInfo[] fs;
        lock (Fields)
            if (!Fields.TryGetValue(n.GetType(), out fs!))
                Fields[n.GetType()] = fs = n.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public)
                    .Where(x => typeof(Node).IsAssignableFrom(x.FieldType) || (typeof(IEnumerable).IsAssignableFrom(x.FieldType) && x.FieldType != typeof(string))).ToArray();
        foreach (var fi in fs)
        {
            var v = fi.GetValue(n);
            if (v is Node c) Each(c, f);
            else if (v is IEnumerable e)
                foreach (var x in e)
                {
                    if (x is Node xc) Each(xc, f);
                    else if (x is IEnumerable inner and not string) foreach (var y in inner) if (y is Node yc) Each(yc, f);
                }
        }
    }
}

/// <summary>In-place AST rewriting: `f` returns a replacement node (or null to keep and descend).</summary>
public static class AstRewrite
{
    public static void Each(List<Stmt> body, Func<Node, Node?> f)
    {
        for (int i = 0; i < body.Count; i++) body[i] = (Stmt)Visit(body[i], f);
    }

    static Node Visit(Node n, Func<Node, Node?> f)
    {
        if (f(n) is { } r) return r;
        if (n is MethodDecl or TypeDecl) return n;
        foreach (var fi in n.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
        {
            var v = fi.GetValue(n);
            if (v is Node c && fi.FieldType.IsAssignableFrom(typeof(Node)) == false && !fi.IsInitOnly)
            {
                var nc = Visit(c, f);
                if (fi.FieldType.IsInstanceOfType(nc)) fi.SetValue(n, nc);
            }
            else if (v is IList list && v is not string)
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i] is Node x)
                    {
                        var nx = Visit(x, f);
                        try { list[i] = nx; } catch (ArgumentException) { }
                    }
                    else if (list[i] is IList inner)
                        for (int k = 0; k < inner.Count; k++)
                            if (inner[k] is Node y) { var ny = Visit(y, f); try { inner[k] = ny; } catch (ArgumentException) { } }
                }
        }
        return n;
    }
}
