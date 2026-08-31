namespace BBT.Workflow.Payloads;

/// <summary>
/// The field vocabulary of the standard vNext request envelope — the shape that carries
/// <c>attributes</c> (the business payload) alongside the instance metadata <c>key</c>,
/// <c>tags</c> and <c>stage</c>.
/// <para>
/// Payload-mode detection is shared by the JSON path (<c>PayloadModeDetector</c>) and the
/// form-url-encoded path (<c>FormUrlEncodedJsonElementInputFormatter</c>). Both must agree on
/// what an envelope looks like: a body classified as free-form is wrapped whole under
/// <c>attributes</c>, so a misclassified envelope makes the transition/start schema validate
/// <c>key</c>/<c>tags</c> instead of the business payload.
/// </para>
/// </summary>
public static class PayloadEnvelope
{
    /// <summary>Header that explicitly overrides payload-mode auto-detection.</summary>
    public const string ModeHeaderName = "x-vnext-payload-mode";

    /// <summary>Header value selecting free-form (raw) mode.</summary>
    public const string RawMode = "raw";

    /// <summary>The property carrying the business payload.</summary>
    public const string AttributesField = "attributes";

    /// <summary>
    /// Returns <c>true</c> when <paramref name="name"/> is the <c>attributes</c> property.
    /// Case-insensitive, to match the case-insensitive JSON model binding
    /// (<c>JsonSerializerOptions.Web</c>) that consumes the body immediately afterwards.
    /// </summary>
    public static bool IsAttributes(string name)
        => string.Equals(name, AttributesField, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <c>true</c> when <paramref name="name"/> is an instance-metadata envelope field
    /// (<c>key</c>, <c>tags</c>, <c>stage</c>) — i.e. an envelope field other than
    /// <c>attributes</c>.
    /// </summary>
    public static bool IsMetadataField(string name)
        => string.Equals(name, "key", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "tags", StringComparison.OrdinalIgnoreCase)
           || string.Equals(name, "stage", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <c>true</c> when <paramref name="name"/> belongs to the envelope vocabulary.
    /// </summary>
    public static bool IsEnvelopeField(string name)
        => IsAttributes(name) || IsMetadataField(name);

    /// <summary>
    /// Resolves the payload mode from an explicit <see cref="ModeHeaderName"/> value.
    /// Returns <c>null</c> when the header is absent, meaning the caller must fall back to
    /// shape-based detection.
    /// </summary>
    public static bool? ResolveModeFromHeader(string? headerValue)
        => headerValue is null
            ? null
            : !string.Equals(headerValue, RawMode, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Shape-based classification over the body's top-level property names.
    /// </summary>
    /// <remarks>
    /// <list type="number">
    ///   <item>An <c>attributes</c> property ⇒ standard, whatever else is alongside it.</item>
    ///   <item>Otherwise, a non-empty body whose properties are <em>all</em> envelope metadata
    ///         ⇒ standard. The envelope's fields are individually optional, so
    ///         <c>{"key":"K1"}</c> is a valid envelope carrying no business data — wrapping it
    ///         would feed <c>key</c> to the business schema.</item>
    ///   <item>Anything else — including the empty object, which carries no signal and keeps its
    ///         long-standing "start with no data" normalization — ⇒ free-form.</item>
    /// </list>
    /// A free-form payload whose own fields are named exactly like envelope metadata is
    /// indistinguishable from an envelope; such callers must send
    /// <c>x-vnext-payload-mode: raw</c>.
    /// </remarks>
    public static bool IsStandardShape(IEnumerable<string> topLevelPropertyNames)
    {
        var hasAnyProperty = false;
        var allMetadata = true;

        foreach (var name in topLevelPropertyNames)
        {
            if (IsAttributes(name))
                return true;

            hasAnyProperty = true;
            if (!IsMetadataField(name))
                allMetadata = false;
        }

        return hasAnyProperty && allMetadata;
    }
}
