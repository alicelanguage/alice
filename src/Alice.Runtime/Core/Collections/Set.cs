using System.Collections;
using System.Text;

namespace Alice.Core.Collections;

/// <summary>
/// Ruby's `Set`: an unordered collection of unique elements over <see cref="HashSet{T}"/>, with Ruby's operators
/// (`|` union, `&amp;` intersection, `-` difference, `^` symmetric difference, `&lt;=` subset) returning new sets.
/// Iteration uses the struct enumerator of <see cref="HashSet{T}"/> (0 bytes).
/// </summary>
public sealed class Set<T> : ISet<T>, IReadOnlySet<T>, IEquatable<Set<T>>
{
    readonly HashSet<T> items;

    /// <summary>An empty set.</summary>
    public Set() => items = [];

    /// <summary>An empty set with an equality comparer.</summary>
    public Set(IEqualityComparer<T>? comparer) => items = new HashSet<T>(comparer);

    /// <summary>Ruby `Set.new(enum)`.</summary>
    public Set(IEnumerable<T> source, IEqualityComparer<T>? comparer = null) => items = new HashSet<T>(source, comparer);

    Set(HashSet<T> h) => items = h;

    /// <summary>Ruby `Set[a, b, c]`.</summary>
    public static Set<T> Of(params ReadOnlySpan<T> values)
    {
        var s = new Set<T>();
        foreach (var v in values) s.items.Add(v);
        return s;
    }

    /// <summary>Number of elements (Ruby `size`, `length`).</summary>
    public int Count => items.Count;

    /// <summary>Ruby `size`.</summary>
    public int Size => items.Count;

    /// <summary>Ruby `empty?`.</summary>
    public bool IsEmpty => items.Count == 0;

    /// <summary>Ruby `include?` / `member?` / `===`.</summary>
    public bool Include(T item) => items.Contains(item);

    /// <inheritdoc/>
    public bool Contains(T item) => items.Contains(item);

    /// <summary>Ruby `add?`: adds and returns true when the element was new.</summary>
    public bool Add(T item) => items.Add(item);

    /// <summary>Ruby `delete?`: removes and returns true when it was present.</summary>
    public bool Delete(T item) => items.Remove(item);

    /// <inheritdoc/>
    public bool Remove(T item) => items.Remove(item);

    /// <summary>Ruby `&lt;&lt;`: adds and returns this set.</summary>
    public static Set<T> operator <<(Set<T> s, T item) { s.items.Add(item); return s; }

    /// <summary>Ruby `|` / `union` / `+`.</summary>
    public static Set<T> operator |(Set<T> a, Set<T> b) { var r = new HashSet<T>(a.items, a.items.Comparer); r.UnionWith(b.items); return new(r); }

    /// <summary>Ruby `&amp;` / `intersection`.</summary>
    public static Set<T> operator &(Set<T> a, Set<T> b) { var r = new HashSet<T>(a.items, a.items.Comparer); r.IntersectWith(b.items); return new(r); }

    /// <summary>Ruby `-` / `difference`.</summary>
    public static Set<T> operator -(Set<T> a, Set<T> b) { var r = new HashSet<T>(a.items, a.items.Comparer); r.ExceptWith(b.items); return new(r); }

    /// <summary>Ruby `^`: elements in exactly one of the sets.</summary>
    public static Set<T> operator ^(Set<T> a, Set<T> b) { var r = new HashSet<T>(a.items, a.items.Comparer); r.SymmetricExceptWith(b.items); return new(r); }

    /// <summary>Ruby `&lt;=` / `subset?`.</summary>
    public static bool operator <=(Set<T> a, Set<T> b) => a.items.IsSubsetOf(b.items);

    /// <summary>Ruby `&gt;=` / `superset?`.</summary>
    public static bool operator >=(Set<T> a, Set<T> b) => a.items.IsSupersetOf(b.items);

    /// <summary>Ruby `&lt;` / `proper_subset?`.</summary>
    public static bool operator <(Set<T> a, Set<T> b) => a.items.IsProperSubsetOf(b.items);

    /// <summary>Ruby `&gt;` / `proper_superset?`.</summary>
    public static bool operator >(Set<T> a, Set<T> b) => a.items.IsProperSupersetOf(b.items);

    /// <summary>Ruby `union`.</summary>
    public Set<T> Union(Set<T> other) => this | other;

    /// <summary>Ruby `intersection`.</summary>
    public Set<T> Intersection(Set<T> other) => this & other;

    /// <summary>Ruby `difference`.</summary>
    public Set<T> Difference(Set<T> other) => this - other;

    /// <summary>Ruby `subset?`.</summary>
    public bool IsSubset(IEnumerable<T> other) => items.IsSubsetOf(other);

    /// <summary>Ruby `superset?`.</summary>
    public bool IsSuperset(IEnumerable<T> other) => items.IsSupersetOf(other);

    /// <summary>Ruby `disjoint?`.</summary>
    public bool IsDisjoint(IEnumerable<T> other) => !items.Overlaps(other);

    /// <summary>Ruby `intersect?`.</summary>
    public bool IsIntersect(IEnumerable<T> other) => items.Overlaps(other);

    /// <summary>Ruby `merge(enum)`: adds every element; returns this.</summary>
    public Set<T> Merge(IEnumerable<T> other) { items.UnionWith(other); return this; }

    /// <summary>Ruby `to_a`.</summary>
    public T[] ToA() { var a = new T[items.Count]; items.CopyTo(a); return a; }

    /// <summary>Ruby `dup`.</summary>
    public Set<T> Dup() => new(new HashSet<T>(items, items.Comparer));

    /// <summary>Ruby `clear`.</summary>
    public void Clear() => items.Clear();

    /// <summary>Struct enumerator (0 bytes).</summary>
    public HashSet<T>.Enumerator GetEnumerator() => items.GetEnumerator();

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => items.GetEnumerator();
    bool ICollection<T>.IsReadOnly => false;
    void ICollection<T>.Add(T item) => items.Add(item);
    void ICollection<T>.CopyTo(T[] array, int arrayIndex) => items.CopyTo(array, arrayIndex);
    void ISet<T>.ExceptWith(IEnumerable<T> other) => items.ExceptWith(other);
    void ISet<T>.IntersectWith(IEnumerable<T> other) => items.IntersectWith(other);
    void ISet<T>.SymmetricExceptWith(IEnumerable<T> other) => items.SymmetricExceptWith(other);
    void ISet<T>.UnionWith(IEnumerable<T> other) => items.UnionWith(other);
    /// <inheritdoc/>
    public bool IsProperSubsetOf(IEnumerable<T> other) => items.IsProperSubsetOf(other);
    /// <inheritdoc/>
    public bool IsProperSupersetOf(IEnumerable<T> other) => items.IsProperSupersetOf(other);
    /// <inheritdoc/>
    public bool IsSubsetOf(IEnumerable<T> other) => items.IsSubsetOf(other);
    /// <inheritdoc/>
    public bool IsSupersetOf(IEnumerable<T> other) => items.IsSupersetOf(other);
    /// <inheritdoc/>
    public bool Overlaps(IEnumerable<T> other) => items.Overlaps(other);
    /// <inheritdoc/>
    public bool SetEquals(IEnumerable<T> other) => items.SetEquals(other);

    /// <summary>Ruby `==`: same elements.</summary>
    public bool Equals(Set<T>? other) => other is not null && items.SetEquals(other.items);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Set<T> s && Equals(s);

    /// <summary>Order-independent hash of the elements.</summary>
    public override int GetHashCode()
    {
        int h = 0;
        var cmp = items.Comparer;
        foreach (var x in items) h ^= x is null ? 0 : cmp.GetHashCode(x);
        return h;
    }

    /// <summary>Ruby `inspect`: <c>#&lt;Set: {1, 2}&gt;</c>.</summary>
    public override string ToString()
    {
        var sb = new StringBuilder("#<Set: {");
        bool first = true;
        foreach (var x in items) { if (!first) sb.Append(", "); sb.Append(Alice.Runtime.Prelude.inspect(x)); first = false; }
        return sb.Append("}>").ToString();
    }
}
