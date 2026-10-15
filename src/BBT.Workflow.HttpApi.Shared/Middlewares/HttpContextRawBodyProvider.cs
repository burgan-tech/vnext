using System.Text;
using System.Text.Json;
using BBT.Workflow.Payloads;
using BBT.Workflow.Scripting;
using Microsoft.AspNetCore.Http;

namespace BBT.Workflow.Middlewares;

/// <summary>
/// HTTP-host <see cref="IRequestRawBodyProvider"/>. Resolves the raw request body from the ambient job
/// scope first — inside a background job the surrounding HTTP request is the Dapr job-callback transport,
/// not the original payload — then falls back to the live request body captured by
/// <see cref="RawRequestBodyBufferingMiddleware"/>.
/// </summary>
public sealed class HttpContextRawBodyProvider(IHttpContextAccessor httpContextAccessor) : IRequestRawBodyProvider
{
    /// <inheritdoc />
    public string? GetRawBody()
    {
        var ambient = RawBodyExecutionScope.Current;
        if (ambient != null)
            return ambient;

        var items = httpContextAccessor.HttpContext?.Items;
        if (items != null
            && items.TryGetValue(RawRequestBodyBufferingMiddleware.RawBodyItemsKey, out var value))
        {
            return value switch
            {
                // Middleware stores the capture lazily; the UTF-16 conversion happens here,
                // on first actual read, and is cached inside the capture.
                RawRequestBodyCapture capture => capture.Text,
                string text => text,
                _ => null
            };
        }

        return null;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The envelope test mirrors the controllers' payload-mode detection: the
    /// <c>x-vnext-payload-mode</c> header wins, otherwise an object body of standard shape
    /// (<see cref="PayloadEnvelope.IsStandardShape"/>) is an envelope. Nothing is stored when no body
    /// was captured for this request.
    /// </remarks>
    public void ReplaceRawBodyAttributes(JsonElement? attributes)
    {
        var context = httpContextAccessor.HttpContext;
        var items = context?.Items;
        if (items is null
            || !items.TryGetValue(RawRequestBodyBufferingMiddleware.RawBodyItemsKey, out var captured))
            return;

        var original = captured switch
        {
            RawRequestBodyCapture capture => capture.Text,
            string text => text,
            _ => null
        };

        items[RawRequestBodyBufferingMiddleware.RawBodyItemsKey] =
            TryRewriteEnvelope(original, attributes, context!.Request.Headers, out var envelope)
                ? envelope
                : attributes?.GetRawText();
    }

    private static bool TryRewriteEnvelope(
        string? original, JsonElement? attributes, IHeaderDictionary headers, out string? envelope)
    {
        envelope = null;
        if (string.IsNullOrWhiteSpace(original))
            return false;

        var fromHeader = headers.TryGetValue(PayloadEnvelope.ModeHeaderName, out var mode)
            ? PayloadEnvelope.ResolveModeFromHeader(mode.ToString())
            : null;
        if (fromHeader == false)
            return false;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(original);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;
            if (fromHeader is null
                && !PayloadEnvelope.IsStandardShape(root.EnumerateObject().Select(property => property.Name)))
                return false;

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                var written = false;
                foreach (var property in root.EnumerateObject())
                {
                    if (PayloadEnvelope.IsAttributes(property.Name))
                    {
                        WriteAttributes(writer, property.Name, attributes);
                        written = true;
                    }
                    else
                    {
                        property.WriteTo(writer);
                    }
                }

                if (!written)
                    WriteAttributes(writer, PayloadEnvelope.AttributesField, attributes);
                writer.WriteEndObject();
            }

            envelope = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            return true;
        }
    }

    private static void WriteAttributes(Utf8JsonWriter writer, string name, JsonElement? attributes)
    {
        writer.WritePropertyName(name);
        if (attributes is { } value)
            value.WriteTo(writer);
        else
            writer.WriteNullValue();
    }
}
