using BBT.Aether.AspNetCore.Controllers;
using BBT.Workflow.Agents;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Orchestration.Controllers.Definitions;

/// <summary>
/// AI agent definition endpoints (issue #1105).
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
public sealed class AgentDefinitionController(IAgentDryRunAppService dryRunAppService) : AetherControllerBase
{
    /// <summary>
    /// Runs an agent once against sample data, without an instance: no journal, no session, no data write;
    /// every tool answers a stub. Allowed only for the caller roles in <c>AiAgent:DryRun:Roles</c>.
    /// </summary>
    /// <param name="domain">Domain key.</param>
    /// <param name="key">Agent key.</param>
    /// <param name="body">Prompt, sample data and optional candidates.</param>
    /// <param name="version">Agent version; latest when omitted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    [HttpPost("{domain}/definitions/agents/{key}/dry-run")]
    [ProducesResponseType(typeof(AgentDryRunOutput), StatusCodes.Status200OK)]
    public async Task<IActionResult> DryRunAsync(
        [FromRoute] string domain,
        [FromRoute] string key,
        [FromBody] AgentDryRunInput body,
        [FromQuery] string? version = null,
        CancellationToken cancellationToken = default)
    {
        body.Domain = domain;
        body.Key = key;
        body.Version = version ?? body.Version;
        body.Headers = HttpContext.Request.Headers.ToDictionary(h => h.Key, h => (string?)h.Value.ToString(), StringComparer.OrdinalIgnoreCase);

        var result = await dryRunAppService.RunAsync(body, cancellationToken);
        return FromResult(result);
    }
}
