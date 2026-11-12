using System.Buffers;

namespace BBT.Workflow.Canonicalization;

/// <summary>
/// <see cref="IBufferWriter{T}"/> over an <see cref="ArrayPool{T}"/>-rented byte array. Used on per-request
/// hashing paths (representation ETag) so a large response body does not allocate a fresh (possibly
/// large-object-heap) array per request. Not thread-safe; dispose to return the buffer to the pool.
/// </summary>
public sealed class PooledByteBufferWriter : IBufferWriter<byte>, IDisposable
{
    private const int MinimumGrowth = 256;
    private byte[] _buffer;
    private int _written;

    /// <summary>Creates a writer with at least <paramref name="initialCapacity"/> bytes of space.</summary>
    public PooledByteBufferWriter(int initialCapacity = MinimumGrowth)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(initialCapacity, MinimumGrowth));
    }

    /// <summary>Bytes written so far.</summary>
    public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);

    /// <summary>Bytes written so far, as memory (valid until the next write or dispose).</summary>
    public ReadOnlyMemory<byte> WrittenMemory => _buffer.AsMemory(0, _written);

    /// <summary>Number of bytes written.</summary>
    public int WrittenCount => _written;

    /// <inheritdoc />
    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (_written + count > _buffer.Length)
            throw new InvalidOperationException("Cannot advance past the end of the buffer.");
        _written += count;
    }

    /// <inheritdoc />
    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_written);
    }

    /// <inheritdoc />
    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_written);
    }

    private void EnsureCapacity(int sizeHint)
    {
        ObjectDisposedException.ThrowIf(_buffer.Length == 0, this);
        if (sizeHint < 1) sizeHint = 1;
        if (_buffer.Length - _written >= sizeHint) return;

        var newSize = Math.Max(_buffer.Length * 2, _written + Math.Max(sizeHint, MinimumGrowth));
        var larger = ArrayPool<byte>.Shared.Rent(newSize);
        _buffer.AsSpan(0, _written).CopyTo(larger);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = larger;
    }

    /// <summary>Returns the buffer to the pool.</summary>
    public void Dispose()
    {
        if (_buffer.Length == 0) return;
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
        _written = 0;
    }
}
