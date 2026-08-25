using BBT.Aether.AspNetCore.Controllers;
using BBT.Workflow.Security;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Orchestration.Controllers.Security;

/// <summary>
/// Operator endpoints for instance-data encryption maintenance: bringing existing rows onto the
/// active key (backfill and key rotation are the same pass) and reporting expired retention windows.
/// </summary>
/// <remarks>
/// Not part of the public API surface. Network isolation is the boundary, as with the other internal
/// maintenance endpoints — see <c>docs/contracts/api-and-service-contracts.md</c>.
/// </remarks>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/{domain}/security/instance-data")]
public sealed class SecurityMaintenanceController(
    IInstanceDataEncryptionMaintenanceService maintenanceService) : AetherControllerBase
{
    /// <summary>
    /// Brings the current schema's instance-data rows onto the active encryption key.
    /// </summary>
    /// <param name="dryRun">
    /// When true (the default) nothing is written and the response reports what would change.
    /// Defaulting to a simulation is deliberate: the pass rewrites live rows.
    /// </param>
    /// <param name="batchSize">Instances per page (1–1000).</param>
    /// <param name="maxInstances">Stop after this many instances. Omit to sweep the whole schema.</param>
    /// <param name="instanceKey">Restrict the pass to one instance key, for verification.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Counts of what was scanned and rewritten, plus per-instance failures.</returns>
    /// <response code="200">The pass completed; the body reports what happened.</response>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost("re-encrypt")]
    [ProducesResponseType(typeof(EncryptionMaintenanceReport), StatusCodes.Status200OK)]
    public async Task<IActionResult> ReEncryptAsync(
        [FromQuery] bool dryRun = true,
        [FromQuery] int batchSize = 200,
        [FromQuery] int? maxInstances = null,
        [FromQuery] string? instanceKey = null,
        CancellationToken cancellationToken = default)
    {
        var report = await maintenanceService.ReEncryptAsync(
            new EncryptionMaintenanceRequest(dryRun, batchSize, maxInstances, instanceKey),
            cancellationToken);

        return Ok(report);
    }
}
