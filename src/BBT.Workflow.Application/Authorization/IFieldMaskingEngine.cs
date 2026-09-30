using BBT.Workflow.Definitions.Schemas;

namespace BBT.Workflow.Authorization;

/// <summary>
/// Applies an <c>x-masking</c> rule to a single string value. The port the read path depends on; the
/// implementation (Infrastructure) wraps the masking SDK so no SDK type crosses into Application or Domain.
/// <para>
/// Implementations must be deterministic (the data-function cache and ETag assume the same input always
/// masks to the same output), thread-safe (one singleton serves every request) and must never echo the
/// input value in an exception message or a log.
/// </para>
/// </summary>
public interface IFieldMaskingEngine
{
    /// <summary>Returns the value a caller is shown for <paramref name="value"/> under <paramref name="rule"/>.</summary>
    string Apply(FieldMaskRule rule, string value);

    /// <summary>
    /// Publish-time check that the engine can execute <paramref name="rule"/>. Returns one message per
    /// problem; empty when the rule is executable. Structural rules live in <see cref="FieldMaskingDefinition"/>.
    /// </summary>
    IReadOnlyList<string> Validate(FieldMaskRule rule);
}
