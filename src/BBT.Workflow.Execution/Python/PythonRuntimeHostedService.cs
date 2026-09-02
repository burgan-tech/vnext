using BBT.Workflow.Execution.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Execution.Python;

internal sealed class PythonRuntimeHostedService(
    IPythonRuntimeRegistry registry,
    IEnumerable<IPythonExecutionRuntime> runtimes,
    IOptions<PythonOptions> options,
    ILogger<PythonRuntimeHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("Python task execution is disabled");
            return;
        }

        await registry.CheckEnabledRuntimesAsync(cancellationToken);
        logger.LogInformation(
            "Python task execution is ready with modes {Modes}",
            string.Join(", ", options.Value.EnabledModes));
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var runtime in runtimes.OfType<PythonNetExecutionRuntime>())
        {
            runtime.Shutdown();
        }

        return Task.CompletedTask;
    }
}
