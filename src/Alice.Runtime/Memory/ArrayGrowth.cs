namespace Alice.Runtime;

/// <summary>Amortized growth for SoA columns. Allocation only when capacity is exceeded.</summary>
public static class ArrayGrowth
{
    public static void Ensure<T>(ref T[] array, int required)
    {
        if (array.Length >= required) return;
        int cap = Math.Max(required, Math.Max(16, array.Length * 2));
        Array.Resize(ref array, cap);
    }
}

/// <summary>Stack of recycled indices for stores whose rows can die.</summary>
public sealed class FreeList
{
    int[] _items = new int[16];
    int _count;

    public int Count => _count;
    public ReadOnlySpan<int> Items => _items.AsSpan(0, _count);

    public void Push(int i)
    {
        ArrayGrowth.Ensure(ref _items, _count + 1);
        _items[_count++] = i;
    }

    public bool TryPop(out int i)
    {
        if (_count == 0) { i = -1; return false; }
        i = _items[--_count];
        return true;
    }

    public void Clear() => _count = 0;
}
