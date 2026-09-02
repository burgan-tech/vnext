using BBT.Workflow.Execution.Configuration;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Execution.Python;

public sealed class PythonRuntimeRegistry(
    IEnumerable<IPythonExecutionRuntime> runtimes,
    IOptions<PythonOptions> options) : IPythonRuntimeRegistry
{
    private readonly IReadOnlyDictionary<string, IPythonExecutionRuntime> _runtimes = runtimes.ToDictionary(
        runtime => runtime.Mode,
        StringComparer.OrdinalIgnoreCase);
    private readonly PythonOptions _options = options.Value;

    public IPythonExecutionRuntime GetRequiredRuntime(string mode)
    {
        if (!_options.Enabled)
        {
            throw new PythonExecutionException("Python task execution is disabled.", "runtime_disabled");
        }

        if (!IsEnabled(mode))
        {
            throw new PythonExecutionException(
                $"Python execution mode '{mode}' is disabled.",
                "runtime_disabled");
        }

        return _runtimes.TryGetValue(mode, out var runtime)
            ? runtime
            : throw new PythonExecutionException(
                $"Python execution mode '{mode}' is unavailable.",
                "runtime_unavailable");
    }

    public bool IsEnabled(string mode) =>
        _options.Enabled &&
        _options.EnabledModes.Contains(mode, StringComparer.OrdinalIgnoreCase);

    public async Task CheckEnabledRuntimesAsync(CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled)
        {
            return;
        }

        foreach (var mode in _options.EnabledModes)
        {
            await GetRequiredRuntime(mode).CheckAvailabilityAsync(cancellationToken);
        }
    }
}
