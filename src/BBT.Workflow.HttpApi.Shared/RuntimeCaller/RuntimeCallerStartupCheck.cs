using BBT.Workflow.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.RuntimeCaller;

/// <summary>
/// Loads the runtime-caller identity at start, so every host logs which key id it signs with
/// (the attestation operators compare across hosts before enabling the gate), and logs Critical
/// when a key ring is held with the script sandbox off under the explicit bypass.
/// </summary>
internal sealed class RuntimeCallerStartupCheck(
    RuntimeCallerIdentity identity,
    IOptions<RuntimeCallerOptions> options,
    IConfiguration configuration,
    ILogger<RuntimeCallerStartupCheck> logger,
    bool runsFlowScripts) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!identity.IsConfigured)
            logger.RuntimeCallerIdentityNotConfigured();

        var holdsMountedSecret = identity.IsConfigured
                                 || !string.IsNullOrWhiteSpace(options.Value.AppApiTokenFile);
        if (holdsMountedSecret
            && runsFlowScripts
            && options.Value.AllowWithoutScriptSandbox
            && !configuration.GetValue<bool>("Scripting:Sandbox:Enabled"))
            logger.RuntimeCallerKeyRingWithoutScriptSandbox();

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
