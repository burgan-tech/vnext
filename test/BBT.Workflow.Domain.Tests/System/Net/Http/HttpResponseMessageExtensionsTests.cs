using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace System.Net.Http;

/// <summary>
/// Unit tests for HttpResponseMessageExtensions
/// </summary>
public class HttpResponseMessageExtensionsTests
{
    [Fact]
    public async Task ReadDecompressedContentAsync_WithGzipContent_ShouldDecompress()
    {
        // Arrange
        var originalContent = "This is a test content that will be compressed.";
        var compressedBytes = CompressString(originalContent);

        var response = new HttpResponseMessage
        {
            Content = new ByteArrayContent(compressedBytes)
        };
        response.Content.Headers.ContentEncoding.Add("gzip");

        // Act
        var result = await response.ReadDecompressedContentAsync(CancellationToken.None);

        // Assert
        Assert.Equal(originalContent, result);
    }

    [Fact]
    public async Task ReadDecompressedContentAsync_WithoutGzipContent_ShouldReturnAsIs()
    {
        // Arrange
        var content = "This is a test content without compression.";
        var response = new HttpResponseMessage
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };

        // Act
        var result = await response.ReadDecompressedContentAsync(CancellationToken.None);

        // Assert
        Assert.Equal(content, result);
    }

    [Fact]
    public async Task ReadDecompressedContentAsync_WithEmptyGzipContent_ShouldReturnEmpty()
    {
        // Arrange
        var compressedBytes = CompressString(string.Empty);
        var response = new HttpResponseMessage
        {
            Content = new ByteArrayContent(compressedBytes)
        };
        response.Content.Headers.ContentEncoding.Add("gzip");

        // Act
        var result = await response.ReadDecompressedContentAsync(CancellationToken.None);

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public async Task ReadDecompressedContentAsync_WithLargeGzipContent_ShouldDecompress()
    {
        // Arrange
        var largeContent = string.Join("", Enumerable.Repeat("Large test content with repetitions. ", 1000));
        var compressedBytes = CompressString(largeContent);

        var response = new HttpResponseMessage
        {
            Content = new ByteArrayContent(compressedBytes)
        };
        response.Content.Headers.ContentEncoding.Add("gzip");

        // Act
        var result = await response.ReadDecompressedContentAsync(CancellationToken.None);

        // Assert
        Assert.Equal(largeContent, result);
    }

    [Fact]
    public async Task ReadDecompressedContentAsync_WithMultipleEncodings_ShouldHandleGzip()
    {
        // Arrange
        var originalContent = "Content with multiple encodings.";
        var compressedBytes = CompressString(originalContent);

        var response = new HttpResponseMessage
        {
            Content = new ByteArrayContent(compressedBytes)
        };
        response.Content.Headers.ContentEncoding.Add("gzip");
        response.Content.Headers.ContentEncoding.Add("identity");

        // Act
        var result = await response.ReadDecompressedContentAsync(CancellationToken.None);

        // Assert
        Assert.Equal(originalContent, result);
    }

    [Fact]
    public async Task ReadDecompressedContentAsync_WithCancellation_ShouldRespectToken()
    {
        // Arrange
        var content = "Test content";
        var response = new HttpResponseMessage
        {
            Content = new StringContent(content)
        };
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<TaskCanceledException>(async () =>
            await response.ReadDecompressedContentAsync(cts.Token));
    }

    [Fact]
    public async Task ReadDecompressedContentAsync_WithUtf8GzipContent_ShouldPreserveEncoding()
    {
        // Arrange
        var originalContent = "Türkçe karakterler: ğüşıöçĞÜŞİÖÇ";
        var compressedBytes = CompressString(originalContent);

        var response = new HttpResponseMessage
        {
            Content = new ByteArrayContent(compressedBytes)
        };
        response.Content.Headers.ContentEncoding.Add("gzip");

        // Act
        var result = await response.ReadDecompressedContentAsync(CancellationToken.None);

        // Assert
        Assert.Equal(originalContent, result);
    }

    [Fact]
    public async Task ReadDecompressedContentAsync_WithJsonGzipContent_ShouldDecompress()
    {
        // Arrange
        var jsonContent = "{\"name\":\"test\",\"value\":123,\"nested\":{\"array\":[1,2,3]}}";
        var compressedBytes = CompressString(jsonContent);

        var response = new HttpResponseMessage
        {
            Content = new ByteArrayContent(compressedBytes)
        };
        response.Content.Headers.ContentEncoding.Add("gzip");

        // Act
        var result = await response.ReadDecompressedContentAsync(CancellationToken.None);

        // Assert
        Assert.Equal(jsonContent, result);
    }

    [Fact]
    public async Task ReadDecompressedContentAsync_WithDeflateContent_ShouldDecompress()
    {
        // Arrange
        var originalContent = "Content with deflate encoding.";
        var compressedBytes = CompressStringWithZLib(originalContent);

        var response = new HttpResponseMessage
        {
            Content = new ByteArrayContent(compressedBytes)
        };
        response.Content.Headers.ContentEncoding.Add("deflate");

        // Act
        var result = await response.ReadDecompressedContentAsync(CancellationToken.None);

        // Assert
        Assert.Equal(originalContent, result);
    }

    [Fact]
    public async Task ReadDecompressedContentAsync_WithBrotliContent_ShouldDecompress()
    {
        // Arrange
        var originalContent = "Content with Brotli encoding.";
        var compressedBytes = CompressStringWithBrotli(originalContent);

        var response = new HttpResponseMessage
        {
            Content = new ByteArrayContent(compressedBytes)
        };
        response.Content.Headers.ContentEncoding.Add("br");

        // Act
        var result = await response.ReadDecompressedContentAsync(CancellationToken.None);

        // Assert
        Assert.Equal(originalContent, result);
    }

    #region Helper Methods

    private static byte[] CompressString(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress))
        {
            gzip.Write(bytes, 0, bytes.Length);
        }
        return output.ToArray();
    }

    private static byte[] CompressStringWithZLib(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Fastest))
        {
            zlib.Write(bytes, 0, bytes.Length);
        }
        return output.ToArray();
    }

    private static byte[] CompressStringWithBrotli(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        using var output = new MemoryStream();
        using (var brotli = new BrotliStream(output, CompressionMode.Compress))
        {
            brotli.Write(bytes, 0, bytes.Length);
        }
        return output.ToArray();
    }

    #endregion
}

