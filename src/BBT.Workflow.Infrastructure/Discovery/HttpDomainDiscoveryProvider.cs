using System.Diagnostics;
using BBT.Aether.Results;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Discovery;

/// <summary>
/// The default provider: resolves a domain to the base URL it registered, and the
/// <c>Remote*</c> typed clients then call that URL over plain HTTP.
/// </summary>
/// <remarks>
/// <para>
/// The provider itself holds no cache and asks <see cref="IDiscoveryRegistryClient"/> on every call;
/// a registration without a <c>baseUrl</c> is an error. This is what
/// <c>ServiceDiscovery:Provider=http</c> restores, which is why it is a real rollback rather than an
/// approximation of one.
/// </para>
/// <para>
/// Caching, when enabled, lives one layer down — a decorator over the registry client, wired up only
/// for this provider. It is kept out of here deliberately: what is cacheable is the caller-independent
/// <c>DomainRegistration</c>, not the <see cref="DiscoveryEndpoint"/> this class produces, whose
/// <c>Kind</c> depends on the calling site's <c>preferredKind</c>. Caching the endpoint
/// under a domain-keyed entry lets a Dapr-preferring caller and a URL caller overwrite each other's
/// answer — a real defect in the implementation removed in <c>79da3b6f</c>.
/// </para>
/// </remarks>
public sealed class HttpDomainDiscoveryProvider(
    IDiscoveryRegistryClient registryClient,
    IOptions<ServiceDiscoveryOptions> serviceDiscoveryOptions,
    ILogger<HttpDomainDiscoveryProvider> logger)
    : DomainDiscoveryProviderBase(serviceDiscoveryOptions, logger)
{
    /// <inheritdoc />
    protected override string ProviderName => DiscoveryProviders.Http;

    /// <summary>
    /// False: the registry is this provider's only source of an address.
    /// </summary>
    protected override bool CanResolveWithoutRegistry => false;

    /// <inheritdoc />
    protected override async Task<Result<DiscoveryEndpoint>> ResolveAsync(
        string domain,
        EndpointKind preferredKind,
        Activity? activity,
        CancellationToken cancellationToken)
    {
        var lookup = await registryClient.LookupAsync(domain, cancellationToken);
        if (!lookup.IsSuccess)
            return Result<DiscoveryEndpoint>.Fail(lookup.Error);

        var registration = lookup.Value!;

        if (string.IsNullOrWhiteSpace(registration.BaseUrl))
        {
            return Result<DiscoveryEndpoint>.Fail(
                WorkflowErrors.DomainDiscoveryFailed(domain, "Empty or invalid response"));
        }

        // Only claim "registry" if nothing already claimed the tag. The caching decorator sits
        // BELOW this call and tags a hit as `cache` before returning, so an unconditional set here
        // would overwrite it on every single hit — leaving the span permanently reporting `registry`
        // and the cache invisible in traces, which is precisely the observability this feature is
        // required to have.
        if (activity?.GetTagItem(TelemetryConstants.TagNames.DiscoveryResolution) is null)
        {
            activity?.SetTag(
                TelemetryConstants.TagNames.DiscoveryResolution,
                TelemetryConstants.DiscoveryResolutions.Registry);
        }

        var baseUrl = registration.BaseUrl.TrimEnd('/') + "/";
        Logger.DomainResolvedFromRegistry(domain, baseUrl);

        // Kind still honours the caller: a trigger task with UseDapr asks for Dapr explicitly,
        // and the registry may carry an app-id for it. Only the DOWNGRADE that used to happen
        // when appId was absent is gone — that decision now belongs to provider selection.
        var kind = preferredKind == EndpointKind.Dapr && !string.IsNullOrWhiteSpace(registration.AppId)
            ? EndpointKind.Dapr
            : EndpointKind.Url;

        return Result.Ok(new DiscoveryEndpoint(kind, new Uri(baseUrl), registration.AppId));
    }
}
