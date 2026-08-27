using System.Diagnostics;
using BBT.Workflow.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Scripting;

/// <summary>
/// Startup warmup for the Roslyn compile pipeline. Compiles one trivial probe mapping in the
/// background so the one-time cold cost — loading the Roslyn assemblies, tier-0 JIT of the
/// compiler pipeline, materializing the default reference set and the compilation template — is
/// paid at process start instead of inside the first real transition's <c>Task.PrepareInput</c>
/// (measured at ~2.8 s on that path before this existed).
/// <para>
/// Fire-and-forget by design: it never blocks host startup, and a failure is logged and swallowed —
/// the first real compile then simply pays the cold cost the warmup would have absorbed. A real
/// compile racing the warmup is harmless: the evaluator's single-flight cache shares or isolates
/// them by cache key as usual. Registered by the Orchestration host (the only host that compiles
/// mapping scripts) behind <c>Workflow:Scripting:WarmupOnStartup</c> (default true).
/// </para>
/// </summary>
public sealed class ScriptEngineWarmupService(
    IServiceScopeFactory scopeFactory,
    ILogger<ScriptEngineWarmupService> logger) : BackgroundService
{
    /// <summary>Configuration switch; default true.</summary>
    public const string EnabledConfigKey = "Workflow:Scripting:WarmupOnStartup";

    /// <summary>
    /// Minimal but representative probe: implements the real <see cref="IMapping"/> contract, so the
    /// compile resolves the same Domain contracts, default usings and default references a real
    /// mapping does.
    /// </summary>
    private const string ProbeSource =
        """
        public class __WarmupProbeMapping : IMapping
        {
            public Task<ScriptResponse> InputHandler(WorkflowTask task, ScriptContext context)
                => Task.FromResult(new ScriptResponse());

            public Task<ScriptResponse> OutputHandler(ScriptContext context)
                => Task.FromResult(new ScriptResponse());
        }
        """;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // IScriptEngine is scoped (a hosted service is a singleton and cannot inject it
            // directly), so the probe compiles inside its own short-lived scope. What the warmup
            // actually heats survives the scope by design: the IEvaluator singleton's type cache,
            // the static compilation templates, and the process-wide Roslyn load + JIT.
            using var scope = scopeFactory.CreateScope();
            var scriptEngine = scope.ServiceProvider.GetRequiredService<IScriptEngine>();

            var startTimestamp = Stopwatch.GetTimestamp();
            await scriptEngine.CompileToInstanceAsync<IMapping>(ProbeSource, cancellationToken: stoppingToken);

            logger.ScriptEngineWarmupCompleted((long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutting down before the warmup finished — nothing to salvage.
        }
        catch (Exception ex)
        {
            logger.ScriptEngineWarmupFailed(ex);
        }
    }
}
