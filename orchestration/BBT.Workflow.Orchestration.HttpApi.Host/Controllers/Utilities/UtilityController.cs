using BBT.Aether.AspNetCore.Controllers;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Events;
using BBT.Workflow.Discovery;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Orchestration.Controllers.Utilities;

/// <summary>
/// Provides utility endpoints for system configuration and cache management operations.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
[ServiceFilter(typeof(ResponseHeaderFilter))]
public sealed class UtilityController(
    IDefinitionAppService definitionAppService,
    IRuntimeCacheInitializer runtimeCacheInitializer,
    IDomainCacheContext domainCacheContext,
    IRuntimeInfoProvider runtimeInfoProvider,
    IOptions<RuntimeOptions> runtimeOptions,
    IDomainDiscoveryResolver domainDiscoveryResolver,
    IConfiguration configuration,
    ILogger<UtilityController> logger) : AetherControllerBase
{
    /// <summary>
    /// Retrieves the current runtime configuration information.
    /// </summary>
    /// <returns>Runtime configuration including version and domain information.</returns>
    /// <response code="200">Returns the runtime configuration.</response>
    [HttpGet("config")]
    [ApiExplorerSettings(IgnoreApi = true)]
    [ProducesResponseType(typeof(RuntimeConfigResponse), StatusCodes.Status200OK)]
    public IActionResult GetConfig()
    {
        var response = new RuntimeConfigResponse
        {
            Version = runtimeInfoProvider.Version,
            Domain = runtimeInfoProvider.Domain,
            Schemas = runtimeOptions.Value.Schemas.ToDictionary(s => s.Key, s => s.Value.Schema)
        };

        return Ok(response);
    }

    /// <summary>
    /// Invalidates the specified cache entry.
    /// </summary>
    /// <param name="input">The cache invalidation parameters.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>Result of the cache invalidation operation.</returns>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost("utilities/invalidate")]
    public async Task<IActionResult> InvalidateCacheAsync(
        [FromBody] InvalidateCacheInput input,
        CancellationToken cancellationToken = default)
    {
        var result = await definitionAppService.InvalidateCacheAsync(input, cancellationToken);
        return FromResult(result);
    }

    /// <summary>
    /// Clears and refreshes the bulk domain cache from service discovery.
    /// Fetches all active domain registrations and updates the cache.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>Success message indicating cache refresh completed.</returns>
    /// <response code="200">Returns success message when cache refresh completes.</response>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost("utilities/discovery/refresh")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> RefreshDiscoveryCacheAsync(CancellationToken cancellationToken = default)
    {
        await domainDiscoveryResolver.RefreshBulkCacheAsync(cancellationToken);
        return Ok(new { message = "Discovery cache refreshed successfully" });
    }

    /// <summary>
    /// Handles broadcast cache invalidation requests from Dapr subscription.
    /// All pods receive this message via vnext-pubsub-broadcast.
    /// Updates only in-memory cache since distributed cache is already updated by initiating pod.
    /// </summary>
    /// <param name="eventData">Cache invalidation event data.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>Result of the cache invalidation operation.</returns>
    /// <response code="200">Returns result of cache invalidation.</response>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost("utilities/cache/invalidate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> InvalidateCacheViaBroadcastAsync(
        [FromBody] DefinitionCacheInvalidationEvent eventData,
        CancellationToken cancellationToken = default)
    {
        var podInstance = Environment.GetEnvironmentVariable("HOSTNAME")
            ?? Environment.GetEnvironmentVariable("POD_NAME")
            ?? Environment.MachineName;
        var hostEnvironment = configuration["ASPNETCORE_ENVIRONMENT"];

        try
        {
            // Environment match validation
            if (!string.Equals(eventData.Environment, hostEnvironment, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogDebug(
                    "Definition cache invalidation ignored - environment mismatch. PodInstance: {PodInstance}, EventEnvironment: {EventEnvironment}, CurrentEnvironment: {CurrentEnvironment}",
                    podInstance, eventData.Environment, hostEnvironment);
                return Ok();
            }

            logger.DefinitionCacheInvalidationReceived(
                podInstance,
                eventData.Domain,
                eventData.RequestedBy);

            // Warm in-memory cache from distributed cache; no full DB scan on receiving pods
            await runtimeCacheInitializer.InitializeFromDistributedCacheAsync(cancellationToken);

            logger.DefinitionCacheInvalidationSucceeded(podInstance);

            return Ok();
        }
        catch (Exception ex)
        {
            logger.DefinitionCacheInvalidationFailed(podInstance, ex.ToString());
            return Ok();
        }
    }

    /// <summary>
    /// Handles granular per-component publish events. Each pod warms its local snapshot for
    /// the affected component (snapshot &lt;- Redis body cache, with single-version DB fallback)
    /// without performing a full cache reload. Complements the Redis version index by keeping
    /// the snapshot hot so subsequent reads avoid the extra Redis hop.
    /// </summary>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost("utilities/cache/component-published")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ComponentPublishedAsync(
        [FromBody] ComponentPublishedEvent eventData,
        CancellationToken cancellationToken = default)
    {
        var podInstance = Environment.GetEnvironmentVariable("HOSTNAME")
            ?? Environment.GetEnvironmentVariable("POD_NAME")
            ?? Environment.MachineName;
        var hostEnvironment = configuration["ASPNETCORE_ENVIRONMENT"];

        try
        {
            if (!string.Equals(eventData.Environment, hostEnvironment, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogDebug(
                    "ComponentPublishedEvent ignored - environment mismatch. Pod={PodInstance} EventEnv={EventEnv} HostEnv={HostEnv}",
                    podInstance, eventData.Environment, hostEnvironment);
                return Ok();
            }

            logger.LogInformation(
                "ComponentPublishedEvent received. Pod={PodInstance} ComponentType={ComponentType} Domain={Domain} Key={Key} Version={Version}",
                podInstance, eventData.ComponentType, eventData.Domain, eventData.Key, eventData.Version);

            await domainCacheContext.WarmComponentAsync(
                eventData.ComponentType,
                eventData.Domain,
                eventData.Key,
                eventData.Version,
                cancellationToken);

            return Ok();
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "ComponentPublishedEvent handling failed. Pod={PodInstance} ComponentType={ComponentType} Domain={Domain} Key={Key} Version={Version}",
                podInstance, eventData.ComponentType, eventData.Domain, eventData.Key, eventData.Version);
            return Ok();
        }
    }
} 