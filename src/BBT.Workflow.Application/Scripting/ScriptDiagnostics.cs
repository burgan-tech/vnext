using BBT.Aether.Results;

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

    /// <summary>
    /// The error a task reports when one of its scripts throws — <c>"{failure}: {Explain}"</c>, the
    /// message executors have always built — with the exception kept on <paramref name="context"/>
    /// for the incident's stack trace (vnext#1268). The task engine takes it after the run. A
    /// cancellation is not the script's failure: it is reported the same way but leaves no cause.
    /// </summary>
    /// <param name="context">The script context the failed script ran on.</param>
    /// <param name="exception">The exception the script threw.</param>
    /// <param name="failure">What failed, e.g. <c>"Http task input handler failed"</c>.</param>
    public static Error TaskScriptError(ScriptContext context, Exception exception, string failure)
    {
        if (exception is not OperationCanceledException)
            context.FailureCause = exception;

        return Error.Failure(WorkflowErrorCodes.TaskExecution, $"{failure}: {Explain(exception)}");
    }

    private static bool MatchesOpenGenericAnonymous(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current.Message?.Contains(OpenGenericAnonymousMarker, StringComparison.Ordinal) == true)
                return true;
        }

        return false;
    }
}
