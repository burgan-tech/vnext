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

    /// <summary>
    /// Number of most-recent incidents surfaced inline in instance metadata
    /// (<c>GetInstanceOutput.Metadata.Incident.History</c>). Matches the pre-table retention cap so the
    /// DTO keeps its historical shape; the full history is served by the incidents endpoint.
    /// </summary>
    public const int InlineHistoryLimit = 5;
}
