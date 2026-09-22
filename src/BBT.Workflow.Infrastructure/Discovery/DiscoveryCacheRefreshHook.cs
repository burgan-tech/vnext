using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Discovery;

/// <summary>
/// Post-deployment hook that republishes the discovery endpoint cache from the registry.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is what makes an expiry-free discovery cache correct.</b> The cache holds no TTL by
/// default, so nothing reclaims a stale entry on a clock — a domain's deployment finishing is the
/// event that says "re-read the registry", and this hook is where that event lands. Remove it and
/// the cache keeps whatever it learned at startup until the pod restarts.
/// </para>
/// <para>
/// <b>Forced, and therefore window-blind.</b> <c>force: true</c> skips the refresh marker: the
/// deployment that just finished is precisely the caller who knows the current window's answer is
/// out of date. The lock inside the refresher still applies, so two overlapping calls produce one
/// registry read.
/// </para>
/// <para>
/// <b>Two different "nothing to do" states, one answer.</b> <c>IDiscoveryCacheRefresher</c> is
/// registered only under <c>ServiceDiscovery:Provider=http</c> with <c>Cache:Enabled</c> — the Dapr
/// provider derives app-ids from a convention and caches nothing to invalidate — and service
/// discovery itself can be switched off entirely with <c>ServiceDiscovery:Enabled=false</c>, which
/// the registration does NOT consider. Both report <c>Disabled</c>. The hook is registered either
/// way so that the endpoint's answer distinguishes "refreshed" from "there is no cache here", which
/// an absent hook could not.
/// </para>
/// <para>
/// <b>The <c>Enabled</c> check is load-bearing, and it was found by running it.</b> A runtime with
/// discovery off still registers the refresher (the condition above omits <c>Enabled</c>), so
/// without this check the hook called it, the bulk read went to the default registry address,
/// nothing answered, and the endpoint reported <c>success: false</c> — on every deployment of every
/// single-domain runtime, for a feature that deployment had switched off. The read path has always
/// gated on <c>Enabled</c> in <c>CachingDiscoveryRegistryClient.LookupAsync</c>; this is the same
/// gate on the write side.
/// </para>
/// </remarks>
public sealed class DiscoveryCacheRefreshHook(
    IOptions<ServiceDiscoveryOptions> serviceDiscoveryOptions,
    IDiscoveryCacheRefresher? refresher) : IPublishCompletedHook
{
    /// <summary>The hook's reported name. A CD pipeline may key on it.</summary>
    public const string HookName = "discovery-cache";

    /// <inheritdoc />
    public string Name => HookName;

    /// <inheritdoc />
    public int Order => 100;

    /// <inheritdoc />
    public async Task<Result<string>> ExecuteAsync(
        PublishCompletedInput input,
        CancellationToken cancellationToken)
    {
        if (refresher is null || !serviceDiscoveryOptions.Value.Enabled)
            return Result<string>.Ok(PublishCompletedHookOutcomes.Disabled);

        var outcome = await refresher.RefreshAsync(force: true, cancellationToken);

        // Failed is the only outcome that is not a success. SkippedNotOwner means another replica is
        // reading the registry right now and its result is written to the shared layer, which is the
        // outcome this hook wanted.
        return outcome == DiscoveryCacheRefreshOutcome.Failed
            ? Result<string>.Fail(Error.Failure(
                WorkflowErrorCodes.DomainDiscoveryFailed,
                "The discovery registry could not be read; cached endpoints were left untouched.",
                "Retry the call, or POST utilities/discovery/refresh once the registry answers."))
            : Result<string>.Ok(outcome.ToString());
    }
}
