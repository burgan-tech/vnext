using System.Text;

namespace BBT.Workflow.Middlewares;

/// <summary>
/// Lazily materialized raw request body captured by <see cref="RawRequestBodyBufferingMiddleware"/>.
/// Holds the buffered UTF-8 bytes and converts them to a string only when a consumer actually reads
/// <see cref="Text"/> — the UTF-16 conversion roughly doubles the payload's memory footprint, so it
/// must not be paid for the majority of requests whose raw body is never read.
/// </summary>
/// <param name="buffer">The buffered request body bytes. May be longer than <paramref name="length"/> (MemoryStream.GetBuffer).</param>
/// <param name="length">The number of valid bytes in <paramref name="buffer"/>.</param>
public sealed class RawRequestBodyCapture(byte[] buffer, int length)
{
    private string? _text;

    /// <summary>
    /// The raw body decoded as UTF-8 text. Materialized on first access and cached,
    /// so repeated reads within the same request pay the conversion once.
    /// </summary>
    public string Text => _text ??= Encoding.UTF8.GetString(buffer, 0, length);
}
