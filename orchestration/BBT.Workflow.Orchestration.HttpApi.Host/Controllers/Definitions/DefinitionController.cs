using BBT.Aether.AspNetCore.Controllers;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Events;
using BBT.Workflow.Runtime;
using Dapr.Client;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Orchestration.Controllers.Definitions;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/definitions")]
public sealed class DefinitionController(
    IDefinitionAppService appService,
    DaprClient daprClient,
    IRuntimeInfoProvider runtimeInfoProvider,
    IConfiguration configuration) : AetherControllerBase
{
    [HttpPost("publish")]
    public async Task<IActionResult> PublishAsync(
        [FromBody] PublishInput input,
        CancellationToken cancellationToken = default)
    {
        var result = await appService.PublishAsync(input, cancellationToken);
        return FromResult(result);
    }

    /// <summary>
    /// Re-initializes the workflow system cache by broadcasting invalidation event to all pods.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>Accepted response indicating broadcast initiated.</returns>
    /// <response code="202">Cache invalidation broadcast initiated successfully.</response>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpGet("re-initialize")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> ReInitializeAsync(
        CancellationToken cancellationToken = default)
    {
        // Publish broadcast event to all pods using Dapr client directly
        var cacheInvalidationEvent = new DefinitionCacheInvalidationEvent
        {
            Domain = runtimeInfoProvider.Domain,
            RequestedBy = "Manual",
            RequestedAt = DateTime.UtcNow,
            Environment = configuration["ASPNETCORE_ENVIRONMENT"]!
        };
        
        await daprClient.PublishEventAsync(
            pubsubName: configuration["DAPR_PUBSUB_BROADCAST_STORE_NAME"]!,
            topicName: DefinitionCacheInvalidationEvent.TopicName,
            data: cacheInvalidationEvent,
            cancellationToken: cancellationToken);
        
        return Accepted(new { message = "Cache invalidation broadcast initiated" });
    }
} 