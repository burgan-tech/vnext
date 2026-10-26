using BBT.Aether.Users;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Authorization.RowSecurity;

/// <summary>
/// Default <see cref="IRowSecurityService"/>: maps the rule-set lookup onto NotConfigured / D5 denial, resolves
/// the caller's role set only for a row-secured flow, and hands the rest to <see cref="RowSecurityPlanner"/>.
/// </summary>
public sealed class RowSecurityService(
    ICallerRoleResolver callerRoleResolver,
    ICurrentUser currentUser,
    IOptions<RowSecurityOptions> options) : IRowSecurityService
{
    private static readonly IReadOnlyDictionary<string, string?> NoHeaders =
        new Dictionary<string, string?>(0, StringComparer.OrdinalIgnoreCase);

    private static readonly RowSecurityDecision NotConfigured =
        new(RowSecurityOutcome.NotConfigured, null, [], [], [], null);

    /// <inheritdoc />
    public async Task<RowSecurityDecision> DecideAsync(
        RowSecurityRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Flow.IsSuccess)
        {
            // R-F9: only "no published definition" means "nothing to enforce". Every other failure — pointer
            // invalid (Instance:100052), registry unavailable (Instance:100053), version not activated, a cache
            // outage — denies, for every flow, and the error goes back to the caller unchanged (D5).
            return request.Flow.Error.Code == WorkflowErrorCodes.CacheItemNotFound
                ? NotConfigured
                : new RowSecurityDecision(
                    RowSecurityOutcome.DeniedRuleSetUnavailable, null, [], [], [], null, request.Flow.Error);
        }

        var flow = request.Flow.Value;
        if (flow?.RowSecurity is not { } definition)
            return NotConfigured; // C-6: decided before the role resolver is touched.

        // Never null: a resolver given null headers may fall back to another credential's headers.
        var headers = request.Headers ?? NoHeaders;
        var roles = await callerRoleResolver.ResolveRoleSetAsync(headers, cancellationToken);

        var parameters = new RequestRowParameterSource(
            request.RawHeaders, request.Headers, request.RawQueryParameters, request.QueryParameters, currentUser);

        var decision = RowSecurityPlanner.Plan(new RowSecurityPlanInput(
            definition, roles, parameters, request.Schema, request.Exposure, options.Value));

        return decision with { FlowVersion = flow.Version };
    }
}
