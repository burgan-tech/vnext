using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Files;

/// <summary>
/// vnext-client-sdk-core#145: Dapr 1.17+ <c>bindings.aws.s3</c> frames every TLS upload as aws-chunked with a CRC32
/// trailer, and a server that does not decode it stores the frame (6265-byte PDF → 6309-byte object). The read path
/// must serve the file, never the frame, and refuse bytes that are neither.
/// </summary>
public sealed class StoredFileVerifierTests
{
    private static byte[] File(int length)
    {
        var bytes = new byte[length];
        new Random(length).NextBytes(bytes);
        "%PDF-1.4"u8.CopyTo(bytes);
        return bytes;
    }

    private static FileHandle HandleOf(byte[] file) => new(
        "vnext-blob-s3", Guid.NewGuid().ToString(), "a.pdf", "application/pdf", file.LongLength,
        Convert.ToHexStringLower(SHA256.HashData(file)), new FileOwner("core", "kyc", Guid.NewGuid().ToString()));

    /// <summary>The exact frame observed on the bank's S3: one chunk, the zero chunk, one CRC32 trailer.</summary>
    private static byte[] BankFrame(byte[] file) =>
        Encoding.ASCII.GetBytes($"{file.Length:x}\r\n")
            .Concat(file)
            .Concat(Encoding.ASCII.GetBytes("\r\n0\r\nx-amz-checksum-crc32:AbCd1234\r\n\r\n"))
            .ToArray();

    [Fact]
    public void BankFrame_IsExactly44BytesLongerThanThe6265BytePdf()
    {
        var file = File(6265);
        BankFrame(file).Length.ShouldBe(6309);
    }

    [Fact]
    public void IntactObject_IsServedAsIs()
    {
        var file = File(6265);
        var check = StoredFileVerifier.Check(HandleOf(file), file);
        check.State.ShouldBe(StoredFileState.Intact);
        check.Content.ShouldBeSameAs(file);
    }

    [Fact]
    public void FramedObject_IsRecoveredToTheOriginal()
    {
        var file = File(6265);
        var check = StoredFileVerifier.Check(HandleOf(file), BankFrame(file));
        check.State.ShouldBe(StoredFileState.RecoveredFromChunkedFrame);
        check.Content.ShouldBe(file);
    }

    [Fact]
    public void SignedMultiChunkFrame_IsRecovered()
    {
        var file = File(20000);
        var frame = Encoding.ASCII.GetBytes($"{8192:x};chunk-signature=aa\r\n").Concat(file.Take(8192))
            .Concat("\r\n"u8.ToArray())
            .Concat(Encoding.ASCII.GetBytes($"{file.Length - 8192:X};chunk-signature=bb\r\n")).Concat(file.Skip(8192))
            .Concat("\r\n0;chunk-signature=cc\r\n\r\n"u8.ToArray())
            .ToArray();

        var check = StoredFileVerifier.Check(HandleOf(file), frame);

        check.State.ShouldBe(StoredFileState.RecoveredFromChunkedFrame);
        check.Content.ShouldBe(file);
    }

    [Fact]
    public void FrameOfOtherBytes_IsAMismatch()
    {
        var file = File(6265);
        var other = File(6264).Append((byte)7).ToArray();
        StoredFileVerifier.Check(HandleOf(file), BankFrame(other)).State.ShouldBe(StoredFileState.Mismatch);
    }

    [Fact]
    public void TruncatedObject_IsAMismatch()
    {
        var file = File(6265);
        StoredFileVerifier.Check(HandleOf(file), file[..6000]).State.ShouldBe(StoredFileState.Mismatch);
    }

    [Fact]
    public void SameLengthButChangedBytes_IsAMismatch()
    {
        var file = File(100);
        var changed = (byte[])file.Clone();
        changed[50] ^= 0xFF;
        StoredFileVerifier.Check(HandleOf(file), changed).State.ShouldBe(StoredFileState.Mismatch);
    }

    [Theory]
    [InlineData("zz\r\nabc\r\n0\r\n\r\n")]                 // not hex
    [InlineData("3\r\nabc\r\n0\r\n")]                      // trailer block not closed
    [InlineData("3\r\nabc\r\n0\r\n\r\nGARBAGE")]           // bytes after the frame
    [InlineData("5\r\nabc\r\n0\r\n\r\n")]                  // chunk longer than the body
    [InlineData("3\r\nabcX\n0\r\n\r\n")]                   // chunk not closed by CRLF
    [InlineData("3\r\nabc\r\n0\r\nno-colon\r\n\r\n")]      // malformed trailer
    public void MalformedFrames_DoNotDecode(string body)
        => AwsChunkedPayload.TryDecode(Encoding.ASCII.GetBytes(body), out _).ShouldBeFalse();

    [Fact]
    public void FrameWithoutTrailers_Decodes()
    {
        AwsChunkedPayload.TryDecode("3\r\nabc\r\n0\r\n\r\n"u8, out var payload).ShouldBeTrue();
        payload.ShouldBe("abc"u8.ToArray());
    }
}
