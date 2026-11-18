namespace BBT.Workflow.Instances;

/// <summary>
/// One published version of a component, identified by key and version, without its body.
/// Used to list the components of a type; reading the bodies instead costs memory in proportion to the
/// whole publish history.
/// </summary>
/// <param name="Key">The component key.</param>
/// <param name="Version">The version as stored, build metadata included.</param>
/// <param name="PublishedAt">When the version's data row was written.</param>
public sealed record PublishedComponentVersion(
    string Key,
    string Version,
    DateTime PublishedAt);
