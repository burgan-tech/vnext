using BBT.Aether.Results;
using BBT.Workflow.Definitions.Schemas;
using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.Authorization.RowSecurity;

/// <summary>
/// One read request as row security sees it (vnext#1107).
/// </summary>
/// <param name="Domain">The domain the request addresses.</param>
/// <param name="Workflow">The flow key the request addresses.</param>
/// <param name="Flow">
/// The rule-set lookup: the result of <c>GetFlowAsync(domain, workflow, null)</c> — the version the
/// default-version pointer selects, else the latest published (K2). Never the instance's own version.
/// </param>
/// <param name="Schema">The flow's master-schema filter metadata, when resolved.</param>
/// <param name="Exposure">The master schema's masking / role metadata (<see cref="SchemaExposureMetadata.Empty"/> when none).</param>
/// <param name="Headers">The request headers (single-valued).</param>
/// <param name="RawHeaders">The request headers with every value.</param>
/// <param name="QueryParameters">The request query parameters (single-valued).</param>
/// <param name="RawQueryParameters">The request query parameters with every value.</param>
public sealed record RowSecurityRequest(
    string Domain,
    string Workflow,
    Result<WorkflowDefinition> Flow,
    SchemaFilterContext? Schema,
    SchemaExposureMetadata Exposure,
    IReadOnlyDictionary<string, string?>? Headers,
    IReadOnlyDictionary<string, string[]>? RawHeaders,
    IReadOnlyDictionary<string, string?>? QueryParameters,
    IReadOnlyDictionary<string, string[]>? RawQueryParameters);

/// <summary>
/// Decides which instances the caller of one read request may see (vnext#1107, spec §4).
/// </summary>
public interface IRowSecurityService
{
    /// <summary>
    /// Decides row security for <paramref name="request"/>.
    /// <list type="bullet">
    /// <item>Rule-set lookup failed with <c>CacheItemNotFound</c>, or the flow declares no <c>rowSecurity</c>
    /// ⇒ <see cref="RowSecurityOutcome.NotConfigured"/>, decided before any role resolution.</item>
    /// <item>Any other lookup failure ⇒ <see cref="RowSecurityOutcome.DeniedRuleSetUnavailable"/> carrying the
    /// lookup error unchanged, for every flow (D5).</item>
    /// <item>Otherwise the caller's role set is resolved and <see cref="RowSecurityPlanner"/> decides.</item>
    /// </list>
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The decision; its <see cref="RowSecurityDecision.FlowVersion"/> is the resolved rule-set version.</returns>
    Task<RowSecurityDecision> DecideAsync(RowSecurityRequest request, CancellationToken cancellationToken = default);
}
