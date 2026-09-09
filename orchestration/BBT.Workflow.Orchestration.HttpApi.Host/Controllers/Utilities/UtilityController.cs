using BBT.Aether.AspNetCore.Controllers;
using BBT.Workflow.Definitions;
using BBT.Workflow.Discovery;
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
    /// Forces an immediate refresh of the discovery endpoint cache.
    /// </summary>
    /// <param name="refresher">Refresher, absent when the discovery cache is disabled.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>What the refresh actually did.</returns>
    /// <response code="200">The refresh ran, was skipped, or the cache is not enabled.</response>
    /// <remarks>
    /// <para>
    /// Reads the registry and republishes every entry <b>synchronously</b>, bypassing the refresh
    /// window. It does not merely request a refresh for the next tick: at an hour-long window this
    /// endpoint IS the safety mechanism for the one case a TTL cannot cover — a domain's
    /// <c>baseUrl</c> moving — and an operator acting on a misrouting incident needs the answer, not
    /// an acknowledgement. The staleness that remains afterwards is each pod's in-process layer,
    /// bounded by <c>Cache:L1TtlSeconds</c>.
    /// </para>
    /// <para>
    /// A no-op when the cache is disabled: there is nothing to refresh, because every resolution
    /// already queries the registry directly.
    /// </para>
    /// </remarks>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost("utilities/discovery/refresh")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> RefreshDiscoveryCacheAsync(
        [FromServices] IDiscoveryCacheRefresher? refresher = null,
        CancellationToken cancellationToken = default)
    {
        if (refresher is null)
        {
            return Ok(new
            {
                outcome = "disabled",
                message = "The discovery cache is disabled; every resolution already queries the registry."
            });
        }

        var outcome = await refresher.RefreshAsync(force: true, cancellationToken);

        logger.LogInformation("Forced discovery cache refresh completed with outcome {Outcome}", outcome);

        return Ok(new
        {
            outcome = outcome.ToString(),
            refreshed = outcome == DiscoveryCacheRefreshOutcome.Refreshed,
            message = outcome switch
            {
                DiscoveryCacheRefreshOutcome.Refreshed =>
                    "Discovery cache refreshed from the registry. Each pod's in-process layer clears within Cache:L1TtlSeconds.",
                DiscoveryCacheRefreshOutcome.SkippedNotOwner =>
                    "Another replica is refreshing right now; its result applies cluster-wide.",
                DiscoveryCacheRefreshOutcome.Failed =>
                    "The registry could not be read. Existing entries were left untouched and will expire on their own.",
                _ => outcome.ToString()
            }
        });
    }
}
