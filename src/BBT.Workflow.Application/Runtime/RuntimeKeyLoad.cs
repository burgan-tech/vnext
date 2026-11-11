namespace BBT.Workflow.Runtime;

/// <summary>
/// The result of loading every active version of one component key.
/// </summary>
/// <typeparam name="T">The component entity type</typeparam>
/// <param name="Entities">The rows that were read successfully.</param>
/// <param name="DroppedVersions">
/// Versions of rows that were found but could not be read. Empty when every row was read.
/// </param>
public sealed record RuntimeKeyLoad<T>(IReadOnlyList<T> Entities, IReadOnlyList<string> DroppedVersions)
    where T : class
{
    /// <summary>Whether any row was dropped from <see cref="Entities"/>.</summary>
    public bool HasDropped => DroppedVersions.Count > 0;
}
