using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Workflow.BackgroundJobs.Handlers;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Instances;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BBT.Workflow.Application.Tests.BackgroundJobs.Handlers;

/// <summary>
/// Pins the timer job span's status against the transition's result. The handler used to discard
/// the <see cref="Result{T}"/> of <c>ExecuteTransitionAsync</c> and close its span <c>Ok</c>
/// unconditionally, so a scheduled transition that failed left a green trace.
/// </summary>
public sealed class TransitionTimerJobHandlerTests
{
    private readonly Mock<IWorkflowExecutionService> _executionService = new();
    private readonly Mock<IInstanceJobRepository> _jobRepo = new();
    private readonly Mock<ICurrentSchema> _currentSchema = new();
    private readonly Mock<ILogger<TransitionTimerJobHandler>> _logger = new();

    public TransitionTimerJobHandlerTests()
    {
        _jobRepo
            .Setup(r => r.MarkAsProcessedAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private TransitionTimerJobHandler CreateHandler()
        => new(_executionService.Object, _jobRepo.Object, _currentSchema.Object, _logger.Object);

    private static TransitionTimerPayload CreatePayload() => new()
    {
        JobName = "timer-abc-expire",
        InstanceId = Guid.NewGuid(),
        TransitionKey = "expire",
        Domain = "test",
        FlowName = "test-flow",
        Version = "1.0.0"
    };

    private static (ActivityListener Listener, List<Activity> Collected) Listen()
    {
        var collected = new List<Activity>();
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "BBT.Workflow.BackgroundJobs",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = collected.Add
        };
        ActivitySource.AddActivityListener(listener);
        return (listener, collected);
    }

    [Fact]
    public async Task HandleAsync_MarksTheJobSpanErrorWhenTheTransitionFails()
    {
        var (listener, collected) = Listen();
        using var _ = listener;
        _executionService
            .Setup(s => s.ExecuteTransitionAsync(
                It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<TransitionOutput>.Fail(new Error("Instance", "Instance:100031", "instance busy")));

        await CreateHandler().HandleAsync(CreatePayload(), CancellationToken.None);

        var job = Assert.Single(collected, a => a.OperationName == "TransitionTimerJob.Execute");
        Assert.Equal(ActivityStatusCode.Error, job.Status);
        Assert.Equal("Instance:100031", job.GetTagItem("error.code"));
    }

    [Fact]
    public async Task HandleAsync_MarksTheJobSpanOkWhenTheTransitionSucceeds()
    {
        var (listener, collected) = Listen();
        using var _ = listener;
        _executionService
            .Setup(s => s.ExecuteTransitionAsync(
                It.IsAny<WorkflowExecutionContext>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<TransitionOutput>.Ok(new TransitionOutput()));

        await CreateHandler().HandleAsync(CreatePayload(), CancellationToken.None);

        var job = Assert.Single(collected, a => a.OperationName == "TransitionTimerJob.Execute");
        Assert.Equal(ActivityStatusCode.Ok, job.Status);
    }
}
