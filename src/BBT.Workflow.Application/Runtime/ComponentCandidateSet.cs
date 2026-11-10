namespace BBT.Workflow.Runtime;

/// <summary>
/// The versions of one component key that a backend load produced, together with the versions it
/// had to leave out.
/// </summary>
/// <remarks>
/// A stored row is left out when its body cannot be turned back into the component (a
/// deserialization failure, or a body that reads as <c>null</c>). Such a row is still published, so
/// version resolution over <see cref="Entities"/> alone may pick an older build than the one that
/// should win. Carrying the dropped versions lets the resolver tell a complete candidate list from a
/// partial one instead of trusting both equally (issue #1192).
/// </remarks>
/// <typeparam name="T">The component type.</typeparam>
/// <param name="Entities">The components that were loaded successfully.</param>
/// <param name="DroppedVersions">The stored versions that were left out of <paramref name="Entities"/>.</param>
public sealed record ComponentCandidateSet<T>(
    List<T> Entities,
    IReadOnlyList<string> DroppedVersions)
    where T : class
{
    /// <summary>Whether any stored version was left out of <see cref="Entities"/>.</summary>
    public bool HasDropped => DroppedVersions.Count > 0;
}
