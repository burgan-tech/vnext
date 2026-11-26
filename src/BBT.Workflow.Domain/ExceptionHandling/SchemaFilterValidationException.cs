using BBT.Aether;

namespace BBT.Workflow.ExceptionHandling;

/// <summary>
/// Exception thrown when a filter request violates schema-driven filter/sort rules
/// (e.g. field is not filterable, operator is not allowed for the field).
/// </summary>
public sealed class SchemaFilterValidationException(string message)
    : UserFriendlyException(code: WorkflowErrorCodes.SchemaFilterValidation, message: message)
{
    /// <summary>
    /// True when the field was refused because THIS caller may not read it (<c>x-roles</c> / <c>x-masking</c>,
    /// vnext#1262), as opposed to a rule that holds for every caller (not filterable, operator not allowed,
    /// encrypted). The list endpoint reads it to tell a refusal it can stand behind from one it made only
    /// because the caller's roles could not be resolved.
    /// </summary>
    public bool CallerRestricted { get; init; }
}
