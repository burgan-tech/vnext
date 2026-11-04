namespace BBT.Workflow.Logging;

/// <summary>
/// Rewrites a value so it survives W3C baggage propagation through every .NET propagator a
/// downstream service may run, and stays usable as a URL path segment.
/// </summary>
/// <remarks>
/// <para>
/// A flow version carries SemVer build metadata (<c>1.0.0-pkg.1.0.346+contract</c>). The <c>+</c>
/// is a valid baggage octet, but a .NET 8 service loading <c>System.Diagnostics.DiagnosticSource</c>
/// older than 10 reads baggage with the legacy propagator, which url-decodes it into a space. The
/// Elastic agent forwards that space raw and Dapr 1.18 rejects the next call with
/// <c>invalid baggage header</c>.
/// </para>
/// <para>
/// Percent-encoding is not an answer: .NET 10's W3C propagator encodes <c>%</c> itself, so a value
/// set as <c>%2B</c> leaves as <c>%252B</c>; and a compliant receiver decodes it and forwards a raw
/// <c>+</c> again, which the next legacy hop breaks. Only RFC 3986 unreserved characters
/// (<c>A-Z a-z 0-9 - . _ ~</c>) pass every hop unchanged, so every other character becomes
/// <see cref="Replacement"/>. SemVer never contains <c>_</c>, so for a version the result stays
/// unambiguous: <c>1.0.0-pkg.1.0.346_contract</c>.
/// </para>
/// <para>
/// Applies to the propagated baggage only. Span tags are not propagated and keep the raw value.
/// </para>
/// </remarks>
public static class BaggageValueFormatter
{
    /// <summary>The character every non-unreserved character is replaced with.</summary>
    public const char Replacement = '_';

    /// <summary>
    /// Returns <paramref name="value"/> with every character outside the RFC 3986 unreserved set
    /// replaced by <see cref="Replacement"/>; <c>null</c> and already-safe values are returned as is.
    /// </summary>
    public static string? Format(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var firstUnsafe = -1;
        for (var i = 0; i < value.Length; i++)
        {
            if (!IsUnreserved(value[i]))
            {
                firstUnsafe = i;
                break;
            }
        }

        if (firstUnsafe < 0)
            return value;

        return string.Create(value.Length, (value, firstUnsafe), static (span, state) =>
        {
            var (source, start) = state;
            source.AsSpan(0, start).CopyTo(span);
            for (var i = start; i < source.Length; i++)
                span[i] = IsUnreserved(source[i]) ? source[i] : Replacement;
        });
    }

    private static bool IsUnreserved(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~';
}
