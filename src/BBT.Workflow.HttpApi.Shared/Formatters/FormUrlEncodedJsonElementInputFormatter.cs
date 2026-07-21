using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Net.Http.Headers;

namespace BBT.Workflow.Formatters;

/// <summary>
/// Converts <c>application/x-www-form-urlencoded</c> request bodies to the
/// <see cref="JsonElement"/> shape consumed by Orchestration endpoints.
/// </summary>
/// <remarks>
/// Supports nested bracket paths, trailing scalar arrays, and indexed object arrays. Ambiguous
/// or malformed paths are returned as model-binding failures instead of being silently rewritten.
/// Payload leaves follow JSON-scalar semantics; standard envelope fields remain strings.
/// See <c>docs/contracts/form-url-encoded-payloads.md</c> for the public request contract.
/// </remarks>
public sealed class FormUrlEncodedJsonElementInputFormatter : TextInputFormatter
{
    private const string PayloadModeHeader = "x-vnext-payload-mode";

    /// <summary>
    /// Upper bound for a bracket array index. Arrays must use contiguous indices and cannot exceed
    /// this size, so any index above the limit is guaranteed invalid. Rejecting it during parsing
    /// prevents an attacker from forcing large intermediate allocations (e.g. <c>items[2000000000]=1</c>)
    /// before the sparse-array validation would reject it.
    /// </summary>
    private const int MaxArrayIndex = 1024;

    public FormUrlEncodedJsonElementInputFormatter()
    {
        SupportedMediaTypes.Add(MediaTypeHeaderValue.Parse("application/x-www-form-urlencoded"));
        SupportedEncodings.Add(Encoding.UTF8);
        SupportedEncodings.Add(Encoding.Unicode);
    }

    protected override bool CanReadType(Type type)
        => type == typeof(JsonElement) || type == typeof(JsonElement?);

    public override async Task<InputFormatterResult> ReadRequestBodyAsync(
        InputFormatterContext context,
        Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(encoding);

        try
        {
            var request = context.HttpContext.Request;
            var form = await request.ReadFormAsync(request.HttpContext.RequestAborted);
            var fields = ParseFields(form);
            var standardPayload = IsStandardPayload(request, fields);
            var root = new ObjectNode();

            foreach (var field in fields)
            {
                foreach (var rawValue in field.Values)
                {
                    var path = standardPayload
                        ? NormalizeStandardEnvelopePath(field.Path)
                        : field.Path;
                    var preserveString = standardPayload && IsStandardEnvelopePath(path);
                    var value = ParseScalar(rawValue ?? string.Empty, preserveString);
                    Insert(root, path, value, field.OriginalKey);
                }
            }

            ValidateComplete(root);

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                WriteNode(writer, root);
            }

            using var document = JsonDocument.Parse(buffer.ToArray());
            return InputFormatterResult.Success(document.RootElement.Clone());
        }
        catch (FormUrlEncodedInputException exception)
        {
            context.ModelState.TryAddModelError(context.ModelName, exception.Message);
            return await InputFormatterResult.FailureAsync();
        }
    }

    private static List<ParsedField> ParseFields(IFormCollection form)
    {
        var fields = new List<ParsedField>(form.Count);
        foreach (var field in form)
        {
            fields.Add(new ParsedField(field.Key, ParsePath(field.Key), field.Value));
        }

        return fields;
    }

    private static IReadOnlyList<PathSegment> ParsePath(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            throw InvalidPath(key, "The form key cannot be empty.");
        }

        var firstBracket = key.IndexOf('[');
        var rootLength = firstBracket < 0 ? key.Length : firstBracket;
        var root = key[..rootLength];
        if (root.Length == 0 || root.Contains(']'))
        {
            throw InvalidPath(key, "The root property is malformed.");
        }

        var segments = new List<PathSegment> { new PropertySegment(root) };
        var position = rootLength;
        while (position < key.Length)
        {
            if (key[position] != '[')
            {
                throw InvalidPath(key, "Unexpected characters follow a bracket segment.");
            }

            var close = key.IndexOf(']', position + 1);
            if (close < 0)
            {
                throw InvalidPath(key, "A bracket segment is not closed.");
            }

            var inner = key[(position + 1)..close];
            if (inner.Contains('[') || inner.Contains(']'))
            {
                throw InvalidPath(key, "A bracket segment is malformed.");
            }

            if (inner.Length == 0)
            {
                if (close != key.Length - 1)
                {
                    throw InvalidPath(key, "Empty brackets are supported only for trailing scalar arrays.");
                }

                segments.Add(new AppendSegment());
            }
            else if (inner[0] == '-' && inner[1..].All(char.IsDigit))
            {
                throw InvalidPath(key, "Array indices cannot be negative.");
            }
            else if (inner.All(char.IsDigit))
            {
                if (!int.TryParse(inner, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                {
                    throw InvalidPath(key, "The array index is too large.");
                }

                if (index > MaxArrayIndex)
                {
                    throw InvalidPath(
                        key,
                        $"The array index exceeds the maximum supported value ({MaxArrayIndex}).");
                }

                segments.Add(new IndexSegment(index));
            }
            else
            {
                segments.Add(new PropertySegment(inner));
            }

            position = close + 1;
        }

        return segments;
    }

    private static bool IsStandardPayload(HttpRequest request, IReadOnlyList<ParsedField> fields)
    {
        if (request.Headers.TryGetValue(PayloadModeHeader, out var mode))
        {
            return !string.Equals(mode.ToString(), "raw", StringComparison.OrdinalIgnoreCase);
        }

        return fields.Any(field =>
            field.Path[0] is PropertySegment { Name: "attributes" });
    }

    private static bool IsStandardEnvelopePath(IReadOnlyList<PathSegment> path)
        => path[0] is PropertySegment property &&
           (property.Name.Equals("key", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Equals("stage", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Equals("tags", StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<PathSegment> NormalizeStandardEnvelopePath(
        IReadOnlyList<PathSegment> path)
    {
        if (path.Count == 1 &&
            path[0] is PropertySegment property &&
            property.Name.Equals("tags", StringComparison.OrdinalIgnoreCase))
        {
            return [property, new AppendSegment()];
        }

        return path;
    }

    private static ScalarNode ParseScalar(string value, bool preserveString)
    {
        if (preserveString)
        {
            return new ScalarNode(value);
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            return document.RootElement.ValueKind switch
            {
                JsonValueKind.String => new ScalarNode(document.RootElement.GetString()),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
                    => new ScalarNode(document.RootElement.Clone()),
                JsonValueKind.Null => new ScalarNode(null),
                _ => throw new FormUrlEncodedInputException(
                    "JSON objects and arrays are not supported as scalar form values; use bracket paths.")
            };
        }
        catch (JsonException)
        {
            return new ScalarNode(value);
        }
    }

    private static void Insert(
        ObjectNode root,
        IReadOnlyList<PathSegment> path,
        ScalarNode value,
        string originalKey)
        => InsertIntoObject(root, path, 0, value, originalKey);

    private static void InsertIntoObject(
        ObjectNode current,
        IReadOnlyList<PathSegment> path,
        int position,
        ScalarNode value,
        string originalKey)
    {
        if (path[position] is not PropertySegment property)
        {
            throw InvalidPath(originalKey, "An object path requires a property segment.");
        }

        if (position == path.Count - 1)
        {
            AddScalar(current.Properties, property.Name, value, originalKey);
            return;
        }

        var expected = CreateContainer(path[position + 1]);
        if (!current.Properties.TryGetValue(property.Name, out var child))
        {
            child = expected;
            current.Properties.Add(property.Name, child);
        }
        else if (child.GetType() != expected.GetType())
        {
            throw Collision(originalKey, property.Name);
        }

        InsertIntoNode(child, path, position + 1, value, originalKey);
    }

    private static void InsertIntoNode(
        Node current,
        IReadOnlyList<PathSegment> path,
        int position,
        ScalarNode value,
        string originalKey)
    {
        switch (current)
        {
            case ObjectNode obj:
                InsertIntoObject(obj, path, position, value, originalKey);
                break;
            case ArrayNode array:
                InsertIntoArray(array, path, position, value, originalKey);
                break;
            case ScalarCollectionNode collection when path[position] is AppendSegment && position == path.Count - 1:
                collection.Values.Add(value);
                break;
            default:
                throw Collision(originalKey, path[position].ToString());
        }
    }

    private static void InsertIntoArray(
        ArrayNode current,
        IReadOnlyList<PathSegment> path,
        int position,
        ScalarNode value,
        string originalKey)
    {
        if (path[position] is not IndexSegment indexSegment)
        {
            throw InvalidPath(originalKey, "An indexed array requires a numeric index.");
        }

        while (current.Items.Count < indexSegment.Index)
        {
            current.Items.Add(MissingNode.Instance);
        }

        if (position == path.Count - 1)
        {
            if (indexSegment.Index == current.Items.Count)
            {
                current.Items.Add(value);
                return;
            }

            current.Items[indexSegment.Index] = current.Items[indexSegment.Index] is MissingNode
                ? value
                : PromoteScalar(current.Items[indexSegment.Index], value, originalKey);
            return;
        }

        var expected = CreateContainer(path[position + 1]);
        Node child;
        if (indexSegment.Index == current.Items.Count)
        {
            child = expected;
            current.Items.Add(child);
        }
        else
        {
            child = current.Items[indexSegment.Index];
            if (child is MissingNode)
            {
                child = expected;
                current.Items[indexSegment.Index] = child;
            }
            else if (child.GetType() != expected.GetType())
            {
                throw Collision(originalKey, indexSegment.Index.ToString(CultureInfo.InvariantCulture));
            }
        }

        InsertIntoNode(child, path, position + 1, value, originalKey);
    }

    private static Node CreateContainer(PathSegment next)
        => next switch
        {
            PropertySegment => new ObjectNode(),
            IndexSegment => new ArrayNode(),
            AppendSegment => new ScalarCollectionNode(),
            _ => throw new InvalidOperationException($"Unknown path segment {next.GetType().Name}.")
        };

    private static void AddScalar(
        Dictionary<string, Node> properties,
        string key,
        ScalarNode value,
        string originalKey)
    {
        if (!properties.TryGetValue(key, out var existing))
        {
            properties.Add(key, value);
            return;
        }

        properties[key] = PromoteScalar(existing, value, originalKey);
    }

    private static Node PromoteScalar(Node existing, ScalarNode value, string originalKey)
    {
        switch (existing)
        {
            case ScalarNode scalar:
                return new ScalarCollectionNode { Values = { scalar, value } };
            case ScalarCollectionNode collection:
                collection.Values.Add(value);
                return collection;
            default:
                throw Collision(originalKey, originalKey);
        }
    }

    private static void WriteNode(Utf8JsonWriter writer, Node node)
    {
        switch (node)
        {
            case ObjectNode obj:
                writer.WriteStartObject();
                foreach (var (key, value) in obj.Properties)
                {
                    writer.WritePropertyName(key);
                    WriteNode(writer, value);
                }

                writer.WriteEndObject();
                break;
            case ArrayNode array:
                writer.WriteStartArray();
                foreach (var value in array.Items)
                {
                    WriteNode(writer, value);
                }

                writer.WriteEndArray();
                break;
            case ScalarCollectionNode collection:
                writer.WriteStartArray();
                foreach (var value in collection.Values)
                {
                    WriteNode(writer, value);
                }

                writer.WriteEndArray();
                break;
            case ScalarNode { Value: JsonElement element }:
                element.WriteTo(writer);
                break;
            case ScalarNode { Value: string text }:
                writer.WriteStringValue(text);
                break;
            case ScalarNode:
                writer.WriteNullValue();
                break;
            default:
                throw new InvalidOperationException($"Unknown form node {node.GetType().Name}.");
        }
    }

    private static void ValidateComplete(Node node)
    {
        switch (node)
        {
            case ObjectNode obj:
                foreach (var child in obj.Properties.Values)
                {
                    ValidateComplete(child);
                }

                break;
            case ArrayNode array when array.Items.Any(item => item is MissingNode):
                throw new FormUrlEncodedInputException("Sparse array indices are not supported.");
            case ArrayNode array:
                foreach (var child in array.Items)
                {
                    ValidateComplete(child);
                }

                break;
        }
    }

    private static FormUrlEncodedInputException InvalidPath(string key, string reason)
        => new($"Invalid form key '{key}': {reason}");

    private static FormUrlEncodedInputException Collision(string key, string segment)
        => InvalidPath(key, $"The path segment '{segment}' conflicts with an existing scalar or container.");

    private sealed record ParsedField(
        string OriginalKey,
        IReadOnlyList<PathSegment> Path,
        IReadOnlyList<string?> Values);

    private abstract record PathSegment;
    private sealed record PropertySegment(string Name) : PathSegment;
    private sealed record IndexSegment(int Index) : PathSegment;
    private sealed record AppendSegment : PathSegment;

    private abstract class Node;

    private sealed class ObjectNode : Node
    {
        public Dictionary<string, Node> Properties { get; } = new(StringComparer.Ordinal);
    }

    private sealed class ArrayNode : Node
    {
        public List<Node> Items { get; } = [];
    }

    private sealed class ScalarCollectionNode : Node
    {
        public List<ScalarNode> Values { get; } = [];
    }

    private sealed class ScalarNode(object? value) : Node
    {
        public object? Value { get; } = value;
    }

    private sealed class MissingNode : Node
    {
        public static MissingNode Instance { get; } = new();

        private MissingNode()
        {
        }
    }

    private sealed class FormUrlEncodedInputException(string message) : Exception(message);
}
