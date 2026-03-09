using System.IO.Compression;

namespace System.Net.Http;

/// <summary>
/// Extension methods for reading HTTP response content with support for compressed encodings.
/// Used by Execution invokers when the platform returns gzip, deflate, or br encoded responses.
/// </summary>
public static class HttpResponseMessageExtensions
{
    /// <summary>
    /// Reads the response content as a string, decompressing when Content-Encoding is gzip, deflate, or br.
    /// Use this when the client does not use AutomaticDecompression or when a proxy strips encoding headers.
    /// </summary>
    /// <param name="response">The HTTP response message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The decompressed or raw response body as string.</returns>
    public static async Task<string> ReadDecompressedContentAsync(this HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var encodings = response.Content.Headers.ContentEncoding;
        if (encodings.Count == 0)
            return await response.Content.ReadAsStringAsync(cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        if (encodings.Contains("gzip"))
        {
            await using var gzip = new GZipStream(stream, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip);
            return await reader.ReadToEndAsync(cancellationToken);
        }

        if (encodings.Contains("deflate"))
        {
            await using var deflate = new ZLibStream(stream, CompressionMode.Decompress);
            using var reader = new StreamReader(deflate);
            return await reader.ReadToEndAsync(cancellationToken);
        }

        if (encodings.Contains("br"))
        {
            await using var brotli = new BrotliStream(stream, CompressionMode.Decompress);
            using var reader = new StreamReader(brotli);
            return await reader.ReadToEndAsync(cancellationToken);
        }

        return await response.Content.ReadAsStringAsync(cancellationToken);
    }
}
