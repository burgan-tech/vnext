using System.Diagnostics;
using BBT.Aether.Results;
using BBT.Workflow.Execution;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Discovery;

/// <summary>
/// Resolves a domain to its Dapr app-id and returns a <c>dapr://{appId}/</c> endpoint, which
/// <c>RemoteTransportRouter</c> hands to the Dapr transport shell on <see cref="EndpointKind.Dapr"/>.
/// Dapr Name Resolution then turns the app-id into an address, and sidecar-to-sidecar mTLS applies.
/// </summary>
/// <remarks>
/// <para>
/// <b>The app-id is derived, not looked up.</b> Every component's app-id follows a fixed
/// convention (<see cref="VNextAppIds"/>), so by default (<c>RequireRegistryEntry=false</c>) the
/// path makes no network call at all — a real improvement over the HTTP provider, which queries
/// the registry on every single cross-domain call. The registry is read only when
/// <c>RequireRegistryEntry=true</c> (existence check + optional <c>appId</c> override, cached) or
/// for a domain pinned to the HTTP shape via <c>DomainOverrides</c>.
/// </para>
/// <para>
/// <b>Cross-namespace app-ids are explicit.</b> When a namespace template is configured the
/// app-id is emitted as <c>{appId}.{namespace}</c>. This is required, not decorative: Dapr's
/// <c>requestAppIDAndNamespace</c> defaults the namespace to the CALLER's own when the app-id
/// carries no dot, so a bare app-id would resolve inside the wrong namespace. The same
/// <c>(namespace, appId)</c> pair is what Dapr uses to build the expected SPIFFE identity, so
/// the address and the mTLS identity are guaranteed to agree.
/// </para>
/// <para>
/// <b>Caching is safe here in a way it was not before.</b> The registry deliberately had no
/// cache so a moved address could never be masked. Under this provider the registry no longer
/// supplies the address — Name Resolution does, per call — so a stale entry can only stale the
/// optional app-id override. Failures are never cached, so a domain that registers later is
/// picked up immediately.
/// </para>
/// </remarks>
public sealed class DaprDomainDiscoveryProvider(
    IDiscoveryRegistryClient registryClient,
    IMemoryCache cache,
    IOptions<ServiceDiscoveryOptions> serviceDiscoveryOptions,
    ILogger<DaprDomainDiscoveryProvider> logger)
    : DomainDiscoveryProviderBase(serviceDiscoveryOptions, logger)
{
    private const string CacheKeyPrefix = "disc:dapr:";

    /// <inheritdoc />
    protected override string ProviderName => DiscoveryProviders.Dapr;

    /// <summary>
    /// True: the app-id comes from the convention, so resolution does not depend on the
    /// registry being enabled.
    /// </summary>
    protected override bool CanResolveWithoutRegistry => true;

    /// <inheritdoc />
    protected override async Task<Result<DiscoveryEndpoint>> ResolveAsync(
        string domain,
        EndpointKind preferredKind,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        var dapr = Options.Dapr;

        // Per-domain escape hatch: "url" pins this domain to the HTTP provider's shape so a
        // single domain can be rolled forward or back without touching the global switch.
        if (dapr.DomainOverrides.TryGetValue(domain, out var over) &&
            over.Trim().Equals(DaprDiscoveryOptions.UrlOverride, StringComparison.OrdinalIgnoreCase))
        {
            return await ResolveViaRegistryUrlAsync(domain, activity, cancellationToken);
        }

        var conventionAppId = VNextAppIds.Orchestrator(domain);
        var cacheKey = CacheKeyPrefix + domain.ToLowerInvariant();

        if (dapr.CacheSeconds > 0 && cache.TryGetValue(cacheKey, out string? cachedAppId) &&
            !string.IsNullOrEmpty(cachedAppId))
        {
            return Success(activity, cachedAppId, TelemetryConstants.DiscoveryResolutions.Cache);
        }

        var appId = conventionAppId;
        var resolution = TelemetryConstants.DiscoveryResolutions.Convention;

        // An explicit per-domain app-id override outranks both the registry and the convention.
        if (dapr.DomainOverrides.TryGetValue(domain, out var explicitAppId) &&
            !string.IsNullOrWhiteSpace(explicitAppId))
        {
            appId = explicitAppId.Trim();
        }
        else if (dapr.RequireRegistryEntry)
        {
            var lookup = await registryClient.LookupAsync(domain, cancellationToken);

            if (!lookup.IsSuccess)
            {
                // RequireRegistryEntry keeps the registry authoritative about which domains
                // exist, so a miss or a registry failure fails the resolution. (The
                // registry-is-down escape hatch is RequireRegistryEntry=false, which never
                // enters this branch — see the else below.)
                return Result<DiscoveryEndpoint>.Fail(lookup.Error);
            }
            else
            {
                resolution = TelemetryConstants.DiscoveryResolutions.Registry;

                if (dapr.PreferRegistryAppId && !string.IsNullOrWhiteSpace(lookup.Value!.AppId))
                {
                    appId = lookup.Value!.AppId!.Trim();

                    if (!appId.Equals(conventionAppId, StringComparison.OrdinalIgnoreCase))
                        Logger.DomainAppIdOverriddenByRegistry(domain, appId, conventionAppId);
                }
            }
        }
        else
        {
            Logger.DomainRegistryLookupSkipped(domain);
        }

        if (resolution == TelemetryConstants.DiscoveryResolutions.Convention)
            Logger.DomainResolvedByConvention(domain, appId);

        var targetNamespace = dapr.ResolveNamespace(domain);
        var qualifiedAppId = QualifyWithNamespace(appId, targetNamespace);

        if (dapr.CacheSeconds > 0)
        {
            cache.Set(cacheKey, qualifiedAppId, TimeSpan.FromSeconds(dapr.CacheSeconds));
        }

        activity?.SetTag(TelemetryConstants.TagNames.DaprNamespace, targetNamespace);
        return Success(activity, qualifiedAppId, resolution);
    }

    /// <summary>
    /// Appends <c>.{namespace}</c> unless it is absent or already present.
    /// </summary>
    /// <remarks>
    /// Dapr splits the app-id on <c>.</c> and rejects more than one separator
    /// (<c>invalid app id</c>), so a namespace is never appended to an app-id that already
    /// carries one — that includes a registry-supplied override which may already be qualified.
    /// </remarks>
    private static string QualifyWithNamespace(string appId, string? targetNamespace) =>
        string.IsNullOrWhiteSpace(targetNamespace) || appId.Contains('.')
            ? appId
            : $"{appId}.{targetNamespace}";

    /// <summary>
    /// Builds the <c>dapr://{appId}/</c> endpoint.
    /// </summary>
    /// <remarks>
    /// <see cref="DiscoveryEndpoint.BaseUrl"/> is non-nullable and there is no URL under Dapr, so
    /// <c>dapr://{appId}/</c> is the honest placeholder: it names the target and is safe to log.
    /// It is <b>informational only</b> — nothing routes on the scheme. The transport decision is
    /// <see cref="EndpointKind.Dapr"/>, read by <c>RemoteTransportRouter</c>; the Dapr shell then
    /// uses <see cref="DiscoveryEndpoint.DaprAppId"/> and never touches this URI.
    /// </remarks>
    private static Result<DiscoveryEndpoint> Success(Activity? activity, string appId, string resolution)
    {
        activity?.SetTag(TelemetryConstants.TagNames.DiscoveryResolution, resolution);
        activity?.SetTag(TelemetryConstants.TagNames.DaprAppId, appId);

        return Result.Ok(new DiscoveryEndpoint(
            EndpointKind.Dapr,
            new Uri($"dapr://{appId}/"),
            appId));
    }

    /// <summary>
    /// Resolves through the registry base URL, for a domain pinned back to the HTTP shape by
    /// <see cref="DaprDiscoveryOptions.DomainOverrides"/>.
    /// </summary>
    private async Task<Result<DiscoveryEndpoint>> ResolveViaRegistryUrlAsync(
        string domain,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        var lookup = await registryClient.LookupAsync(domain, cancellationToken);
        if (!lookup.IsSuccess)
            return Result<DiscoveryEndpoint>.Fail(lookup.Error);

        if (string.IsNullOrWhiteSpace(lookup.Value!.BaseUrl))
        {
            return Result<DiscoveryEndpoint>.Fail(
                WorkflowErrors.DomainDiscoveryFailed(domain, "Empty or invalid response"));
        }

        activity?.SetTag(
            TelemetryConstants.TagNames.DiscoveryResolution,
            TelemetryConstants.DiscoveryResolutions.Registry);

        var baseUrl = lookup.Value!.BaseUrl!.TrimEnd('/') + "/";
        Logger.DomainResolvedFromRegistry(domain, baseUrl);

        return Result.Ok(new DiscoveryEndpoint(EndpointKind.Url, new Uri(baseUrl), lookup.Value!.AppId));
    }
}
