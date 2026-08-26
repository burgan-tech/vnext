using BBT.Workflow.Execution.Configuration;
using BBT.Workflow.Execution.Python.Containers;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Execution.Python;

public sealed class ContainerPythonExecutionRuntime : IPythonExecutionRuntime
{
    private readonly PythonContainerOptions _containerOptions;
    private readonly PythonRunnerProtocol _protocol;
    private readonly IContainerExecutionDriver _driver;
    private readonly SemaphoreSlim _concurrency;

    public ContainerPythonExecutionRuntime(
        IOptions<PythonOptions> options,
        PythonRunnerProtocol protocol,
        IEnumerable<IContainerExecutionDriver> drivers)
    {
        _containerOptions = options.Value.Container;
        _protocol = protocol;
        _driver = drivers.FirstOrDefault(driver =>
                      string.Equals(driver.Name, _containerOptions.Driver, StringComparison.OrdinalIgnoreCase))
                  ?? throw new InvalidOperationException(
                      $"Container execution driver '{_containerOptions.Driver}' is not registered.");
        _concurrency = new SemaphoreSlim(
            _containerOptions.MaxConcurrency,
            _containerOptions.MaxConcurrency);
    }

    public string Mode => PythonRuntimeModes.Container;

    public async Task<PythonExecutionResult> ExecuteAsync(
        PythonExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        await _concurrency.WaitAsync(cancellationToken);
        try
        {
            var requestJson = _protocol.CreateRequestJson(request);
            var result = await _driver.ExecuteAsync(
                new ContainerExecutionRequest(
                    _containerOptions.Image,
                    requestJson,
                    request.Timeout,
                    _containerOptions.MemoryBytes,
                    _containerOptions.NanoCpus,
                    _containerOptions.PidsLimit,
                    _containerOptions.TmpfsBytes,
                    _containerOptions.NetworkMode,
                    _protocol.MaxResponseBytes,
                    new Dictionary<string, string>
                    {
                        ["OMP_NUM_THREADS"] = "1",
                        ["OPENBLAS_NUM_THREADS"] = "1",
                        ["MKL_NUM_THREADS"] = "1",
                        ["PIP_NO_INDEX"] = "1",
                        ["PYTHONDONTWRITEBYTECODE"] = "1"
                    }),
                cancellationToken);

            if (result.ExitCode != 0)
            {
                throw new PythonExecutionException(
                    string.IsNullOrWhiteSpace(result.Stderr)
                        ? $"Python container exited with code {result.ExitCode}."
                        : $"Python container exited with code {result.ExitCode}: {result.Stderr}",
                    "container_failed");
            }

            return _protocol.ParseResponse(result.Stdout);
        }
        finally
        {
            _concurrency.Release();
        }
    }

    public Task CheckAvailabilityAsync(CancellationToken cancellationToken = default) =>
        _driver.CheckAvailabilityAsync(cancellationToken);
}
