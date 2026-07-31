namespace BBT.Workflow.Scripting.Related;

/// <summary>
/// Thrown when a related-instance read fails or the per-context resolution cap is exceeded.
/// Absence (no parent, no correlation, instance gone) is reported as null instead — a read failure
/// must never be mistaken for absence, because that silently produces a wrong business decision.
/// </summary>
public sealed class RelatedInstanceAccessException : Exception
{
    /// <summary>Creates the exception with a message.</summary>
    public RelatedInstanceAccessException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with a message and an inner cause.</summary>
    public RelatedInstanceAccessException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
