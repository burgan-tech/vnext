using System.Security.Cryptography;

namespace BBT.Workflow.Files;

/// <summary>
/// Read-only pass-through that counts and sha256-hashes every byte read from the source, and refuses to read past the
/// declared length. A streamed upload's handle <c>size</c>/<c>eTag</c> come from here: what was actually sent to the
/// store, not what the client declared.
/// </summary>
internal sealed class HashingReadStream(Stream source, long expectedLength) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private string? _sha256;

    public long BytesRead { get; private set; }

    /// <summary>The lower-case hex sha256 of everything read; final once the source is exhausted.</summary>
    public string Sha256 => _sha256 ??= Convert.ToHexStringLower(_hash.GetHashAndReset());

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => expectedLength;
    public override long Position { get => BytesRead; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var read = source.Read(buffer);
        Account(buffer[..read]);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Account(buffer.Span[..read]);
        return read;
    }

    private void Account(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return;
        BytesRead += bytes.Length;
        if (BytesRead > expectedLength)
            throw new IOException($"The body is longer than its declared length ({expectedLength} bytes).");
        _hash.AppendData(bytes);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _hash.Dispose();
        base.Dispose(disposing);
    }
}
