namespace BBT.Workflow.Scripting.Caller;

/// <summary>
/// Thrown by <see cref="ICallerAccessor"/> when the caller's roles cannot be read: the surface has no
/// caller, the selection that exposed them has finished, or the provider could not answer.
/// </summary>
public sealed class CallerRolesUnavailableException : Exception
{
    /// <summary>Creates the exception.</summary>
    public CallerRolesUnavailableException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
