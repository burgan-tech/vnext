using BBT.Aether.AspNetCore.Controllers;
using BBT.Workflow.Definitions;
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
    IRuntimeInfoProvider runtimeInfoProvider,
    IOptions<RuntimeOptions> runtimeOptions,
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
    /// Invalidates the specified cache entry by reloading it from DB and writing to Redis.
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
    /// Deprecated no-op. Discovery is no longer cached - every resolution queries the registry
    /// directly - so there is nothing to refresh. The route is kept so existing runbooks and
    /// automation do not break; it will be removed a few versions after 0.0.85.
    /// </summary>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost("utilities/discovery/refresh")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult RefreshDiscoveryCacheAsync()
        => Ok(new { message = "Discovery is no longer cached; this endpoint is a deprecated no-op." });
}
