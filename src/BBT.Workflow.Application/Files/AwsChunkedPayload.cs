using System.Buffers;

namespace BBT.Workflow.Files;

/// <summary>
/// Decodes an HTTP/1.1 <c>aws-chunked</c> request body (<c>&lt;hex-size&gt;[;ext]\r\n&lt;data&gt;\r\n … 0\r\n[trailers]\r\n</c>),
/// the framing Dapr 1.17+ <c>bindings.aws.s3</c> puts on every upload over TLS
/// (<c>STREAMING-UNSIGNED-PAYLOAD-TRAILER</c>, CRC32 trailer). An S3 server that does not decode the frame stores it
/// as the object, 44+ bytes longer than the file (vnext-client-sdk-core#145). Only a body that is a COMPLETE frame
/// decodes: every chunk header parses, every chunk ends in CRLF, the zero chunk and the trailer block close it, and
/// nothing follows. Whether the decoded bytes are the file is the caller's check (size + sha256 against the handle).
/// </summary>
public static class AwsChunkedPayload
{
    private const int MaxHexDigits = 16;

    /// <summary>True and the decoded payload when <paramref name="body"/> is one complete aws-chunked frame.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> body, out byte[] payload)
    {
        payload = [];
        var output = new ArrayBufferWriter<byte>(body.Length);
        var pos = 0;

        while (true)
        {
            if (!TryReadLine(body, ref pos, out var header) || !TryParseChunkSize(header, out var size))
                return false;

            if (size == 0)
                break;

            if (size > body.Length - pos - 2)
                return false;
            output.Write(body.Slice(pos, (int)size));
            pos += (int)size;
            if (body[pos] != (byte)'\r' || body[pos + 1] != (byte)'\n')
                return false;
            pos += 2;
        }

        // Trailer block: zero or more "name:value" lines, closed by an empty line that must end the body.
        while (true)
        {
            if (!TryReadLine(body, ref pos, out var trailer))
                return false;
            if (trailer.IsEmpty)
                break;
            if (trailer.IndexOf((byte)':') <= 0)
                return false;
        }

        if (pos != body.Length)
            return false;

        payload = output.WrittenSpan.ToArray();
        return true;
    }

    private static bool TryReadLine(ReadOnlySpan<byte> body, ref int pos, out ReadOnlySpan<byte> line)
    {
        line = default;
        if (pos > body.Length)
            return false;
        var rest = body[pos..];
        var lf = rest.IndexOf("\r\n"u8);
        if (lf < 0)
            return false;
        line = rest[..lf];
        pos += lf + 2;
        return true;
    }

    /// <summary>The hex size before an optional <c>;chunk-extension</c> (a signed chunk carries <c>;chunk-signature=…</c>).</summary>
    private static bool TryParseChunkSize(ReadOnlySpan<byte> header, out long size)
    {
        size = 0;
        var semicolon = header.IndexOf((byte)';');
        var hex = semicolon >= 0 ? header[..semicolon] : header;
        if (hex.IsEmpty || hex.Length > MaxHexDigits)
            return false;
        foreach (var b in hex)
        {
            var digit = b switch
            {
                >= (byte)'0' and <= (byte)'9' => b - '0',
                >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
                >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
                _ => -1,
            };
            if (digit < 0)
                return false;
            size = (size << 4) | (uint)digit;
            if (size < 0)
                return false;
        }
        return true;
    }
}
