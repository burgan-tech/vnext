namespace BBT.Workflow.SubFlow.Headers;

/// <summary>
/// Settings for carrying the headers of the request that settled a SubFlow child up to its parent.
/// Bound from configuration section "Workflow:SubFlow:CarriedHeaders". The deny floor and the
/// framework exclusions are enforced in code and cannot be relaxed from here.
/// </summary>
public sealed class SubFlowCarriedHeadersOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Workflow:SubFlow:CarriedHeaders";

    /// <summary>When false, terminal events carry no headers.</summary>
    public bool Emit { get; set; } = true;

    /// <summary>When true, the parent's output mapping can read the carried headers.</summary>
    public bool ExposeToOutputMapping { get; set; } = true;

    /// <summary>When true, the carried headers are applied to the parent's resume request.</summary>
    public bool ApplyToResume { get; set; } = false;

    /// <summary>Extra header names to deny, matched exactly and case-insensitively. Adds to the floor only.</summary>
    public string[] AdditionalDenied { get; set; } = [];

    /// <summary>Maximum number of carried entries; overflow is dropped and counted.</summary>
    public int MaxEntries { get; set; } = 64;

    /// <summary>Maximum length of a carried value; longer values are dropped.</summary>
    public int MaxValueLength { get; set; } = 4096;
}
