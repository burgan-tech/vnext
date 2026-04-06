using BBT.Aether.AspNetCore.Controllers;
using BBT.Workflow.Definitions;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Orchestration.Controllers.Definitions;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/definitions")]
public sealed class DefinitionController(
    IDefinitionAppService appService) : AetherControllerBase
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
    /// <returns>Result of the cache re-initialization operation.</returns>
    /// <response code="200">Cache re-initialization completed successfully.</response>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpGet("re-initialize")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ReInitializeAsync(
        [FromQuery] bool fullLoad = false,
        CancellationToken cancellationToken = default)
    {
        var result = await appService.ReInitializeAsync(fullLoad, cancellationToken);
        return FromResult(result);
    }
} 