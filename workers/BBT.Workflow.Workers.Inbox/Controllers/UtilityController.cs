using BBT.Workflow.Caching;
using BBT.Workflow.Definitions.Events;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Workers.Inbox.Controllers;

/// <summary>
/// Provides utility endpoints for cache management operations in Worker.Inbox.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/utilities")]
public sealed class UtilityController(
    IRuntimeCacheInitializer runtimeCacheInitializer,
    IDomainCacheContext domainCacheContext,
    IRuntimeInfoProvider runtimeInfoProvider,
    IConfiguration configuration,
    ILogger<UtilityController> logger) : ControllerBase
{
    /// <summary>
    /// Handles broadcast cache invalidation requests from Dapr subscription.
    /// All Worker.Inbox pods receive this message via vnext-pubsub-broadcast.
    /// Updates only in-memory cache since distributed cache is already updated by initiating pod.
    /// </summary>
    /// <param name="eventData">Cache invalidation event data.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>Result of the cache invalidation operation.</returns>
    /// <response code="200">Returns result of cache invalidation.</response>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost("cache/invalidate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> InvalidateCacheViaBroadcastAsync(
        [FromBody] DefinitionCacheInvalidationEvent eventData,
        CancellationToken cancellationToken = default)
    {
        var podInstance = Environment.GetEnvironmentVariable("HOSTNAME") 
            ?? Environment.GetEnvironmentVariable("POD_NAME") 
            ?? Environment.MachineName;
        
        var hostEnvironment = configuration["ASPNETCORE_ENVIRONMENT"];
        // Environment match validation
        if (!string.Equals(eventData.Environment, hostEnvironment, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug(
                "Definition cache invalidation ignored - environment mismatch. PodInstance: {PodInstance}, EventEnvironment: {EventEnvironment}, CurrentEnvironment: {CurrentEnvironment}",
                podInstance, eventData.Environment, hostEnvironment);
            return Ok();
        }
        
        // Domain match validation (Worker.Inbox multi-tenant)
        if (!runtimeInfoProvider.IsDomainMatch(eventData.Domain))
        {
            logger.DefinitionCacheInvalidationIgnoredDomainMismatch(podInstance, eventData.Domain);
            return Ok();
        }
        
        logger.DefinitionCacheInvalidationReceived(podInstance, eventData.Domain, eventData.RequestedBy);
        
        // Warm in-memory cache from distributed cache; no full DB scan on receiving pods
        await runtimeCacheInitializer.InitializeFromDistributedCacheAsync(cancellationToken);
        
        logger.DefinitionCacheInvalidationSucceeded(podInstance);
                
        return Ok();
    }

    /// <summary>
    /// Handles granular per-component publish events on Worker.Inbox pods. Warms the local
    /// snapshot for the affected component without a full reload. See the orchestration
    /// host's UtilityController for the full rationale.
    /// </summary>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost("cache/component-published")]
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

            // Worker.Inbox is multi-tenant - drop events that target a different domain.
            if (!runtimeInfoProvider.IsDomainMatch(eventData.Domain))
            {
                logger.LogDebug(
                    "ComponentPublishedEvent ignored - domain mismatch. Pod={PodInstance} EventDomain={EventDomain}",
                    podInstance, eventData.Domain);
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
                "ComponentPublishedEvent handling failed. Pod={PodInstance}",
                podInstance);
            return Ok();
        }
    }
}
