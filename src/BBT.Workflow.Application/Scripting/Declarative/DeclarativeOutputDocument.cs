using System.Text.Json;
using System.Text.Json.Nodes;
using BBT.Aether.Results;

namespace BBT.Workflow.Scripting.Declarative;

/// <summary>
/// The response a declarative <c>output</c> block produced (workflow output and function output): the body, and the
/// status code and headers when the block wrote them.
/// </summary>
/// <param name="Data">The <c>data</c> member as an owned <see cref="JsonElement"/>; null when absent (empty response).</param>
/// <param name="StatusCode">The <c>statusCode</c> member; null when absent.</param>
/// <param name="Headers">The <c>headers</c> member; null when absent or empty.</param>
internal sealed record DeclarativeOutput(object? Data, int? StatusCode, Dictionary<string, string>? Headers);

/// <summary>
/// Reads the target document <c>{ data, statusCode, headers }</c> of a workflow or function <c>output</c> block. Shared
/// by both sites so the conversion rules are one: <c>statusCode</c> is an integer JSON number or absent, <c>headers</c> an
/// object of string values or absent; anything else is a block failure (never a cast exception).
/// </summary>
internal static class DeclarativeOutputDocument
{
    private const string DataMember = "data";
    private const string StatusCodeMember = "statusCode";
    private const string HeadersMember = "headers";

    /// <summary>
    /// Converts <paramref name="document"/> to the response, or fails with <paramref name="site"/>'s failure when
    /// <c>statusCode</c> or <c>headers</c> has the wrong shape. With <paramref name="dataOnly"/> (a function without
    /// <c>rawResponse</c>, which answers <c>{ key: data }</c>) only <c>data</c> is read: <c>statusCode</c> and
    /// <c>headers</c> are ignored entirely, not type-checked.
    /// </summary>
    public static Result<DeclarativeOutput> Read(JsonObject document, DeclarativeSite site, bool dataOnly = false)
    {
        // An owned element: the response body serializes as the JSON the block wrote.
        object? data = document[DataMember] is { } dataNode ? JsonSerializer.SerializeToElement(dataNode) : null;
        if (dataOnly)
            return Result<DeclarativeOutput>.Ok(new DeclarativeOutput(data, null, null));

        int? statusCode = null;
        var statusNode = document[StatusCodeMember];
        switch (statusNode)
        {
            case null:
                break;
            case JsonValue value when value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<int>(out var code):
                statusCode = code;
                break;
            default:
                return Result<DeclarativeOutput>.Fail(
                    site.Failure($"$.statusCode is a JSON {statusNode.GetValueKind()} ({statusNode.ToJsonString()}), not an integer."));
        }

        Dictionary<string, string>? headers = null;
        var headersNode = document[HeadersMember];
        switch (headersNode)
        {
            case null:
                break;
            case JsonObject members:
                headers = new Dictionary<string, string>(members.Count, StringComparer.Ordinal);
                foreach (var (name, node) in members)
                {
                    if (node is not JsonValue headerValue || !headerValue.TryGetValue<string>(out var text))
                    {
                        return Result<DeclarativeOutput>.Fail(site.Failure(
                            $"$.headers.{name} is a JSON {node?.GetValueKind() ?? JsonValueKind.Null}, not a string."));
                    }

                    headers[name] = text;
                }

                break;
            default:
                return Result<DeclarativeOutput>.Fail(
                    site.Failure($"$.headers is a JSON {headersNode.GetValueKind()}, not an object."));
        }

        return Result<DeclarativeOutput>.Ok(
            new DeclarativeOutput(data, statusCode, headers is { Count: > 0 } ? headers : null));
    }
}
