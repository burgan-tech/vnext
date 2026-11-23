using BBT.Workflow.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Middlewares;

/// <summary>
/// Announces once at start that the guard runs in <see cref="DaprLocalDeliveryMode.LogOnly"/> without
/// an app API token (EventId 40998), so per-request checks stay silent until a token is configured.
/// <see cref="DaprLocalDeliveryMode.Enforce"/> without a token never gets this far: it fails startup in
/// <see cref="DaprLocalDeliveryOptionsValidator"/>.
/// </summary>
public sealed class DaprLocalDeliveryStartupCheck(
    IOptions<DaprLocalDeliveryOptions> options,
    DaprAppApiToken token,
    ILogger<DaprLocalDeliveryStartupCheck> logger) : IHostedService
{
    /// <summary>Logs EventId 40998 when LogOnly runs without a token.</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (options.Value.Mode == DaprLocalDeliveryMode.LogOnly && !token.IsConfigured)
            logger.DaprLocalDeliveryLogOnlyWithoutToken();

        return Task.CompletedTask;
    }

    /// <summary>Nothing to stop.</summary>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
