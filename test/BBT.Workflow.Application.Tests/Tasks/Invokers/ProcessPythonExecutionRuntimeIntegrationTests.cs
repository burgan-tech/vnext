using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Execution.Configuration;
using BBT.Workflow.Execution.Python;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Tasks.Invokers;

public sealed class ProcessPythonExecutionRuntimeIntegrationTests
{
    [Fact]
    public async Task ExecuteAsync_RunsLockedEnvironmentAndHonorsCancellation_WhenEnabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("VNEXT_PYTHON_PROCESS_TEST"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var executable = Environment.GetEnvironmentVariable("VNEXT_PYTHON_EXECUTABLE");
        var runner = Environment.GetEnvironmentVariable("VNEXT_PYTHON_RUNNER");
        executable.ShouldNotBeNullOrWhiteSpace();
        runner.ShouldNotBeNullOrWhiteSpace();

        var options = Options.Create(new PythonOptions
        {
            Enabled = true,
            EnabledModes = [PythonRuntimeModes.Process],
            Process = new PythonProcessOptions
            {
                PythonExecutable = executable!,
                RunnerPath = runner!,
                UsePrlimit = false,
                MaxConcurrency = 1
            }
        });
        var runtime = new ProcessPythonExecutionRuntime(
            options,
            new PythonRunnerProtocol(options),
            NullLogger<ProcessPythonExecutionRuntime>.Instance);

        var result = await runtime.ExecuteAsync(new PythonExecutionRequest(
            "import os\nimport numpy as np\ndef main(input): print('process'); os.write(1, b'native\\n'); return np.array(input).sum().item()",
            "process.py",
            "[2,3,4]",
            TimeSpan.FromSeconds(10)));

        JsonDocument.Parse(result.OutputJson).RootElement.GetInt32().ShouldBe(9);
        result.Stdout.ShouldBe("process\nnative\n");

        await Should.ThrowAsync<OperationCanceledException>(() => runtime.ExecuteAsync(
            new PythonExecutionRequest(
                "def main(input):\n    while True: pass",
                "timeout.py",
                "null",
                TimeSpan.FromMilliseconds(250))));

        var childMarker = Path.Combine(Path.GetTempPath(), $"vnext-python-child-{Guid.NewGuid():N}");
        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            var markerJson = JsonSerializer.Serialize(new { marker = childMarker });
            await Should.ThrowAsync<OperationCanceledException>(() => runtime.ExecuteAsync(
                new PythonExecutionRequest(
                    """
                    import subprocess
                    import sys
                    def main(input):
                        child = "import pathlib,sys,time; time.sleep(1); pathlib.Path(sys.argv[1]).write_text('alive')"
                        subprocess.Popen([sys.executable, "-c", child, input["marker"]])
                        while True:
                            pass
                    """,
                    "cancel.py",
                    markerJson,
                    TimeSpan.FromSeconds(10)),
                cancellation.Token));

            await Task.Delay(TimeSpan.FromMilliseconds(1500));
            File.Exists(childMarker).ShouldBeFalse("cancellation must kill the whole Python process tree");
        }
        finally
        {
            if (File.Exists(childMarker))
            {
                File.Delete(childMarker);
            }
        }
    }
}
