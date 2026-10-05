namespace BBT.Workflow.Authorization;

/// <summary>
/// Host-level switch for master-schema <c>x-masking</c>. When disabled the read surfaces fall back to
/// <c>x-roles</c> pruning only; nothing else changes.
/// <para>
/// The switch is part of the data-function cache generation (key AND ETag), so flipping it can never
/// answer a client with a 304 for a body produced under the other setting.
/// </para>
/// </summary>
public sealed class SchemaMaskingOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "SchemaMasking";

    /// <summary>Whether <c>x-masking</c> rules are applied. Default true.</summary>
    public bool Enabled { get; set; } = true;
}
