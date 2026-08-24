namespace BBT.Workflow.Definitions.Schemas;

/// <summary>
/// The <c>x-sensitive</c> annotation on a single schema property — an author's declaration that
/// the field carries protected data, and what the runtime should do about it.
/// <para>
/// Declared once on the schema property; every protection surface (log redaction, masking, and
/// from Phase 2 encryption at rest) derives its behaviour from this one record rather than from
/// its own configuration.
/// </para>
/// </summary>
public sealed record SensitiveFieldMetadata
{
    /// <summary>
    /// Master switch. A property whose <c>x-sensitive.enabled</c> is <c>false</c> is treated as
    /// not sensitive at all — the annotation is inert, which is the documented way to stage an
    /// annotation before turning it on.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Why the field is protected (e.g. <c>PII</c>, <c>PII-Identification</c>, <c>Financial</c>).
    /// Free-form and required when <see cref="Enabled"/> is set: it is the only part of the
    /// annotation that explains the classification to an auditor.
    /// </summary>
    public string? Purpose { get; init; }

    /// <summary>
    /// Encrypt the value before it reaches the <c>Data</c> jsonb column. Has no effect until the
    /// Phase 2 cipher lands; a definition carrying it today is accepted (and validated) so
    /// authors can annotate ahead of the runtime.
    /// </summary>
    public bool EncryptAtRest { get; init; }

    /// <summary>
    /// Replace the value with <see cref="MaskingPattern"/> when it would otherwise reach a log
    /// sink or a diagnostic message.
    /// </summary>
    public bool RedactInLogs { get; init; }

    /// <summary>
    /// Pattern applied when the value is redacted or masked (e.g. <c>{first}***@***.***</c>,
    /// <c>***-**-{last4}</c>). Null falls back to a fixed placeholder. Token validity is checked
    /// at definition time — see <see cref="SensitiveValueMasker"/> for the token vocabulary.
    /// </summary>
    public string? MaskingPattern { get; init; }

    /// <summary>
    /// How long the value may be retained, in days. Parsed and surfaced so a definition is not
    /// silently lossy, but <b>not enforced</b> — retention needs a purge job over the instance
    /// data history and is deliberately out of scope here. Treat a value as documentation until
    /// that job exists.
    /// </summary>
    public int? RetentionDays { get; init; }

    /// <summary>
    /// True when this annotation asks for anything at all. An <c>enabled: true</c> annotation
    /// with no protection selected is legal — it still classifies the field for auditing — but
    /// callers that only care about behaviour can skip it.
    /// </summary>
    public bool HasProtection => Enabled && (EncryptAtRest || RedactInLogs);
}
