using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Alice.Runtime;

/// <summary>
/// Strided n-d tensor over 64-byte aligned native memory (ADR 0030). Row-major by default.
/// Views (<see cref="Slice"/>, <see cref="Narrow"/>, <see cref="Transpose"/>, <see cref="Reshape"/>) share memory and
/// keep their base alive; only the base frees memory. Disposing a base while views are in use is a caller bug.
/// </summary>
public sealed unsafe class Tensor<T> : IDisposable where T : unmanaged
{
    public const int Alignment = 64;

    T* _ptr;
    readonly int[] _shape, _strides;
    readonly bool _owns;
    readonly object? _keepAlive;

    Tensor(T* ptr, int[] shape, int[] strides, bool owns, object? keepAlive)
    {
        _ptr = ptr; _shape = shape; _strides = strides; _owns = owns; _keepAlive = keepAlive;
        long n = 1;
        foreach (var d in shape) { if (d < 0) throw new ArgumentOutOfRangeException(nameof(shape)); n *= d; }
        Length = n;
    }

    /// <summary>Zeroed, aligned, contiguous tensor.</summary>
    public static Tensor<T> Allocate(params int[] shape)
    {
        var strides = RowMajor(shape);
        long n = 1;
        foreach (var d in shape) n *= d;
        nuint bytes = (nuint)Math.Max(1, n * sizeof(T));
        var p = (T*)NativeMemory.AlignedAlloc(bytes, Alignment);
        NativeMemory.Clear(p, bytes);
        if (bytes > 4096) GC.AddMemoryPressure((long)bytes);
        return new Tensor<T>(p, (int[])shape.Clone(), strides, true, null);
    }

    public static Tensor<T> FromSpan(ReadOnlySpan<T> data, params int[] shape)
    {
        var t = Allocate(shape);
        if (data.Length != t.Length) { t.Dispose(); throw new ArgumentException("data length does not match shape"); }
        data.CopyTo(t.Span);
        return t;
    }

    /// <summary>Non-owning view over external memory (mapped files, pinned buffers). <paramref name="keepAlive"/> is held for the view's lifetime.</summary>
    public static Tensor<T> Wrap(T* ptr, object? keepAlive, params int[] shape)
        => new(ptr, (int[])shape.Clone(), RowMajor(shape), false, keepAlive);

    static int[] RowMajor(int[] shape)
    {
        var s = new int[shape.Length];
        int acc = 1;
        for (int i = shape.Length - 1; i >= 0; i--) { s[i] = acc; acc = checked(acc * Math.Max(1, shape[i])); }
        return s;
    }

    public int Rank => _shape.Length;
    public long Length { get; }
    public ReadOnlySpan<int> Shape => _shape;
    public ReadOnlySpan<int> Strides => _strides;
    public T* Pointer => _ptr;
    public nint Address => (nint)_ptr;

    public bool IsContiguous
    {
        get
        {
            int acc = 1;
            for (int i = _shape.Length - 1; i >= 0; i--)
            {
                if (_shape[i] != 1 && _strides[i] != acc) return false;
                acc *= _shape[i];
            }
            return true;
        }
    }

    /// <summary>All elements as one span; contiguous tensors only.</summary>
    public Span<T> Span
    {
        get
        {
            if (!IsContiguous) throw new InvalidOperationException("tensor is not contiguous; call Contiguous()");
            return new Span<T>(_ptr, checked((int)Length));
        }
    }

    /// <summary>Row <paramref name="i"/> of a rank-2 tensor whose last dimension is dense.</summary>
    public Span<T> Row(int i)
    {
        if (Rank != 2 || _strides[1] != 1) throw new InvalidOperationException("Row needs a rank-2 tensor with dense rows");
        if ((uint)i >= (uint)_shape[0]) throw new IndexOutOfRangeException();
        return new Span<T>(_ptr + (long)i * _strides[0], _shape[1]);
    }

    public ref T this[int i]
    {
        get { Check(1); Bound(i, 0); return ref _ptr[(long)i * _strides[0]]; }
    }

    public ref T this[int i, int j]
    {
        get { Check(2); Bound(i, 0); Bound(j, 1); return ref _ptr[(long)i * _strides[0] + (long)j * _strides[1]]; }
    }

    public ref T this[int i, int j, int k]
    {
        get { Check(3); Bound(i, 0); Bound(j, 1); Bound(k, 2); return ref _ptr[(long)i * _strides[0] + (long)j * _strides[1] + (long)k * _strides[2]]; }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void Check(int rank) { if (_shape.Length != rank) throw new InvalidOperationException($"tensor has rank {_shape.Length}, indexed with {rank}"); }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void Bound(int i, int d) { if ((uint)i >= (uint)_shape[d]) throw new IndexOutOfRangeException(); }

    object Base => _owns ? this : _keepAlive ?? this;

    /// <summary>View of <paramref name="count"/> entries of dimension <paramref name="dim"/> starting at <paramref name="start"/>.</summary>
    public Tensor<T> Narrow(int dim, int start, int count)
    {
        if ((uint)dim >= (uint)Rank || start < 0 || count < 0 || start + count > _shape[dim]) throw new ArgumentOutOfRangeException();
        var shape = (int[])_shape.Clone();
        shape[dim] = count;
        return new Tensor<T>(_ptr + (long)start * _strides[dim], shape, (int[])_strides.Clone(), false, Base);
    }

    public Tensor<T> Slice(int start, int count) => Narrow(0, start, count);

    /// <summary>Swaps the two dimensions of a rank-2 tensor (strided view, no copy).</summary>
    public Tensor<T> Transpose()
    {
        if (Rank != 2) throw new InvalidOperationException("Transpose needs rank 2");
        return new Tensor<T>(_ptr, [_shape[1], _shape[0]], [_strides[1], _strides[0]], false, Base);
    }

    public Tensor<T> Reshape(params int[] shape)
    {
        if (!IsContiguous) throw new InvalidOperationException("Reshape needs a contiguous tensor");
        long n = 1;
        foreach (var d in shape) n *= d;
        if (n != Length) throw new ArgumentException("reshape must keep the element count");
        return new Tensor<T>(_ptr, (int[])shape.Clone(), RowMajor(shape), false, Base);
    }

    /// <summary>Dense copy (or this tensor when already contiguous).</summary>
    public Tensor<T> Contiguous()
    {
        if (IsContiguous) return this;
        var t = Allocate(_shape);
        var dst = t.Span;
        var idx = new int[Rank];
        for (long n = 0; n < Length; n++)
        {
            long off = 0;
            for (int d = 0; d < Rank; d++) off += (long)idx[d] * _strides[d];
            dst[(int)n] = _ptr[off];
            for (int d = Rank - 1; d >= 0; d--) { if (++idx[d] < _shape[d]) break; idx[d] = 0; }
        }
        return t;
    }

    public void Dispose()
    {
        Free();
        GC.SuppressFinalize(this);
    }

    ~Tensor() => Free();

    void Free()
    {
        if (!_owns || _ptr == null) return;
        long bytes = Math.Max(1, Length * sizeof(T));
        NativeMemory.AlignedFree(_ptr);
        _ptr = null;
        if (bytes > 4096) GC.RemoveMemoryPressure(bytes);
    }
}
