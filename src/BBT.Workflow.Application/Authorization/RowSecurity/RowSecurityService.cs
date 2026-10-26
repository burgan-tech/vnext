using System.Diagnostics;
using BBT.Aether.Users;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Authorization.RowSecurity;

/// <summary>
/// Default <see cref="IRowSecurityService"/>: maps the rule-set lookup onto NotConfigured / D5 denial, resolves
/// the caller's role set only for a row-secured flow, and hands the rest to <see cref="RowSecurityPlanner"/>.
/// <para>
/// Observability (vnext#1107): every decision for a row-secured flow is tagged on the read span and logged through
/// <c>WorkflowLogs</c> — parameter NAMES, rule indexes, error codes and coarse kinds only, never a value and never
/// exception text. <see cref="RowSecurityOutcome.NotConfigured"/> emits nothing at all (C-6).
/// </para>
/// </summary>
public sealed class RowSecurityService(
    ICallerRoleResolver callerRoleResolver,
    ICurrentUser currentUser,
    IOptions<RowSecurityOptions> options,
    ILogger<RowSecurityService>? logger = null) : IRowSecurityService
{
    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;

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

        var decision = await DecideCoreAsync(request, cancellationToken);
        Observe(request, decision);
        return decision;
    }

    private async Task<RowSecurityDecision> DecideCoreAsync(RowSecurityRequest request, CancellationToken cancellationToken)
    {
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

        if (decision.Outcome == RowSecurityOutcome.DeniedResolutionFailed)
        {
            _logger.RowSecurityDeniedRoleResolutionFailed(
                request.Domain, request.Workflow, roles.FailureKind ?? "unknown");
        }

        return decision with { FlowVersion = flow.Version };
    }

    private void Observe(RowSecurityRequest request, RowSecurityDecision decision)
    {
        if (decision.Outcome.ToTagValue() is not { } outcome)
            return; // NotConfigured: no tag, no log (C-6).

        InstanceReadActivityHelper.SetRowSecurity(request.Span ?? Activity.Current, decision);

        _logger.RowSecurityApplied(
            request.Domain, request.Workflow, decision.FlowVersion, request.Surface, outcome,
            string.Join(',', decision.MatchedRuleIndexes));

        foreach (var drop in decision.DroppedRules)
        {
            _logger.RowSecurityRuleDropped(
                request.Domain, request.Workflow, drop.RuleIndex, drop.Reason, string.Join(',', drop.ParameterNames));
        }

        if (decision.Outcome == RowSecurityOutcome.DeniedRuleSetUnavailable)
        {
            _logger.RowSecurityRuleSetUnavailable(
                request.Domain, request.Workflow, decision.LookupError?.Code ?? "unknown");
        }
    }
}
