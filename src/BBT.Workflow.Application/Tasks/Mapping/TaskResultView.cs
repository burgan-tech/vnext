using System.Buffers;
using System.Globalization;
using System.Text.Json;
using BBT.Workflow.Scripting.Rules;
using BBT.Workflow.Shared.Json;

namespace BBT.Workflow.Tasks.Mapping;

/// <summary>
/// The declarative output phase's <c>result</c> — <c>{ body, headers, statusCode }</c> — read without building that
/// object: <c>body</c> is the task's response <c>Data</c> as it already is when that is a <see cref="JsonElement"/> (or
/// <see cref="JsonDocument"/>), otherwise serialized ONCE (else the raw <c>Body</c> text as a JSON string); <c>headers</c>
/// and <c>statusCode</c> are built only when read. A <c>select</c> resolves its first segment here and walks only that
/// member; the whole object is written out only when an expression uses <c>result</c> itself as a value.
/// </summary>
/// <remarks>
/// Scope-lifetime object, one per output mapping, single-threaded. Every element it hands out is borrowed: the documents
/// it creates are disposed by <see cref="Dispose"/>, so values are materialized (<see cref="DeclarativeValues.ToNode"/>)
/// before that. The body is resolved in the constructor, so a response <c>Data</c> that cannot be serialized fails where
/// the old result document failed — before any entry runs.
/// </remarks>
internal sealed class TaskResultView : IRuleJsonObjectSource, IDisposable
{
    public const int BodyIndex = 0;
    public const int HeadersIndex = 1;
    public const int StatusCodeIndex = 2;

    private static readonly string[] Names = ["body", "headers", "statusCode"];

    private static readonly JsonElement NullElement = CreateNullElement();

    private readonly TaskInvocationResult _result;
    private readonly JsonElement _body;
    private readonly JsonDocument? _bodyDocument;
    private JsonDocument? _headersDocument;
    private JsonDocument? _statusDocument;
    private JsonDocument? _rootDocument;

    public TaskResultView(TaskInvocationResult result)
    {
        _result = result;
        switch (result.Data)
        {
            case null when result.Body is null:
                _body = NullElement;
                break;
            case null:
                _bodyDocument = JsonSerializer.SerializeToDocument(result.Body);
                _body = _bodyDocument.RootElement;
                break;
            case JsonElement element:
                // Borrowed from the task result; Undefined (a default element) reads as JSON null, as the
                // SafeJsonElementConverter wrote it before.
                _body = element.ValueKind == JsonValueKind.Undefined ? NullElement : element;
                break;
            case JsonDocument document:
                _body = document.RootElement;
                break;
            default:
                _bodyDocument = JsonSerializer.SerializeToDocument(
                    result.Data, result.Data.GetType(), JsonSerializerConstants.JsonOptions);
                _body = _bodyDocument.RootElement;
                break;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> MemberNames => Names;

    /// <summary>The index of member <paramref name="name"/> (ordinal), or -1.</summary>
    public static int IndexOf(string name) => Array.IndexOf(Names, name);

    /// <inheritdoc />
    public JsonElement GetMember(int index) => index switch
    {
        BodyIndex => _body,
        HeadersIndex => Headers(),
        StatusCodeIndex => StatusCode(),
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    /// <inheritdoc />
    public JsonElement Materialize()
    {
        if (_rootDocument is not null)
            return _rootDocument.RootElement;

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            for (var i = 0; i < Names.Length; i++)
            {
                writer.WritePropertyName(Names[i]);
                GetMember(i).WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        _rootDocument = JsonDocument.Parse(buffer.WrittenMemory);
        return _rootDocument.RootElement;
    }

    /// <summary>
    /// Reads <paramref name="selector"/> from this result: the member it starts with, then the rest of the path
    /// inside that member — the same answer <see cref="JsonPath.TryRead(JsonElement, out JsonElement)"/> gives over
    /// the whole <c>{ body, headers, statusCode }</c> object.
    /// </summary>
    public bool TryRead(Selector selector, out JsonElement value)
    {
        if (selector.IsRoot)
        {
            value = Materialize();
            return true;
        }

        if (selector.Member < 0)
        {
            value = default;
            return false;
        }

        return selector.Rest!.TryRead(GetMember(selector.Member), out value);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _rootDocument?.Dispose();
        _statusDocument?.Dispose();
        _headersDocument?.Dispose();
        _bodyDocument?.Dispose();
    }

    private JsonElement Headers()
    {
        if (_result.Headers is null)
            return NullElement;

        _headersDocument ??= JsonSerializer.SerializeToDocument(_result.Headers);
        return _headersDocument.RootElement;
    }

    private JsonElement StatusCode()
    {
        if (_result.StatusCode is not { } status)
            return NullElement;

        _statusDocument ??= JsonDocument.Parse(status.ToString(CultureInfo.InvariantCulture));
        return _statusDocument.RootElement;
    }

    private static JsonElement CreateNullElement()
    {
        using var document = JsonDocument.Parse("null");
        return document.RootElement.Clone();
    }

    /// <summary>
    /// An output <c>select</c> path split at its first segment: the root (<c>$</c>, the whole object), a member of
    /// <c>result</c> plus the path inside it, or (<see cref="Member"/> -1) a path that cannot resolve — a first segment
    /// that is not one of the three member names (case-sensitive, as the path reader is) or is an index / wildcard.
    /// </summary>
    internal sealed record Selector(bool IsRoot, int Member, JsonPath? Rest)
    {
        public static Selector For(JsonPath path)
        {
            if (path.IsRoot)
                return new Selector(true, -1, null);

            var member = path.FirstSegment.Length == 0 ? -1 : IndexOf(path.FirstSegment);
            return member < 0
                ? new Selector(false, -1, null)
                : new Selector(false, member, path.WithoutFirstSegment());
        }
    }
}
