namespace BBT.Workflow.Scripting;

/// <summary>
/// Default <see cref="IRequestRawBodyProvider"/> for hosts without an HTTP request pipeline
/// (workers, migrators). Resolves the raw body from the ambient job scope only
/// (<see cref="RawBodyExecutionScope"/>). HTTP hosts replace this with an implementation that
/// also reads the live request body.
/// </summary>
public sealed class AmbientRequestRawBodyProvider : IRequestRawBodyProvider
{
    /// <inheritdoc />
    public string? GetRawBody() => RawBodyExecutionScope.Current;
}
