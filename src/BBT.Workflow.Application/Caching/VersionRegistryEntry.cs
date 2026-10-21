namespace BBT.Workflow.Caching;

/// <summary>
/// The data payload of a <c>sys-version-registry</c> instance (camelCase JSON). The instance key is the
/// flow key. <see cref="Type"/> is informational only: resolution never reads it.
/// </summary>
/// <param name="Type">Informational component type, for example <c>Flow</c>.</param>
/// <param name="DefaultVersion">The default-version pointer, or null for automatic resolution.</param>
/// <param name="Reason">Optional free-text reason recorded by the operator.</param>
public sealed record VersionRegistryEntry(string? Type, string? DefaultVersion, string? Reason);
