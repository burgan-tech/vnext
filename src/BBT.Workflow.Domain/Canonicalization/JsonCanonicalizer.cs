using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BBT.Workflow.Canonicalization;

public sealed class JsonCanonicalizer : IJsonCanonicalizer
{
    private readonly StringBuilder _buffer = new();

    /// <summary>
    /// Parameterless constructor for DI. Use <see cref="Canonicalize"/> to canonicalize JSON.
    /// </summary>
    public JsonCanonicalizer()
    {
    }

    /// <summary>
    /// Produces a canonical JSON string from the given JSON. One instance per scope (e.g. per request).
    /// </summary>
    public string Canonicalize(string jsonData)
    {
        _buffer.Clear();
        Serialize(new JsonDecoder(jsonData).Root);
        return _buffer.ToString();
    }

    /// <summary>
    /// Legacy entry point for one-shot canonicalization; prefer injecting <see cref="IJsonCanonicalizer"/> and calling <see cref="Canonicalize"/>.
    /// </summary>
    public JsonCanonicalizer(string jsonData)
    {
        Serialize(new JsonDecoder(jsonData).Root);
    }

    /// <summary>
    /// Legacy entry point for one-shot canonicalization from UTF-8 bytes.
    /// </summary>
    public JsonCanonicalizer(byte[] jsonData)
        : this(new UTF8Encoding(false, true).GetString(jsonData))
    {
    }

    public string GetEncodedString() => _buffer.ToString();

    public byte[] GetEncodedUTF8() => new UTF8Encoding(false, true).GetBytes(GetEncodedString());

    private void Escape(char c) => _buffer.Append('\\').Append(c);

    private void SerializeString(string value)
    {
        _buffer.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '\n': Escape('n'); break;
                case '\b': Escape('b'); break;
                case '\f': Escape('f'); break;
                case '\r': Escape('r'); break;
                case '\t': Escape('t'); break;
                case '"':
                case '\\': Escape(c); break;
                default:
                    if (c < ' ')
                        _buffer.Append("\\u").Append(((int)c).ToString("x4"));
                    else
                        _buffer.Append(c);
                    break;
            }
        }
        _buffer.Append('"');
    }

    private void Serialize(object? o)
    {
        if (o is SortedDictionary<string, object> dict)
        {
            _buffer.Append('{');
            var next = false;
            foreach (var kv in dict)
            {
                if (next) _buffer.Append(',');
                next = true;
                SerializeString(kv.Key);
                _buffer.Append(':');
                Serialize(kv.Value);
            }
            _buffer.Append('}');
        }
        else if (o is List<object> list)
        {
            _buffer.Append('[');
            var next = false;
            foreach (var value in list)
            {
                if (next) _buffer.Append(',');
                next = true;
                Serialize(value);
            }
            _buffer.Append(']');
        }
        else if (o == null)
        {
            _buffer.Append("null");
        }
        else if (o is string s)
        {
            SerializeString(s);
        }
        else if (o is bool b)
        {
            _buffer.Append(b ? "true" : "false");
        }
        else if (o is double d)
        {
            _buffer.Append(Es6NumberSerialization.SerializeNumber(d));
        }
        else
        {
            throw new InvalidOperationException("Unknown object type: " + o.GetType().FullName);
        }
    }

    /// <summary>
    /// ES6-style number serialization for canonical JSON (deterministic, no unnecessary decimals).
    /// </summary>
    private static class Es6NumberSerialization
    {
        public static string SerializeNumber(double value)
        {
            if (double.IsNaN(value)) return "null";
            if (double.IsPositiveInfinity(value)) return "1e+400";
            if (double.IsNegativeInfinity(value)) return "-1e+400";
            var s = value.ToString("G17", CultureInfo.InvariantCulture);
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var roundTrip) &&
                Math.Abs(roundTrip - value) < double.Epsilon &&
                Math.Truncate(value) == value && value >= long.MinValue && value <= long.MaxValue)
            {
                var l = (long)value;
                if (l.ToString(CultureInfo.InvariantCulture) == s.TrimEnd('0').TrimEnd('.'))
                    return l.ToString(CultureInfo.InvariantCulture);
            }
            return s;
        }
    }

    private class JsonDecoder
    {
        private const char LeftCurly = '{';
        private const char RightCurly = '}';
        private const char Quote = '"';
        private const char Colon = ':';
        private const char LeftBracket = '[';
        private const char RightBracket = ']';
        private const char Comma = ',';
        private const char Backslash = '\\';

        private static readonly Regex NumberPattern = new(@"^-?[0-9]+(\.[0-9]+)?([eE][-+]?[0-9]+)?$", RegexOptions.Compiled);
        private static readonly Regex BooleanPattern = new(@"^true|false$", RegexOptions.Compiled);

        private int _index;
        private readonly string _jsonData;

        internal object? Root { get; }

        internal JsonDecoder(string jsonData)
        {
            _jsonData = jsonData;
            if (TestNextNonWhiteSpaceChar() == LeftBracket)
                Root = ParseArray();
            else
            {
                ScanFor(LeftCurly);
                Root = ParseObject();
            }
            while (_index < _jsonData.Length)
            {
                if (!IsWhiteSpace(_jsonData[_index++]))
                    throw new InvalidOperationException("Improperly terminated JSON object");
            }
        }

        private object? ParseElement()
        {
            return Scan() switch
            {
                LeftCurly => ParseObject(),
                Quote => ParseQuotedString(),
                LeftBracket => ParseArray(),
                _ => ParseSimpleType()
            };
        }

        private SortedDictionary<string, object> ParseObject()
        {
            var dict = new SortedDictionary<string, object>(StringComparer.Ordinal);
            var next = false;
            while (TestNextNonWhiteSpaceChar() != RightCurly)
            {
                if (next) ScanFor(Comma);
                next = true;
                ScanFor(Quote);
                var name = ParseQuotedString();
                ScanFor(Colon);
                dict.Add(name, ParseElement()!);
            }
            Scan();
            return dict;
        }

        private List<object> ParseArray()
        {
            var list = new List<object>();
            var next = false;
            while (TestNextNonWhiteSpaceChar() != RightBracket)
            {
                if (next) ScanFor(Comma);
                else next = true;
                list.Add(ParseElement()!);
            }
            Scan();
            return list;
        }

        private object? ParseSimpleType()
        {
            _index--;
            var temp = new StringBuilder();
            char c;
            while ((c = TestNextNonWhiteSpaceChar()) != Comma && c != RightBracket && c != RightCurly)
            {
                if (IsWhiteSpace(NextChar())) break;
                temp.Append(_jsonData[_index - 1]);
            }
            var token = temp.ToString();
            if (token.Length == 0) throw new InvalidOperationException("Missing argument");
            if (NumberPattern.IsMatch(token))
                return double.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture);
            if (BooleanPattern.IsMatch(token))
                return bool.Parse(token);
            if (token == "null") return null;
            throw new InvalidOperationException("Unrecognized or malformed JSON token: " + token);
        }

        private string ParseQuotedString()
        {
            var result = new StringBuilder();
            while (true)
            {
                var c = NextChar();
                if (c < ' ')
                    throw new InvalidOperationException(c == '\n' ? "Unterminated string literal" : "Unescaped control character");
                if (c == Quote) break;
                if (c == Backslash)
                {
                    c = NextChar() switch
                    {
                        '"' or '\\' or '/' => NextChar(),
                        'b' => '\b',
                        'f' => '\f',
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        'u' => ParseUnicodeEscape(),
                        _ => throw new InvalidOperationException("Unsupported escape")
                    };
                }
                result.Append(c);
            }
            return result.ToString();
        }

        private char ParseUnicodeEscape()
        {
            var c = (char)0;
            for (var i = 0; i < 4; i++)
                c = (char)((c << 4) + GetHexChar());
            return c;
        }

        private char GetHexChar()
        {
            var c = NextChar();
            return c switch
            {
                >= '0' and <= '9' => (char)(c - '0'),
                >= 'a' and <= 'f' => (char)(c - 'a' + 10),
                >= 'A' and <= 'F' => (char)(c - 'A' + 10),
                _ => throw new InvalidOperationException("Bad hex in \\u escape: " + c)
            };
        }

        private char TestNextNonWhiteSpaceChar()
        {
            var save = _index;
            var c = Scan();
            _index = save;
            return c;
        }

        private void ScanFor(char expected)
        {
            var c = Scan();
            if (c != expected)
                throw new InvalidOperationException($"Expected '{expected}' but got '{c}'");
        }

        private char NextChar()
        {
            if (_index < _jsonData.Length)
                return _jsonData[_index++];
            throw new InvalidOperationException("Unexpected EOF reached");
        }

        private static bool IsWhiteSpace(char c) => c is ' ' or '\n' or '\r' or '\t';

        private char Scan()
        {
            while (true)
            {
                var c = NextChar();
                if (!IsWhiteSpace(c)) return c;
            }
        }
    }
}
