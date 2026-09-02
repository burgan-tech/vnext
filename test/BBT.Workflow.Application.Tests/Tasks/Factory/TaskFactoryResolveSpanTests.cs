using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;
using TaskFactory = BBT.Workflow.Tasks.Factory.TaskFactory;

namespace BBT.Workflow.Application.Tests.Tasks.Factory;

/// <summary>
/// Pins the Task.Resolve span: component-ref resolution + clone inside the task factory was the
/// unspanned head of Task.Execute (47.8 ms unattributed in trace 036088b9…). One span per
/// CreateExecutionTaskAsync call, emitted from INSIDE the factory so engine, FanOut and
/// CacheAside call sites are all covered.
/// </summary>
[Collection("TracingDetailLevel")]
public sealed class TaskFactoryResolveSpanTests : IDisposable
{
    private const string TaskSourceName = "BBT.Workflow.Tasks"; // literal — see ShouldListenTo trap

    private readonly ActivityListener _listener;
    private readonly List<Activity> _started = [];

    public TaskFactoryResolveSpanTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == TaskSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = a => { lock (_started) _started.Add(a); }
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        Activity.Current = null;
    }

    [Fact]
    public async Task CreateExecutionTaskAsync_emits_TaskResolve_span()
    {
        // Arrange: componentCacheStore substitute returning a cached HttpTask
        var cacheStore = Substitute.For<IComponentCacheStore>();
        var reference = Substitute.For<IReference>();
        reference.Key.Returns("my-task");
        reference.Domain.Returns("test-domain");
        reference.Flow.Returns("test-flow");
        reference.Version.Returns("1.0.0");

        var cached = HttpTask.CreateEmpty();
        cached.SetReference(reference);

        cacheStore.GetTaskAsync(reference, Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowTask>.Ok(cached));

        var factory = new TaskFactory(cacheStore, NullLogger<TaskFactory>.Instance);

        // Act
        var result = await factory.CreateExecutionTaskAsync(reference);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        Activity? resolve;
        lock (_started) resolve = _started.FirstOrDefault(a => a.OperationName == "Task.Resolve");
        resolve.ShouldNotBeNull();
        resolve.GetTagItem("vnext.task.key").ShouldBe("my-task");
    }
}
