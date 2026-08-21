namespace BBT.Workflow.Definitions.GraphQL.Validation;

/// <summary>
/// The set of instance-query parameters to validate together before a query is built.
/// </summary>
/// <remarks>
/// This validator covers the <em>grammar</em> of a query — the cases that currently fail open and
/// silently widen the result set. Schema-driven <em>policy</em> (field filterability, operator
/// whitelisting, sortability from <c>SchemaFilterContext</c>) is enforced deeper, where it already
/// throws <c>SchemaFilterValidationException</c>. Keeping the two apart avoids two implementations
/// of the same rule drifting.
/// </remarks>
public sealed record InstanceQueryValidationRequest
{
    /// <summary>Filter JSON (GraphQL-style) or a legacy <c>field=op:value</c> string.</summary>
    public string? Filter { get; init; }

    /// <summary>OrderBy/sort JSON.</summary>
    public string? Sort { get; init; }

    /// <summary>GroupBy JSON.</summary>
    public string? GroupBy { get; init; }

    /// <summary>Aggregations JSON.</summary>
    public string? Aggregations { get; init; }
}

/// <summary>
/// A single reason an instance-query parameter was rejected.
/// </summary>
/// <param name="Code">Machine sub-code, e.g. <c>filter.unknownOperator</c>.</param>
/// <param name="Message">Human-readable explanation, including a correction hint where known.</param>
/// <param name="Target">Path into the request the error applies to, e.g. <c>filter.attributes.amount</c>.</param>
public sealed record FilterValidationError(string Code, string Message, string? Target = null);

/// <summary>
/// The outcome of validating an <see cref="InstanceQueryValidationRequest"/>.
/// </summary>
/// <param name="IsValid">True when every parameter can be executed exactly as authored.</param>
/// <param name="Errors">All reasons for rejection, capped at <see cref="MaxErrors"/>.</param>
public sealed record FilterValidationResult(bool IsValid, IReadOnlyList<FilterValidationError> Errors)
{
    /// <summary>Upper bound on collected errors, so a pathological filter cannot inflate the response.</summary>
    public const int MaxErrors = 20;

    /// <summary>A passing result.</summary>
    public static readonly FilterValidationResult Valid = new(true, []);

    /// <summary>Creates a failing result from a non-empty error list.</summary>
    public static FilterValidationResult Invalid(IReadOnlyList<FilterValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (errors.Count == 0)
            throw new ArgumentException("An invalid result requires at least one error.", nameof(errors));

        return new FilterValidationResult(false, errors);
    }

    /// <summary>
    /// The <see cref="WorkflowErrorCodes"/> constant matching the first error's parameter family.
    /// </summary>
    public string PrimaryErrorCode
    {
        get
        {
            var first = Errors.Count > 0 ? Errors[0].Code : string.Empty;
            if (first.StartsWith("sort.", StringComparison.Ordinal))
                return WorkflowErrorCodes.InstanceSortInvalid;
            if (first.StartsWith("groupBy.", StringComparison.Ordinal))
                return WorkflowErrorCodes.InstanceGroupByInvalid;
            if (first.StartsWith("aggregations.", StringComparison.Ordinal))
                return WorkflowErrorCodes.InstanceAggregationInvalid;
            return WorkflowErrorCodes.InstanceFilterInvalid;
        }
    }

    /// <summary>Joins every error message into a single sentence for the error response summary.</summary>
    public string ToMessage() => string.Join(" ", Errors.Select(e => e.Message));
}
