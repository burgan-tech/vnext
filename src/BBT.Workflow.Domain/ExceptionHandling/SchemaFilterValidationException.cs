using BBT.Aether;

namespace BBT.Workflow.ExceptionHandling;

/// <summary>
/// Exception thrown when a filter request violates schema-driven filter/sort rules
/// (e.g. field is not filterable, operator is not allowed for the field).
/// </summary>
public sealed class SchemaFilterValidationException(string message)
    : UserFriendlyException(code: WorkflowErrorCodes.SchemaFilterValidation, message: message);
