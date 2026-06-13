namespace BBT.Workflow.Scripting.Helpers;

/// <summary>
/// Feature switch for the custom-script-helpers capability, bound from configuration
/// (section <c>Scripting:Helpers</c>). When disabled, transition mappings that declare
/// <c>helpers[]</c> are rejected at compile time so the feature can be opted into per environment.
/// </summary>
public sealed class ScriptHelpersOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Scripting:Helpers";

    /// <summary>
    /// Master switch for referencing helper components from a mapping. Defaults to <c>false</c>
    /// so existing deployments are unaffected until explicitly opted in.
    /// </summary>
    public bool Enabled { get; set; }
}
