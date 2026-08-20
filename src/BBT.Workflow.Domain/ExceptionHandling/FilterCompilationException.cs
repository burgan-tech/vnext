using BBT.Aether;

namespace BBT.Workflow.ExceptionHandling;

/// <summary>
/// Thrown by the fail-closed guards in the filter/sort builders when a request that already passed
/// boundary validation still cannot be translated into SQL exactly as authored.
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="SchemaFilterValidationException"/> on purpose. That one signals a
/// <em>schema policy</em> decision — the field is not filterable, the operator is not in
/// <c>x-filterOperators</c> — which is an expected rejection of a well-formed request. This one
/// signals that <c>InstanceQueryValidator</c> and the SQL builder disagree about what is
/// executable, i.e. a defect in the runtime rather than in the caller's request.
/// </para>
/// <para>
/// Both map to HTTP 400 because the caller cannot be served either way, but only this one is worth
/// an Error-level log: conflating them would fire a drift alarm on every routine policy rejection.
/// </para>
/// </remarks>
public sealed class FilterCompilationException(string message)
    : UserFriendlyException(code: WorkflowErrorCodes.InstanceFilterInvalid, message: message);
