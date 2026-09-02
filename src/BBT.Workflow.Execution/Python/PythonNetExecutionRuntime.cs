using System.Collections.Concurrent;
using BBT.Workflow.Execution.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Python.Runtime;

namespace BBT.Workflow.Execution.Python;

public sealed class PythonNetExecutionRuntime : IPythonExecutionRuntime
{
    private static readonly object InitializationLock = new();
    private static readonly ConcurrentDictionary<int, Task> ActiveExecutions = new();
    private static bool _initialized;
    private static bool _threadsEnabled;
    private static bool _shutdown;
    private static int _executionId;

    private readonly PythonNetOptions _options;
    private readonly PythonRunnerProtocol _protocol;
    private readonly ILogger<PythonNetExecutionRuntime> _logger;
    private readonly SemaphoreSlim _concurrency;

    public PythonNetExecutionRuntime(
        IOptions<PythonOptions> options,
        PythonRunnerProtocol protocol,
        ILogger<PythonNetExecutionRuntime> logger)
    {
        _options = options.Value.PythonNet;
        _protocol = protocol;
        _logger = logger;
        _concurrency = new SemaphoreSlim(_options.MaxConcurrency, _options.MaxConcurrency);
    }

    public string Mode => PythonRuntimeModes.PythonNet;

    public void Initialize()
    {
        lock (InitializationLock)
        {
            if (_initialized)
            {
                return;
            }

            if (_shutdown)
            {
                throw new InvalidOperationException(
                    "Python.NET cannot be initialized again after host shutdown.");
            }

            Runtime.PythonDLL = _options.PythonDll;
            if (!string.IsNullOrWhiteSpace(_options.PythonHome))
            {
                PythonEngine.PythonHome = _options.PythonHome;
            }

            var paths = new List<string?>
                {
                    _options.RunnerDirectory,
                    _options.PythonPath,
                    Environment.GetEnvironmentVariable("PYTHONPATH")
                };
            if (!string.IsNullOrWhiteSpace(_options.PythonHome))
            {
                var standardLibrary = Path.Combine(_options.PythonHome, "lib", "python3.12");
                paths.Add(standardLibrary);
                paths.Add(Path.Combine(standardLibrary, "lib-dynload"));
            }

            var configuredPaths = paths
                .Where(path => !string.IsNullOrWhiteSpace(path));
            var pythonPath = string.Join(
                Path.PathSeparator,
                configuredPaths.Distinct(StringComparer.Ordinal));
            Environment.SetEnvironmentVariable("PYTHONPATH", pythonPath);
            if (!string.IsNullOrWhiteSpace(_options.PythonHome))
            {
                PythonEngine.PythonPath = pythonPath;
            }

            PythonEngine.Initialize();
            if (!_threadsEnabled)
            {
                PythonEngine.BeginAllowThreads();
                _threadsEnabled = true;
            }

            _initialized = true;
            _logger.LogInformation("Python.NET runtime initialized with {PythonDll}", _options.PythonDll);
        }
    }

    public async Task<PythonExecutionResult> ExecuteAsync(
        PythonExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        Initialize();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);
        await _concurrency.WaitAsync(timeout.Token);

        ulong pythonThreadId = 0;
        var requestJson = _protocol.CreateRequestJson(request);
        var executionTask = Task.Run(() => ExecuteCore(requestJson, id => pythonThreadId = id));
        var executionId = Interlocked.Increment(ref _executionId);
        ActiveExecutions[executionId] = executionTask;
        _ = executionTask.ContinueWith(
            completed => ActiveExecutions.TryRemove(executionId, out _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        try
        {
            var responseJson = await executionTask.WaitAsync(timeout.Token);
            return _protocol.ParseResponse(responseJson);
        }
        catch (OperationCanceledException)
        {
            if (pythonThreadId != 0)
            {
                try
                {
                    int interrupted;
                    using (Py.GIL())
                    {
                        interrupted = PythonEngine.Interrupt(pythonThreadId);
                    }

                    if (interrupted == 0)
                    {
                        _logger.LogDebug(
                            "Python.NET thread {PythonThreadId} completed before it could be interrupted",
                            pythonThreadId);
                    }

                    try
                    {
                        await executionTask.WaitAsync(TimeSpan.FromSeconds(2));
                    }
                    catch
                    {
                        // The interrupted Python call is expected to fault with KeyboardInterrupt.
                        // Waiting here observes it and prevents host shutdown from racing the GIL.
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Unable to interrupt Python.NET thread {PythonThreadId}", pythonThreadId);
                }
            }

            throw;
        }
        finally
        {
            if (executionTask.IsCompleted)
            {
                _concurrency.Release();
            }
            else
            {
                _ = executionTask.ContinueWith(
                    _ => _concurrency.Release(),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
    }

    public Task CheckAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Initialize();

        using (Py.GIL())
        {
            using var runner = Py.Import("vnext_runner");
            using var numpy = Py.Import("numpy");
            using var pandas = Py.Import("pandas");
            using var sklearn = Py.Import("sklearn");
        }

        return Task.CompletedTask;
    }

    public void Shutdown()
    {
        lock (InitializationLock)
        {
            if (!_initialized)
            {
                return;
            }

            var active = ActiveExecutions.Values.Where(task => !task.IsCompleted).ToArray();
            if (active.Length > 0)
            {
                try
                {
                    Task.WaitAll(active, TimeSpan.FromSeconds(2));
                }
                catch (AggregateException)
                {
                    // Faults from interrupted executions are observed by their invocation paths.
                }
            }

            if (ActiveExecutions.Values.Any(task => !task.IsCompleted))
            {
                _logger.LogWarning(
                    "Skipping Python.NET shutdown because {ActiveCount} native execution(s) are still active",
                    ActiveExecutions.Values.Count(task => !task.IsCompleted));
                return;
            }

            PythonEngine.Shutdown();
            _initialized = false;
            _threadsEnabled = false;
            _shutdown = true;
            _logger.LogInformation("Python.NET runtime shut down");
        }
    }

    private static string ExecuteCore(string requestJson, Action<ulong> setThreadId)
    {
        using (Py.GIL())
        {
            setThreadId(PythonEngine.GetPythonThreadID());
            using var runner = Py.Import("vnext_runner");
            using var request = requestJson.ToPython();
            using var args = new PyTuple([request]);
            using var response = runner.InvokeMethod("execute_json", args);
            return response.As<string>();
        }
    }
}
