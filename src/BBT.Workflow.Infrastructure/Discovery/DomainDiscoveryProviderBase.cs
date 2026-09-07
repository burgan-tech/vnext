using System.Diagnostics;
using BBT.Aether.Results;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Discovery;

/// <summary>
/// Shared shell for the discovery providers: owns the <c>Discovery.Resolve/{domain}</c> span,
/// its common tags, and the disabled-discovery short circuit. Subclasses implement only the
/// resolution itself.
/// </summary>
/// <remarks>
/// <para>
/// Provider selection is config-driven (<see cref="ServiceDiscoveryOptions.Provider"/>), which is
/// load-bearing rather than cosmetic: ALL ~40 cross-domain call sites pass
/// <see cref="EndpointKind.Url"/>, so if the caller's preference decided the kind, nothing would
/// ever migrate. See <see cref="IDomainDiscoveryResolver.GetEndpointAsync"/> for the full
/// advisory-vs-binding rule.
/// </para>
/// <para>
/// The span uses <c>PipelineStepActivityHelper.ActivitySource</c> (<c>BBT.Workflow.Pipeline</c>)
/// deliberately — it is already present in every host's
/// <c>Telemetry:Tracing:AdditionalSources</c>. Introducing a new source here would require the
/// same commit to register it in all four hosts, and an unregistered source produces spans the
/// TracerProvider never subscribes to: dropped silently, with no error.
/// </para>
/// </remarks>
public abstract class DomainDiscoveryProviderBase(
    IOptions<ServiceDiscoveryOptions> serviceDiscoveryOptions,
    ILogger logger) : IDomainDiscoveryResolver
{
    /// <summary>Options for the discovery subsystem.</summary>
    protected ServiceDiscoveryOptions Options => serviceDiscoveryOptions.Value;

    /// <summary>Logger for the concrete provider.</summary>
    protected ILogger Logger => logger;

    /// <summary>
    /// Value reported as <c>vnext.discovery.provider</c> — one of
    /// <see cref="DiscoveryProviders"/>.
    /// </summary>
    protected abstract string ProviderName { get; }

    /// <summary>
    /// True when this provider can answer without the registry being enabled.
    /// </summary>
    /// <remarks>
    /// The HTTP provider cannot: its only source of an address IS the registry. The Dapr
    /// provider can, because the app-id comes from the convention — so
    /// <c>ServiceDiscovery:Enabled=false</c> (which governs REGISTRATION) must not block
    /// resolution. Conflating the two would make the switch look broken in exactly the
    /// deployment that needs it least.
    /// </remarks>
    protected abstract bool CanResolveWithoutRegistry { get; }

    /// <inheritdoc />
    public async Task<Result<DiscoveryEndpoint>> GetEndpointAsync(
        string domain,
        EndpointKind preferredKind = EndpointKind.Url,
        CancellationToken cancellationToken = default)
    {
        using var activity = PipelineStepActivityHelper.ActivitySource.StartActivity(
            $"Discovery.Resolve/{domain}", ActivityKind.Internal);
        activity?.SetTag(TelemetryConstants.TagNames.DiscoveryDomain, domain);
        activity?.SetTag(TelemetryConstants.TagNames.DiscoveryProvider, ProviderName);
        activity?.SetTag(TelemetryConstants.TagNames.SpanCategory, TelemetryConstants.SpanCategories.Business);

        if (!Options.Enabled && !CanResolveWithoutRegistry)
        {
            const string reason = "Service discovery is disabled";
            activity?.SetStatus(ActivityStatusCode.Error, reason);
            return Result<DiscoveryEndpoint>.Fail(
                WorkflowErrors.DomainDiscoveryFailed(domain, reason));
        }

        var result = await ResolveAsync(domain, preferredKind, activity, cancellationToken);

        if (!result.IsSuccess)
        {
            activity?.SetStatus(ActivityStatusCode.Error, result.Error.Message);
            return result;
        }

        activity?.SetTag(
            TelemetryConstants.TagNames.DiscoveryEndpointKind, result.Value!.Kind.ToString());
        Logger.DomainResolvedViaProvider(domain, ProviderName, result.Value!.BaseUrl.ToString());

        return result;
    }

    /// <summary>
    /// Resolves the endpoint. Called inside the <c>Discovery.Resolve</c> span, which is passed
    /// in so the provider can add its own tags (app-id, namespace, resolution source).
    /// </summary>
    /// <param name="domain">Domain to resolve.</param>
    /// <param name="preferredKind">
    /// Caller preference. Advisory for cross-domain call sites; binding when
    /// <see cref="EndpointKind.Dapr"/> is requested by a trigger task's <c>UseDapr</c>.
    /// </param>
    /// <param name="activity">The active span, or null when not sampled.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    protected abstract Task<Result<DiscoveryEndpoint>> ResolveAsync(
        string domain,
        EndpointKind preferredKind,
        Activity? activity,
        CancellationToken cancellationToken);
}
