using BBT.Workflow.Monitor.Jobs.Filters;

namespace BBT.Workflow.Monitor.Jobs;

/// <summary>Outcome of validating the createdAt range supplied to the jobs endpoints.</summary>
public enum JobsFilterValidation
{
    /// <summary>The filter is acceptable for the requested scope.</summary>
    Valid,

    /// <summary>Domain-wide query is missing a bounded createdAt range (both bounds required).</summary>
    CreatedAtRequired,

    /// <summary>Exactly one bound was supplied, or the lower bound is greater than the upper bound.</summary>
    CreatedAtRange
}

/// <summary>
/// Pure validator for the jobs createdAt range. Enforces a bounded range on domain-wide queries
/// and a both-or-neither rule everywhere. No GraphQL parsing.
/// </summary>
public static class JobsFilterValidator
{
    /// <summary>
    /// Validates the supplied createdAt range for the requested scope.
    /// </summary>
    /// <param name="filter">The caller-supplied filter; treated as empty when null.</param>
    /// <param name="isDomainWide">True for the domain-wide jobs query, where a bounded range is mandatory.</param>
    /// <returns>The validation outcome.</returns>
    public static JobsFilterValidation Validate(MonitorJobFilterInput? filter, bool isDomainWide)
    {
        var hasGte = filter?.CreatedAtGte is not null;
        var hasLte = filter?.CreatedAtLte is not null;

        if (isDomainWide && !(hasGte && hasLte))
            return JobsFilterValidation.CreatedAtRequired;

        if (hasGte != hasLte)
            return JobsFilterValidation.CreatedAtRange;

        if (hasGte && hasLte && filter!.CreatedAtGte > filter.CreatedAtLte)
            return JobsFilterValidation.CreatedAtRange;

        return JobsFilterValidation.Valid;
    }
}
