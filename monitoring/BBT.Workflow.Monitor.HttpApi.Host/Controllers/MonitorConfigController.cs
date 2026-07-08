using BBT.Aether.AspNetCore.Controllers;
using BBT.Workflow.Monitor.Config;
using BBT.Workflow.Monitor.Config.DTOs;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BBT.Workflow.Monitor.Controllers;

/// <summary>Read-only runtime/monitor configuration (P9).</summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
[ServiceFilter(typeof(ResponseHeaderFilter))]
public sealed class MonitorConfigController(IMonitorConfigService configService) : AetherControllerBase
{
    /// <summary>Returns runtime and monitor configuration (secrets and connection strings excluded).</summary>
    /// <response code="200">Config returned.</response>
    [HttpGet("config")]
    [ProducesResponseType(typeof(MonitorConfigResponse), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetConfigAsync(CancellationToken cancellationToken = default)
        => FromResult(await configService.GetConfigAsync(cancellationToken));
}
