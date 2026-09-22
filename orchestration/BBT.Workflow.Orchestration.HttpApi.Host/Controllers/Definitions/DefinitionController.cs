using BBT.Aether.AspNetCore.Controllers;
using BBT.Workflow.Definitions;
using Microsoft.AspNetCore.Mvc;

namespace BBT.Workflow.Orchestration.Controllers.Definitions;

[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}/definitions")]
public sealed class DefinitionController(
    IDefinitionAppService appService,
    IPublishCompletedAppService publishCompletedAppService) : AetherControllerBase
{
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost("publish")]
    public async Task<IActionResult> PublishAsync(
        [FromBody] PublishInput input,
        CancellationToken cancellationToken = default)
    {
        var result = await appService.PublishAsync(input, cancellationToken);
        return FromResult(result);
    }

    /// <summary>
    /// Signals that a deployment has finished publishing every component of a package, and runs the
    /// post-deployment hooks once.
    /// </summary>
    /// <param name="input">Optional identification of the finished deployment.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>What each hook did.</returns>
    /// <response code="200">The hooks ran. Read <c>success</c> in the body — a failed hook is reported there, not as a non-2xx.</response>
    /// <remarks>
    /// <para>
    /// <b>Called ONCE per deployment, after the last <c>publish</c>.</b> A component deployment sends
    /// every component to <c>publish</c> one at a time; per-component post-work would repeat this
    /// N times for an effect that is identical each time. The callers today are the init host's
    /// package job and the <c>wf</c> CLI's <c>sync</c> / <c>update</c> / <c>reset</c>.
    /// </para>
    /// <para>
    /// <b>This replaces <c>definitions/re-initialize</c></b>, which had been reduced to a no-op —
    /// both callers already made exactly this one call at exactly this moment, and it did nothing.
    /// The shape was right; the content was gone.
    /// </para>
    /// <para>
    /// <b>It is the discovery cache's only automatic invalidation.</b> That cache holds no expiry by
    /// default, so a deployment finishing is what tells the runtime to re-read the domain registry.
    /// A pipeline that does not make this call keeps whatever endpoints it resolved at startup until
    /// the pod restarts.
    /// </para>
    /// </remarks>
    [ApiExplorerSettings(IgnoreApi = true)]
    [HttpPost("publish/completed")]
    [ProducesResponseType(typeof(PublishCompletedOutput), StatusCodes.Status200OK)]
    public async Task<IActionResult> PublishCompletedAsync(
        [FromBody] PublishCompletedInput? input = null,
        CancellationToken cancellationToken = default)
    {
        var result = await publishCompletedAppService.ExecuteAsync(
            input ?? new PublishCompletedInput(), cancellationToken);

        return FromResult(result);
    }
}
