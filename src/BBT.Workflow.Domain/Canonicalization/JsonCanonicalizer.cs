using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace BBT.Workflow.Canonicalization;

/// <summary>
/// Canonical JSON for representation ETags: object members sorted by UTF-16 ordinal name (a repeated
/// name keeps its LAST value), arrays positional, numbers re-formatted from their double value
/// (round-trip <c>G17</c>), strings written with a minimal fixed escape set, no whitespace.
/// <para>
/// Single pass: the UTF-8 input is parsed once into a <see cref="JsonDocument"/> and the canonical
/// form is written straight to an <see cref="IBufferWriter{T}"/> as UTF-8 — no intermediate object
/// tree, string or regex. Output is byte-identical to the previous hand-written parser for every
/// input that parser handled correctly; the two cases it did not are fixed (escaped <c>\\</c>,
/// <c>\"</c>, <c>\/</c> were dropped together with the following character; a top-level array was
/// never consumed).
/// </para>
/// <para>Stateless and thread-safe; registered as a singleton.</para>
/// </summary>
public sealed class JsonCanonicalizer : IJsonCanonicalizer
{
    /// <summary>
    /// Response DTOs are serialized with <c>JsonSerializerConstants.JsonOptions.MaxDepth = 256</c>; the
    /// previous parser had no depth limit at all. 1024 keeps every serializable body canonicalizable.
    /// </summary>
    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = 1024 };

    /// <summary>Same replacement semantics the old path got from <c>Encoding.UTF8.GetBytes</c>.</summary>
    private static readonly Encoding Utf8 = Encoding.UTF8;

    private static ReadOnlySpan<byte> HexDigits => "0123456789abcdef"u8;

    /// <inheritdoc />
    public string Canonicalize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var input = ArrayPool<byte>.Shared.Rent(Utf8.GetMaxByteCount(json.Length));
        try
        {
            var length = Utf8.GetBytes(json, input);
            using var output = new PooledByteBufferWriter(Math.Max(256, length));
            Canonicalize(input.AsMemory(0, length), output);
            return Utf8.GetString(output.WrittenSpan);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(input);
        }
    }

    /// <inheritdoc />
    public void Canonicalize(ReadOnlyMemory<byte> utf8Json, IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);

        using var document = JsonDocument.Parse(utf8Json, DocumentOptions);
        var scratch = new CharScratch();
        try
        {
            WriteValue(document.RootElement, output, ref scratch);
        }
        finally
        {
            scratch.Dispose();
        }
    }

    private static void WriteValue(JsonElement element, IBufferWriter<byte> output, ref CharScratch scratch)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                WriteObject(element, output, ref scratch);
                break;
            case JsonValueKind.Array:
            {
                WriteByte(output, (byte)'[');
                var next = false;
                foreach (var item in element.EnumerateArray())
                {
                    if (next) WriteByte(output, (byte)',');
                    next = true;
                    WriteValue(item, output, ref scratch);
                }
                WriteByte(output, (byte)']');
                break;
            }
            case JsonValueKind.String:
                WriteString(JsonMarshal.GetRawUtf8Value(element), output, ref scratch);
                break;
            case JsonValueKind.Number:
                WriteNumber(JsonMarshal.GetRawUtf8Value(element), output);
                break;
            case JsonValueKind.True:
                output.Write("true"u8);
                break;
            case JsonValueKind.False:
                output.Write("false"u8);
                break;
            case JsonValueKind.Null:
                output.Write("null"u8);
                break;
            default:
                throw new InvalidOperationException("Unexpected JSON value kind: " + element.ValueKind);
        }
    }

    /// <summary>
    /// One object level. Member names are decoded to UTF-16 into the shared scratch buffer and sorted
    /// with ordinal UTF-16 comparison (= <see cref="StringComparer.Ordinal"/>, the old
    /// <c>SortedDictionary</c> comparer — NOT UTF-8 byte order, which disagrees for supplementary
    /// characters vs U+E000..U+FFFF). Equal names: the one appearing last in the document wins.
    /// </summary>
    private static void WriteObject(JsonElement element, IBufferWriter<byte> output, ref CharScratch scratch)
    {
        var count = element.GetPropertyCount();
        if (count == 0)
        {
            output.Write("{}"u8);
            return;
        }

        var members = ArrayPool<Member>.Shared.Rent(count);
        var scratchMark = scratch.Length;
        try
        {
            var n = 0;
            foreach (var property in element.EnumerateObject())
            {
                var start = scratch.Length;
                scratch.AppendJsonString(JsonMarshal.GetRawUtf8PropertyName(property));
                members[n] = new Member(start, scratch.Length - start, n, property.Value);
                n++;
            }

            var sorted = members.AsSpan(0, n);
            sorted.Sort(new MemberComparer(scratch.Buffer));

            WriteByte(output, (byte)'{');
            var first = true;
            for (var i = 0; i < sorted.Length; i++)
            {
                ref readonly var member = ref sorted[i];
                // Equal names sort adjacent by document index; only the last of a run is written.
                if (i + 1 < sorted.Length && NameEquals(scratch.Buffer, member, sorted[i + 1])) continue;

                if (!first) WriteByte(output, (byte)',');
                first = false;
                WriteEscaped(scratch.Buffer.AsSpan(member.NameStart, member.NameLength), output);
                WriteByte(output, (byte)':');
                WriteValue(member.Value, output, ref scratch);
            }
            WriteByte(output, (byte)'}');
        }
        finally
        {
            ArrayPool<Member>.Shared.Return(members, clearArray: true);
            scratch.Length = scratchMark;
        }
    }

    private static bool NameEquals(char[] buffer, in Member a, in Member b) =>
        buffer.AsSpan(a.NameStart, a.NameLength).SequenceEqual(buffer.AsSpan(b.NameStart, b.NameLength));

    /// <summary>
    /// <paramref name="raw"/> is the token as it appears in the document, quotes included. Without a
    /// backslash its content is copied verbatim: valid JSON cannot carry a raw control character or
    /// quote inside a string, and non-ASCII text is literal UTF-8 in both the input and the canonical
    /// form. With a backslash it is decoded and re-escaped with the canonical escape set.
    /// </summary>
    private static void WriteString(ReadOnlySpan<byte> raw, IBufferWriter<byte> output, ref CharScratch scratch)
    {
        var content = raw[1..^1];
        if (content.IndexOf((byte)'\\') < 0)
        {
            output.Write(raw);
            return;
        }

        var start = scratch.Length;
        scratch.AppendJsonString(content);
        WriteEscaped(scratch.Buffer.AsSpan(start, scratch.Length - start), output);
        scratch.Length = start;
    }

    /// <summary>
    /// Canonical string escape (unchanged from the previous implementation): <c>\n \b \f \r \t \" \\</c>
    /// as two-character escapes, any other char below U+0020 as <c>\u00xx</c> (lowercase hex), every
    /// other char literal — including non-ASCII, <c>/</c>, U+007F and HTML-sensitive characters. Lone
    /// surrogates become U+FFFD, exactly as <c>Encoding.UTF8.GetBytes</c> did on the old string.
    /// </summary>
    private static void WriteEscaped(ReadOnlySpan<char> value, IBufferWriter<byte> output)
    {
        WriteByte(output, (byte)'"');
        var runStart = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c >= ' ' && c != '"' && c != '\\') continue;

            // Escapes are ASCII, so a literal run never splits a surrogate pair.
            WriteLiteral(value[runStart..i], output);
            runStart = i + 1;
            switch (c)
            {
                case '\n': output.Write("\\n"u8); break;
                case '\b': output.Write("\\b"u8); break;
                case '\f': output.Write("\\f"u8); break;
                case '\r': output.Write("\\r"u8); break;
                case '\t': output.Write("\\t"u8); break;
                case '"': output.Write("\\\""u8); break;
                case '\\': output.Write("\\\\"u8); break;
                default:
                {
                    var span = output.GetSpan(6);
                    span[0] = (byte)'\\';
                    span[1] = (byte)'u';
                    span[2] = (byte)'0';
                    span[3] = (byte)'0';
                    span[4] = HexDigits[c >> 4];
                    span[5] = HexDigits[c & 0xF];
                    output.Advance(6);
                    break;
                }
            }
        }
        WriteLiteral(value[runStart..], output);
        WriteByte(output, (byte)'"');
    }

    private static void WriteLiteral(ReadOnlySpan<char> chars, IBufferWriter<byte> output)
    {
        if (chars.IsEmpty) return;
        var span = output.GetSpan(Utf8.GetMaxByteCount(chars.Length));
        output.Advance(Utf8.GetBytes(chars, span));
    }

    /// <summary>
    /// Numbers are canonicalized from their double value: <c>value.ToString("G17", Invariant)</c>, NaN as
    /// <c>null</c>, ±Infinity (an out-of-range literal such as <c>1e400</c>) as <c>±1e+400</c> — the exact
    /// output of the previous ES6-style serializer, whose integral branch only ever returned a string
    /// equal to the G17 one. Fast path: an integer token of at most 15 digits is exactly representable
    /// and G17 prints it unchanged (JSON forbids leading zeros), so its raw text is copied.
    /// </summary>
    private static void WriteNumber(ReadOnlySpan<byte> raw, IBufferWriter<byte> output)
    {
        var digits = raw[0] == (byte)'-' ? raw[1..] : raw;
        if (digits.Length <= 15 && digits.IndexOfAnyExceptInRange((byte)'0', (byte)'9') < 0)
        {
            output.Write(raw);
            return;
        }

        var value = double.Parse(raw, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (double.IsNaN(value))
        {
            output.Write("null"u8);
            return;
        }
        if (double.IsPositiveInfinity(value))
        {
            output.Write("1e+400"u8);
            return;
        }
        if (double.IsNegativeInfinity(value))
        {
            output.Write("-1e+400"u8);
            return;
        }

        var span = output.GetSpan(32);
        if (!value.TryFormat(span, out var written, "G17", CultureInfo.InvariantCulture))
            throw new InvalidOperationException("Number formatting exceeded the canonical buffer.");
        output.Advance(written);
    }

    private static void WriteByte(IBufferWriter<byte> output, byte value)
    {
        output.GetSpan(1)[0] = value;
        output.Advance(1);
    }

    private readonly record struct Member(int NameStart, int NameLength, int Index, JsonElement Value);

    private readonly struct MemberComparer(char[] buffer) : IComparer<Member>
    {
        public int Compare(Member x, Member y)
        {
            var byName = buffer.AsSpan(x.NameStart, x.NameLength)
                .SequenceCompareTo(buffer.AsSpan(y.NameStart, y.NameLength));
            return byName != 0 ? byName : x.Index.CompareTo(y.Index);
        }
    }

    /// <summary>
    /// Per-call UTF-16 scratch (pooled): holds decoded member names of the object levels currently on
    /// the stack, plus one decoded escaped string at a time. Levels truncate back to their mark.
    /// </summary>
    private struct CharScratch : IDisposable
    {
        public char[] Buffer;
        public int Length;

        public CharScratch()
        {
            Buffer = ArrayPool<char>.Shared.Rent(256);
            Length = 0;
        }

        /// <summary>
        /// Appends the decoded value of a JSON string body (no surrounding quotes). Escapes are decoded
        /// here rather than by <see cref="JsonElement.GetString"/> so a lone-surrogate <c>\uXXXX</c> stays a
        /// lone UTF-16 unit (later written as U+FFFD, as before) instead of throwing.
        /// </summary>
        public void AppendJsonString(ReadOnlySpan<byte> body)
        {
            // Decoded UTF-16 length never exceeds the UTF-8 byte length.
            EnsureCapacity(body.Length);

            var backslash = body.IndexOf((byte)'\\');
            if (backslash < 0)
            {
                Length += Utf8.GetChars(body, Buffer.AsSpan(Length));
                return;
            }

            while (true)
            {
                Length += Utf8.GetChars(body[..backslash], Buffer.AsSpan(Length));
                body = body[(backslash + 1)..];
                var escape = body[0];
                char c;
                var consumed = 1;
                switch (escape)
                {
                    case (byte)'"': c = '"'; break;
                    case (byte)'\\': c = '\\'; break;
                    case (byte)'/': c = '/'; break;
                    case (byte)'b': c = '\b'; break;
                    case (byte)'f': c = '\f'; break;
                    case (byte)'n': c = '\n'; break;
                    case (byte)'r': c = '\r'; break;
                    case (byte)'t': c = '\t'; break;
                    case (byte)'u':
                        if (!Utf8Parser.TryParse(body.Slice(1, 4), out ushort code, out var used, 'X') || used != 4)
                            throw new InvalidOperationException("Bad hex in \\u escape.");
                        c = (char)code;
                        consumed = 5;
                        break;
                    default:
                        throw new InvalidOperationException("Unsupported escape.");
                }
                Buffer[Length++] = c;
                body = body[consumed..];

                backslash = body.IndexOf((byte)'\\');
                if (backslash < 0)
                {
                    Length += Utf8.GetChars(body, Buffer.AsSpan(Length));
                    return;
                }
            }
        }

        private void EnsureCapacity(int additional)
        {
            if (Length + additional <= Buffer.Length) return;
            var larger = ArrayPool<char>.Shared.Rent(Math.Max(Buffer.Length * 2, Length + additional));
            Buffer.AsSpan(0, Length).CopyTo(larger);
            ArrayPool<char>.Shared.Return(Buffer);
            Buffer = larger;
        }

        public void Dispose()
        {
            if (Buffer is null) return;
            ArrayPool<char>.Shared.Return(Buffer);
            Buffer = null!;
        }
    }
}
