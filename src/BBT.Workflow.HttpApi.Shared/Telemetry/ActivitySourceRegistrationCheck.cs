using BBT.Workflow.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Telemetry;

/// <summary>
/// Startup check: every ActivitySource this runtime declares and every host must register is
/// actually present in the configuration the process resolved.
/// <para>
/// This exists because the failure it detects is invisible. An unregistered source does not throw
/// and does not warn — <c>ActivitySource.StartActivity</c> returns <c>null</c>, the span is never
/// created, and everything that would have nested under it flattens onto the nearest ancestor. The
/// trace simply looks shallower than it is.
/// </para>
/// <para>
/// It is deliberately NOT the same check as the unit test over the repository's
/// <c>appsettings.json</c> files. That test cannot see a deployment: .NET merges configuration
/// arrays by index, so one <c>Telemetry__Tracing__AdditionalSources__0</c> entry in a chart's
/// free-form environment block silently REPLACES the first declared source rather than appending to
/// it. On a pod configured that way the repository files still look correct and every pipeline span
/// is gone. Only a check that reads the merged configuration can say so.
/// </para>
/// <para>
/// Warns, never throws. A telemetry gap must not stop a runtime from serving traffic, and a startup
/// crash here would turn an observability problem into an outage.
/// </para>
/// </summary>
public sealed class ActivitySourceRegistrationCheck(
    IConfiguration configuration,
    ILogger<ActivitySourceRegistrationCheck> logger) : IHostedService
{
    internal const string ConfigurationPath = "Telemetry:Tracing:AdditionalSources";

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var registered = configuration.GetSection(ConfigurationPath)
            .Get<string[]>() ?? [];

        var missing = RequiredSources()
            .Where(source => !registered.Any(entry => Covers(entry, source)))
            .ToList();

        if (missing.Count > 0)
            logger.ActivitySourcesMissingFromConfiguration(string.Join(", ", missing));

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// The sources every host must carry: everything declared, minus the ones a host deliberately
    /// omits because nothing in it can emit them. Derived from the same map the repository-level
    /// guard uses, so the two cannot drift apart — and host-specific omissions never produce a
    /// warning, which is what keeps this check worth reading.
    /// </summary>
    private static IEnumerable<string> RequiredSources() =>
        TelemetryConstants.ActivitySources.All
            .Where(source =>
                !TelemetryConstants.ActivitySources.DeliberatelyUnregistered.ContainsKey(source));

    private static bool Covers(string entry, string source) =>
        entry.EndsWith('*')
            ? source.StartsWith(entry[..^1], StringComparison.Ordinal)
            : string.Equals(entry, source, StringComparison.Ordinal);
}
