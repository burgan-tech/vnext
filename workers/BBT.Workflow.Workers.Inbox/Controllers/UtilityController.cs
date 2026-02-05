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
        
        // Update only in-memory cache (distributed cache already updated by initiating pod)
        await runtimeCacheInitializer.InitializeAsync(cancellationToken);
        
        logger.DefinitionCacheInvalidationSucceeded(podInstance);
                
        return Ok();
    }
}
