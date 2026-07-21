namespace BBT.Workflow.Scripting;

/// <summary>
/// Enriches raw script-execution exceptions with actionable guidance before they are
/// surfaced in a <c>Result</c> error message. Keeps executor catch-sites free of
/// diagnostic heuristics — they simply call <see cref="Explain"/> in place of
/// <c>ex.Message</c>.
/// </summary>
public static class ScriptDiagnostics
{
    // Thrown by the runtime when a dynamic value is used inside an anonymous-type
    // initializer, leaving the anonymous type as an open generic
    // (e.g. "Cannot create an instance of <>f__AnonymousType0`1[...] because
    //  Type.ContainsGenericParameters is true.").
    private const string OpenGenericAnonymousMarker = "ContainsGenericParameters";

    private const string DynamicAnonymousGuidance =
        "A script built its response with a 'dynamic' value (e.g. from context.Body) inside an " +
        "anonymous type 'new { ... }', which leaves the anonymous type as an open generic and cannot " +
        "be instantiated. Cast dynamic values to concrete types or 'object' " +
        "(e.g. (object?)result?.value), or build the object with CreateObject()/SetProperty()/" +
        "ToDictionary() or a Dictionary<string, object?>.";

    /// <summary>
    /// Returns an actionable message for known script-authoring pitfalls, or the original
    /// <see cref="System.Exception.Message"/> when the exception is not recognized.
    /// </summary>
    /// <param name="exception">The exception thrown while compiling or running a script mapping.</param>
    public static string Explain(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (MatchesOpenGenericAnonymous(exception))
            return $"{DynamicAnonymousGuidance} Original error: {exception.Message}";

        return exception.Message;
    }

    private static bool MatchesOpenGenericAnonymous(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains(OpenGenericAnonymousMarker, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
