using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Workers.Inbox.Controllers;

/// <summary>
/// Provides utility endpoints for Worker.Inbox.
/// Cache invalidation is handled via shared Redis (no pod-to-pod broadcast needed).
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/utilities")]
public sealed class UtilityController : ControllerBase
{
    /// <summary>
    /// Health check endpoint for Worker.Inbox utilities.
    /// </summary>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpGet("health")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult Health() => Ok(new { status = "healthy" });
}
