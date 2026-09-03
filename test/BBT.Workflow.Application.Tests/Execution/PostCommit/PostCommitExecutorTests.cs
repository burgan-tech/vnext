using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.BackgroundJobs.Handlers;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.PostCommit;
using BBT.Workflow.Instances;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.PostCommit;

/// <summary>
/// Unit tests for PostCommitExecutor.
/// Focuses on how handler failures are surfaced as fault requests, including the
/// propagation of the originating error's detail (stack trace) into the fault request.
/// </summary>
public class PostCommitExecutorTests
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IServiceProvider _serviceProvider;
    private readonly IPostCommitIdempotencyStore _idempotencyStore;
    private readonly PostCommitExecutor _executor;

    public PostCommitExecutorTests()
    {
        _serviceProvider = Substitute.For<IServiceProvider>();
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(_serviceProvider);
        _scopeFactory = Substitute.For<IServiceScopeFactory>();
        _scopeFactory.CreateScope().Returns(scope);

        _idempotencyStore = Substitute.For<IPostCommitIdempotencyStore>();

        _executor = new PostCommitExecutor(
            _scopeFactory,
            new DefaultPostCommitFailurePolicy(),
            _idempotencyStore,
            Substitute.For<ILogger<PostCommitExecutor>>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenHandlerFaultsWithStackTraceDetail_ShouldPropagateStackTraceToFaultRequest()
    {
        // Arrange — a system error (faulting) whose Detail carries the exception stack trace,
        // mirroring SubFlowInputMappingFailed capturing ex.StackTrace in Error.Detail.
        const string stackTrace = "   at SubFlowMapping.InputHandler(ScriptContext ctx)";
        var error = Error.Failure(
            "Instance:100023",
            "SubFlow 'credit-bureau-inquiry' input mapping failed: " +
            "'System.Dynamic.ExpandoObject' does not contain a definition for 'application'",
            detail: stackTrace);

        var handler = Substitute.For<IPostCommitHandler<TestJob>>();
        handler.HandleAsync(Arg.Any<TestJob>(), Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(Result.Fail(error));
        _serviceProvider.GetService(typeof(IPostCommitHandler<TestJob>)).Returns(handler);

        var context = CreateContext();

        // Act
        var result = await _executor.ExecuteAsync(new IPostCommitJob[] { new TestJob() }, context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.FaultRequest.ShouldNotBeNull();
        result.FaultRequest!.ErrorCode.ShouldBe("Instance:100023");
        result.FaultRequest.StackTrace.ShouldBe(stackTrace);
    }

    [Fact]
    public async Task ExecuteAsync_PostCommitSpan_IsARealChildOfTheCurrentTransition()
    {
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == BackgroundJobActivityHelper.ActivitySource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(listener);

        using var parent = new Activity("TransitionJob.Execute/test").Start();
        ActivityContext observed = default;
        ActivitySpanId observedParent = default;
        ActivityLink[] observedLinks = [];
        var handler = Substitute.For<IPostCommitHandler<TestJob>>();
        handler.HandleAsync(
                Arg.Any<TestJob>(), Arg.Any<TransitionExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                observed = Activity.Current?.Context ?? default;
                observedParent = Activity.Current?.ParentSpanId ?? default;
                observedLinks = Activity.Current?.Links.ToArray() ?? [];
                return Result.Ok();
            });
        _serviceProvider.GetService(typeof(IPostCommitHandler<TestJob>)).Returns(handler);

        var result = await _executor.ExecuteAsync(
            [new TestJob()], CreateContext(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        observed.TraceId.ShouldBe(parent.TraceId);
        observedParent.ShouldBe(parent.SpanId);
        observedLinks.ShouldBeEmpty();
    }

    private static TransitionExecutionContext CreateContext()
    {
        var instanceId = Guid.NewGuid();
        var instance = Instance.Create(instanceId, "test-workflow", "1.0.0", "test-key");

        return new TransitionExecutionContext
        {
            InstanceId = instanceId,
            Instance = instance,
            Workflow = CreateMockWorkflow("test-workflow", "test-domain"),
            Domain = "test-domain",
            WorkflowKey = "test-workflow",
            TransitionKey = "test-transition"
        };
    }

    private static Definitions.Workflow CreateMockWorkflow(string key, string domain)
    {
        var json = """
        {
            "type": "F",
            "timeout": null,
            "labels": [],
            "functions": [],
            "features": [],
            "states": [
                {
                    "key": "state1",
                    "type": "P",
                    "transitions": []
                }
            ],
            "sharedTransitions": [],
            "extensions": [],
            "startTransition": {"key": "start", "from": null, "target": "state1", "triggerType": "Manual", "versionStrategy": "Patch", "labels": [], "onExecutionTasks": [], "view": null}
        }
        """;

        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        var workflow = System.Text.Json.JsonSerializer.Deserialize<Definitions.Workflow>(json, options)!;

        workflow.SetReference(new Reference(key, domain, "sys-flows", "1.0.0"));
        return workflow;
    }

    /// <summary>
    /// Minimal non-idempotent post-commit job used to drive the executor without
    /// touching the idempotency store.
    /// </summary>
    public sealed record TestJob : IPostCommitJob;
}
