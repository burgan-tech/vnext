using System.Diagnostics;
using System.Text;
using BBT.Workflow.Execution.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Execution.Python;

public sealed class ProcessPythonExecutionRuntime : IPythonExecutionRuntime
{
    private readonly PythonOptions _options;
    private readonly PythonProcessOptions _processOptions;
    private readonly PythonRunnerProtocol _protocol;
    private readonly ILogger<ProcessPythonExecutionRuntime> _logger;
    private readonly SemaphoreSlim _concurrency;

    public ProcessPythonExecutionRuntime(
        IOptions<PythonOptions> options,
        PythonRunnerProtocol protocol,
        ILogger<ProcessPythonExecutionRuntime> logger)
    {
        _options = options.Value;
        _processOptions = options.Value.Process;
        _protocol = protocol;
        _logger = logger;
        _concurrency = new SemaphoreSlim(_processOptions.MaxConcurrency, _processOptions.MaxConcurrency);
    }

    public string Mode => PythonRuntimeModes.Process;

    public async Task<PythonExecutionResult> ExecuteAsync(
        PythonExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        await _concurrency.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(request.Timeout);
            return await ExecuteProcessAsync(request, timeout.Token);
        }
        finally
        {
            _concurrency.Release();
        }
    }

    public async Task CheckAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_processOptions.PythonExecutable))
        {
            throw new PythonExecutionException(
                $"Python executable was not found at '{_processOptions.PythonExecutable}'.",
                "runtime_unavailable");
        }

        if (!File.Exists(_processOptions.RunnerPath))
        {
            throw new PythonExecutionException(
                $"Python runner was not found at '{_processOptions.RunnerPath}'.",
                "runtime_unavailable");
        }

        var smokeRequest = new PythonExecutionRequest(
            "import numpy\nimport pandas\nimport sklearn\ndef main(input):\n    return {'ready': True}",
            "readiness",
            "null",
            TimeSpan.FromSeconds(Math.Min(15, _options.MaxTimeoutSeconds)));
        await ExecuteAsync(smokeRequest, cancellationToken);
    }

    private async Task<PythonExecutionResult> ExecuteProcessAsync(
        PythonExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var requestJson = _protocol.CreateRequestJson(request);
        var startInfo = CreateStartInfo();

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new PythonExecutionException("Python process could not be started.", "runtime_unavailable");
        }

        try
        {
            await process.StandardInput.WriteAsync(requestJson.AsMemory(), cancellationToken);
            process.StandardInput.Close();

            var stdoutTask = ReadLimitedAsync(
                process.StandardOutput,
                _protocol.MaxResponseBytes,
                cancellationToken);
            var stderrTask = ReadLimitedAsync(
                process.StandardError,
                _options.MaxStderrBytes + 64 * 1024,
                cancellationToken);

            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                throw new PythonExecutionException(
                    string.IsNullOrWhiteSpace(stderr)
                        ? $"Python runner exited with code {process.ExitCode}."
                        : $"Python runner exited with code {process.ExitCode}: {stderr}",
                    "process_failed");
            }

            return _protocol.ParseResponse(stdout);
        }
        catch (OperationCanceledException)
        {
            await KillProcessTreeAsync(process);
            throw;
        }
        catch
        {
            await KillProcessTreeAsync(process);
            throw;
        }
    }

    private ProcessStartInfo CreateStartInfo()
    {
        var usePrlimit = OperatingSystem.IsLinux() &&
                         _processOptions.UsePrlimit &&
                         File.Exists(_processOptions.PrlimitExecutable);

        var startInfo = new ProcessStartInfo
        {
            FileName = usePrlimit
                ? _processOptions.PrlimitExecutable
                : _processOptions.PythonExecutable,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (usePrlimit)
        {
            startInfo.ArgumentList.Add($"--as={_processOptions.MemoryBytes}");
            startInfo.ArgumentList.Add($"--cpu={_processOptions.CpuTimeSeconds}");
            startInfo.ArgumentList.Add($"--nofile={_processOptions.OpenFiles}");
            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add(_processOptions.PythonExecutable);
        }

        startInfo.ArgumentList.Add("-I");
        startInfo.ArgumentList.Add(_processOptions.RunnerPath);
        startInfo.Environment["OMP_NUM_THREADS"] = "1";
        startInfo.Environment["OPENBLAS_NUM_THREADS"] = "1";
        startInfo.Environment["MKL_NUM_THREADS"] = "1";
        startInfo.Environment["PIP_NO_INDEX"] = "1";
        startInfo.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
        return startInfo;
    }

    private static async Task<string> ReadLimitedAsync(
        StreamReader reader,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder(Math.Min(maxBytes, 16 * 1024));
        var buffer = new char[4096];
        var totalBytes = 0;

        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0)
            {
                return builder.ToString();
            }

            totalBytes += Encoding.UTF8.GetByteCount(buffer, 0, count);
            if (totalBytes > maxBytes)
            {
                throw new PythonExecutionException(
                    "Python runner response exceeds the configured size limit.",
                    "output_limit_exceeded");
            }

            builder.Append(buffer, 0, count);
        }
    }

    private async Task KillProcessTreeAsync(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to kill Python process tree {ProcessId}", process.Id);
        }
    }
}
