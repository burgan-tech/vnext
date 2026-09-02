using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Configuration;
using BBT.Workflow.Execution.Invokers;
using BBT.Workflow.Execution.Python;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Tasks.Invokers;

public sealed class PythonTaskInvokerTests
{
    [Fact]
    public async Task InvokeAsync_ReturnsStrictJsonResultAndRuntimeMetadata()
    {
        var runtime = new StubRuntime(PythonRuntimeModes.Process, (_, _) =>
            Task.FromResult(new PythonExecutionResult(
                "{\"value\":42}", "hello", "", "3.12.13", false, false)));
        var invoker = CreateInvoker(runtime, PythonRuntimeModes.Process);

        var result = await invoker.InvokeAsync(Descriptor(new PythonTaskBinding
        {
            Script = "def main(input): return input",
            ExecutionMode = PythonRuntimeModes.Process,
            Input = JsonDocument.Parse("{\"value\":21}").RootElement.Clone(),
            TimeoutSeconds = 5
        }));

        result.IsSuccess.ShouldBeTrue();
        result.Body.ShouldBe("{\"value\":42}");
        result.Metadata!["ExecutionMode"].ShouldBe(PythonRuntimeModes.Process);
        result.Metadata["RuntimeVersion"].ShouldBe("3.12.13");
    }

    [Fact]
    public async Task InvokeAsync_UsesConfiguredDefaultMode()
    {
        var runtime = new StubRuntime(PythonRuntimeModes.PythonNet, (_, _) =>
            Task.FromResult(new PythonExecutionResult("null", "", "", "3.12", false, false)));
        var invoker = CreateInvoker(runtime, PythonRuntimeModes.PythonNet);

        var result = await invoker.InvokeAsync(Descriptor(new PythonTaskBinding
        {
            Script = "def main(input): return None",
            TimeoutSeconds = 5
        }));

        result.IsSuccess.ShouldBeTrue();
        runtime.InvocationCount.ShouldBe(1);
    }

    [Fact]
    public async Task InvokeAsync_TimeoutReturnsFailure()
    {
        var runtime = new StubRuntime(PythonRuntimeModes.Process, async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        });
        var invoker = CreateInvoker(runtime, PythonRuntimeModes.Process, maxTimeoutSeconds: 2);

        var result = await invoker.InvokeAsync(Descriptor(new PythonTaskBinding
        {
            Script = "def main(input): return input",
            TimeoutSeconds = 1
        }));

        result.IsSuccess.ShouldBeFalse();
        result.StatusCode.ShouldBe(408);
        result.Metadata!["Reason"].ShouldBe("timeout");
    }

    [Fact]
    public async Task InvokeAsync_DisabledModeDoesNotFallback()
    {
        var options = Options.Create(new PythonOptions
        {
            Enabled = true,
            DefaultMode = PythonRuntimeModes.PythonNet,
            EnabledModes = [PythonRuntimeModes.PythonNet]
        });
        var runtimes = new IPythonExecutionRuntime[]
        {
            new StubRuntime(PythonRuntimeModes.PythonNet, (_, _) =>
                Task.FromResult(new PythonExecutionResult("null", "", "", "3.12", false, false)))
        };
        var invoker = new PythonTaskInvoker(
            new PythonRuntimeRegistry(runtimes, options),
            options,
            NullLogger<PythonTaskInvoker>.Instance);

        var result = await invoker.InvokeAsync(Descriptor(new PythonTaskBinding
        {
            Script = "def main(input): return input",
            ExecutionMode = PythonRuntimeModes.Container,
            TimeoutSeconds = 5
        }));

        result.IsSuccess.ShouldBeFalse();
        result.StatusCode.ShouldBe(503);
        result.Metadata!["Reason"].ShouldBe("runtime_disabled");
    }

    [Fact]
    public async Task InvokeAsync_PythonFailureIncludesRuntimeVersionWithoutPayloadLoggingMetadata()
    {
        var runtime = new StubRuntime(PythonRuntimeModes.Process, (_, _) =>
            throw new PythonExecutionException(
                "model failed",
                "execution_error",
                "ValueError",
                "3.12.13"));
        var invoker = CreateInvoker(runtime, PythonRuntimeModes.Process);

        var result = await invoker.InvokeAsync(Descriptor(new PythonTaskBinding
        {
            Script = "def main(input): raise ValueError('model failed')",
            Input = JsonDocument.Parse("{\"secret\":true}").RootElement.Clone(),
            TimeoutSeconds = 5
        }));

        result.IsSuccess.ShouldBeFalse();
        result.Metadata!["RuntimeVersion"].ShouldBe("3.12.13");
        result.Metadata.ShouldNotContainKey("Script");
        result.Metadata.ShouldNotContainKey("Input");
        result.Metadata.ShouldNotContainKey("Output");
    }

    [Theory]
    [InlineData("unknown", "inline.py")]
    [InlineData("process", "/tmp/task.py")]
    public async Task InvokeAsync_RejectsInvalidModeAndFilesystemLocation(string mode, string location)
    {
        var runtime = new StubRuntime(PythonRuntimeModes.Process, (_, _) =>
            Task.FromResult(new PythonExecutionResult("null", "", "", "3.12", false, false)));
        var invoker = CreateInvoker(runtime, PythonRuntimeModes.Process);

        var result = await invoker.InvokeAsync(Descriptor(new PythonTaskBinding
        {
            Script = "def main(input): return input",
            ExecutionMode = mode,
            Location = location,
            TimeoutSeconds = 5
        }));

        result.IsSuccess.ShouldBeFalse();
        result.StatusCode.ShouldBe(400);
        runtime.InvocationCount.ShouldBe(0);
    }

    [Fact]
    public async Task InvokeAsync_EmitsPayloadFreeTraceAndMetricDimensions()
    {
        Activity? stoppedActivity = null;
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PythonTaskTelemetry.InstrumentationName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (Equals(activity.GetTagItem("task.key"), "python-telemetry-task"))
                {
                    stoppedActivity = activity;
                }
            }
        };
        ActivitySource.AddActivityListener(activityListener);

        var measurements = new List<(string Name, Dictionary<string, object?> Tags)>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == PythonTaskTelemetry.InstrumentationName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            measurements.Add((instrument.Name, CopyTags(tags))));
        meterListener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            measurements.Add((instrument.Name, CopyTags(tags))));
        meterListener.Start();

        var runtime = new StubRuntime(PythonRuntimeModes.Process, (_, _) =>
            Task.FromResult(new PythonExecutionResult("{\"ok\":true}", "", "", "3.12.13", false, false)));
        var invoker = CreateInvoker(runtime, PythonRuntimeModes.Process);

        var result = await invoker.InvokeAsync(Descriptor(
            new PythonTaskBinding
            {
                Script = "def main(input): return {'ok': True}",
                Input = JsonDocument.Parse("{\"secret\":true}").RootElement.Clone(),
                TimeoutSeconds = 5
            },
            "python-telemetry-task"));

        result.IsSuccess.ShouldBeTrue();
        stoppedActivity.ShouldNotBeNull();
        stoppedActivity!.Status.ShouldBe(ActivityStatusCode.Ok);
        stoppedActivity.GetTagItem("task.type").ShouldBe(TaskTypes.Python);
        stoppedActivity.GetTagItem("python.execution_mode").ShouldBe(PythonRuntimeModes.Process);
        stoppedActivity.GetTagItem("python.runtime_version").ShouldBe("3.12.13");
        stoppedActivity.GetTagItem("status").ShouldBe("success");
        stoppedActivity.Tags.Any(tag =>
            tag.Key == "python.script" || tag.Key == "python.input" || tag.Key == "python.output").ShouldBeFalse();

        var relevantMeasurements = measurements.FindAll(measurement =>
            measurement.Tags.GetValueOrDefault("python.runtime_version")?.ToString() == "3.12.13");
        relevantMeasurements.Count.ShouldBeGreaterThanOrEqualTo(2);
        relevantMeasurements.ShouldAllBe(measurement =>
            measurement.Tags["task.type"]!.ToString() == TaskTypes.Python &&
            measurement.Tags["python.execution_mode"]!.ToString() == PythonRuntimeModes.Process &&
            measurement.Tags["python.runtime_version"]!.ToString() == "3.12.13" &&
            measurement.Tags["status"]!.ToString() == "success");
    }

    private static PythonTaskInvoker CreateInvoker(
        IPythonExecutionRuntime runtime,
        string defaultMode,
        int maxTimeoutSeconds = 50)
    {
        var options = Options.Create(new PythonOptions
        {
            Enabled = true,
            DefaultMode = defaultMode,
            EnabledModes = [runtime.Mode],
            MaxTimeoutSeconds = maxTimeoutSeconds
        });
        return new PythonTaskInvoker(
            new PythonRuntimeRegistry([runtime], options),
            options,
            NullLogger<PythonTaskInvoker>.Instance);
    }

    private static TaskDescriptor<PythonTaskBinding> Descriptor(
        PythonTaskBinding binding,
        string taskKey = "python-task") => new()
    {
        TaskType = TaskTypes.Python,
        TaskKey = taskKey,
        Binding = binding
    };

    private static Dictionary<string, object?> CopyTags(
        ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var result = new Dictionary<string, object?>();
        foreach (var tag in tags)
        {
            result[tag.Key] = tag.Value;
        }

        return result;
    }

    private sealed class StubRuntime(
        string mode,
        Func<PythonExecutionRequest, CancellationToken, Task<PythonExecutionResult>> handler)
        : IPythonExecutionRuntime
    {
        public string Mode { get; } = mode;
        public int InvocationCount { get; private set; }

        public Task<PythonExecutionResult> ExecuteAsync(
            PythonExecutionRequest request,
            CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            return handler(request, cancellationToken);
        }

        public Task CheckAvailabilityAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
