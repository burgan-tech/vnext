using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BBT.Workflow.Execution.Configuration;
using BBT.Workflow.Execution.Python;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Tasks.Invokers;

public sealed class PythonNetExecutionRuntimeIntegrationTests
{
    [Fact]
    public async Task ExecuteAsync_RunsNumpyThroughEmbeddedCpython_WhenEnabled()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("VNEXT_PYTHONNET_TEST"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        var pythonDll = Environment.GetEnvironmentVariable("VNEXT_PYTHONNET_DLL");
        var pythonHome = Environment.GetEnvironmentVariable("VNEXT_PYTHONNET_HOME");
        var pythonPath = Environment.GetEnvironmentVariable("VNEXT_PYTHONNET_PATH");
        var runnerDirectory = Environment.GetEnvironmentVariable("VNEXT_PYTHON_RUNNER_DIRECTORY");
        pythonDll.ShouldNotBeNullOrWhiteSpace();
        pythonHome.ShouldNotBeNullOrWhiteSpace();
        pythonPath.ShouldNotBeNullOrWhiteSpace();
        runnerDirectory.ShouldNotBeNullOrWhiteSpace();

        var options = Options.Create(new PythonOptions
        {
            Enabled = true,
            EnabledModes = [PythonRuntimeModes.PythonNet],
            PythonNet = new PythonNetOptions
            {
                PythonDll = pythonDll!,
                PythonHome = pythonHome!,
                PythonPath = pythonPath,
                RunnerDirectory = runnerDirectory!,
                MaxConcurrency = 4
            }
        });
        var protocol = new PythonRunnerProtocol(options);
        var runtime = new PythonNetExecutionRuntime(
            options,
            protocol,
            NullLogger<PythonNetExecutionRuntime>.Instance);

        try
        {
            runtime.Initialize();
            runtime.Initialize();
            await runtime.CheckAvailabilityAsync();
            var result = await runtime.ExecuteAsync(new PythonExecutionRequest(
                "import numpy as np\ndef main(input): return np.array(input).sum().item()",
                "embedded.py",
                "[1,2,3]",
                TimeSpan.FromSeconds(10)));

            JsonDocument.Parse(result.OutputJson).RootElement.GetInt32().ShouldBe(6);
            result.RuntimeVersion.ShouldStartWith("3.12");

            var parallelResults = await Task.WhenAll(Enumerable.Range(1, 8).Select(value =>
                runtime.ExecuteAsync(new PythonExecutionRequest(
                    "import numpy as np\ndef main(input): return np.array(input).sum().item()",
                    $"parallel-{value}.py",
                    $"[{value},{value}]",
                    TimeSpan.FromSeconds(10)))));
            parallelResults.Select(item => JsonDocument.Parse(item.OutputJson).RootElement.GetInt32())
                .ShouldBe(Enumerable.Range(1, 8).Select(value => value * 2));

            const string isolationScript = """
                try:
                    invocation_count += 1
                except NameError:
                    invocation_count = 1
                def main(input):
                    return invocation_count
                """;
            var firstScope = await runtime.ExecuteAsync(new PythonExecutionRequest(
                isolationScript, "isolation-1.py", "null", TimeSpan.FromSeconds(10)));
            var secondScope = await runtime.ExecuteAsync(new PythonExecutionRequest(
                isolationScript, "isolation-2.py", "null", TimeSpan.FromSeconds(10)));
            firstScope.OutputJson.ShouldBe("1");
            secondScope.OutputJson.ShouldBe("1");

            await Should.ThrowAsync<OperationCanceledException>(() => runtime.ExecuteAsync(
                new PythonExecutionRequest(
                    "def main(input):\n    while True: pass",
                    "timeout.py",
                    "null",
                    TimeSpan.FromMilliseconds(250))));
        }
        finally
        {
            runtime.Shutdown();
        }
    }
}
