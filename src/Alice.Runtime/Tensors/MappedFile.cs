using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;

namespace Alice.Runtime;

/// <summary>
/// Read-only memory-mapped file exposed as spans, tensors and quantized matrices over the mapping (no copy; pages
/// load on first touch). Views keep the mapping alive. Views are read-only memory: writing through a returned
/// <see cref="Tensor{T}"/> faults.
/// </summary>
public sealed unsafe class MappedFile : IDisposable
{
    readonly MemoryMappedFile _file;
    readonly MemoryMappedViewAccessor _view;
    byte* _base;

    public long Length { get; }

    MappedFile(MemoryMappedFile file, MemoryMappedViewAccessor view, long length)
    {
        _file = file; _view = view; Length = length;
        byte* p = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _base = p + view.PointerOffset;
    }

    public static MappedFile Open(string path)
    {
        long len = new FileInfo(path).Length;
        if (len == 0) throw new ArgumentException("cannot map an empty file");
        var f = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
        var v = f.CreateViewAccessor(0, len, MemoryMappedFileAccess.Read);
        return new MappedFile(f, v, len);
    }

    public byte* Pointer => _base != null ? _base : throw new ObjectDisposedException(nameof(MappedFile));

    byte* At(long offset, long bytes)
    {
        if (offset < 0 || bytes < 0 || offset + bytes > Length) throw new ArgumentOutOfRangeException(nameof(offset));
        return Pointer + offset;
    }

    public ReadOnlySpan<T> Span<T>(long offset, int count) where T : unmanaged
        => new(At(offset, (long)count * sizeof(T)), count);

    public Tensor<T> Tensor<T>(long offset, params int[] shape) where T : unmanaged
    {
        long n = 1;
        foreach (var d in shape) n *= d;
        return Alice.Runtime.Tensor<T>.Wrap((T*)At(offset, n * sizeof(T)), this, shape);
    }

    public QuantizedMatrix Quantized(long offset, int rows, int cols, QuantFormat fmt)
        => QuantizedMatrix.Wrap(At(offset, (long)rows * (cols / QuantizedMatrix.BlockSize) * QuantizedMatrix.BlockBytes(fmt)), rows, cols, fmt, this);

    public void Dispose()
    {
        if (_base == null) return;
        _base = null;
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
    }
}
