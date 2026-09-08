namespace BBT.Workflow.Instances;

/// <summary>
/// Column length limits for <see cref="InstanceIncident"/>. Shared by the factory (which truncates
/// free-text fields) and the EF model configuration (which sizes the columns), so the two cannot drift.
/// </summary>
public static class InstanceIncidentConstants
{
    /// <summary>Maximum stored length of <see cref="InstanceIncident.Message"/>.</summary>
    public const int MaxMessageLength = 1024;

    /// <summary>Maximum stored length of <see cref="InstanceIncident.StackTrace"/>.</summary>
    public const int MaxStackTraceLength = 4096;

    /// <summary>Maximum length of <see cref="InstanceIncident.ErrorCode"/>.</summary>
    public const int MaxErrorCodeLength = 256;

    /// <summary>Maximum length of <see cref="InstanceIncident.ErrorLayer"/>, <see cref="InstanceIncident.BoundaryAction"/> and <see cref="InstanceIncident.BoundaryLevel"/>.</summary>
    public const int MaxShortTokenLength = 64;

    /// <summary>Maximum length of <see cref="InstanceIncident.TraceId"/>.</summary>
    public const int MaxTraceIdLength = 64;

    // There is deliberately no inline-history cap any more: no read surface embeds incidents. The
    // state body and instance metadata carry links, and the history endpoint pages the full set.
}
