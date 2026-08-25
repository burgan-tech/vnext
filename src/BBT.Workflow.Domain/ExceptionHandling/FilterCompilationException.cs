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
/// Only this one is worth an Error-level log: conflating the two would fire a drift alarm on every
/// routine policy rejection.
/// </para>
/// <para>
/// KNOWN ISSUE — both currently surface as HTTP <b>500</b>, not 400. The status is derived from
/// <c>Error.Prefix</c> (<c>ErrorNormalizer.MapPrefixToStatusCode</c>), and <c>ResultExtensions.TryAsync</c>
/// wraps any thrown exception as <c>Prefix = "failure"</c>, which has no mapping. The
/// <c>Validation:</c> segment of the <em>code</em> has no effect on the status. Only a returned
/// <c>Result.Fail(Error.Validation(...))</c> produces <c>Prefix = "validation"</c> ⇒ 400, which is
/// why the boundary validator's rejections answer 400 while these throws do not. Fixing it means
/// surfacing these as a failed Result instead of an exception — a deliberate change to the public
/// error contract, not a comment-level fix.
/// </para>
/// </remarks>
public sealed class FilterCompilationException(string message)
    : UserFriendlyException(code: WorkflowErrorCodes.InstanceFilterInvalid, message: message);
