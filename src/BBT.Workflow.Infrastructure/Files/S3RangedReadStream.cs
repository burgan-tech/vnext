using Amazon.S3;
using Amazon.S3.Model;

namespace BBT.Workflow.Files;

/// <summary>
/// A seekable, read-only view of one S3 object of known length that fetches bytes lazily with ranged GETs: a read at
/// the current position reuses the open response, a read after a seek opens <c>bytes=position-</c>. Lets
/// <c>FileStreamResult</c> serve <c>Range</c> (206/416, <c>If-Range</c>) and HEAD without loading the object, and a
/// HEAD never opens a GET at all.
/// </summary>
internal sealed class S3RangedReadStream(IAmazonS3 client, string bucket, string key, long length) : Stream
{
    private GetObjectResponse? _response;
    private Stream? _body;
    private long _bodyPosition;
    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;

    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        return _position;
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty || _position >= length)
            return 0;

        if (_body is null || _bodyPosition != _position)
            await OpenAtAsync(_position, cancellationToken).ConfigureAwait(false);

        var wanted = (int)Math.Min(buffer.Length, length - _position);
        var read = await _body!.ReadAsync(buffer[..wanted], cancellationToken).ConfigureAwait(false);
        if (read == 0)
            throw new IOException($"Object {key} ended at {_position} of {length} bytes.");
        _position += read;
        _bodyPosition += read;
        return read;
    }

    private async Task OpenAtAsync(long start, CancellationToken cancellationToken)
    {
        CloseBody();
        _response = await client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = bucket,
            Key = key,
            ByteRange = new ByteRange(start, length - 1),
        }, cancellationToken).ConfigureAwait(false);
        _body = _response.ResponseStream;
        _bodyPosition = start;
    }

    private void CloseBody()
    {
        _body?.Dispose();
        _response?.Dispose();
        _body = null;
        _response = null;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            CloseBody();
        base.Dispose(disposing);
    }
}
