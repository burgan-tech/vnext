using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Discovery;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks;
using BBT.Workflow.Tasks.Coordinator;
using BBT.Workflow.Tasks.Executors;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Executors;

/// <summary>
/// Pins the trigger-family LOCAL invocation contract added for trace readability:
/// (1) the local (same-domain, in-process) branch runs inside an always-on
/// <c>Trigger.Local.{taskKey}</c> span under <c>Task.Execute.*</c> — previously the local branch
/// produced no span at all, unlike the remote branch's Dapr/HTTP client span; and
/// (2) the invocation opens a <see cref="WorkflowTraceLane"/> CHILD lane anchored on that span,
/// so transition jobs the invocation enqueues anchor under the task instead of surfacing as
/// siblings of the executing instance's own hops.
/// </summary>
[Collection("TracingDetailLevel")]
public sealed class TriggerLocalScopeTests : IDisposable
{
    private const string LocalDomain = "test-domain";

    // Literal (not TaskExecutionActivityHelper.ActivitySource.Name): reading the helper's static
    // field inside a listener callback re-enters its type initializer mid-construction.
    private const string TaskSourceName = "BBT.Workflow.Tasks";

    private readonly ActivityListener _listener;

    public TriggerLocalScopeTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TaskSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        Activity.Current = null;
    }

    private static TaskExecutorContext CreateContext(GetInstanceTask task)
    {
        var onExecute = OnExecuteTask.Create(1, task, ScriptCode.FromNative(string.Empty));
        var instance = Instance.Create(Guid.NewGuid(), "test-flow", "1.0", "ctx-key");
        var scriptContext = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
            .SetInstance(instance)
            .Build();

        return new TaskExecutorContext(task, onExecute, scriptContext, null, TaskTrigger.OnExecute, TaskExecutionOrigin.Flow);
    }

    private static GetInstanceTaskExecutor CreateExecutor(
        IInstanceQueryGateway gateway,
        IRuntimeInfoProvider runtime)
        => new(
            Substitute.For<IScriptEngine>(),
            runtime,
            Substitute.For<IRemoteInvokerService>(),
            gateway,
            Substitute.For<IDomainDiscoveryResolver>(),
            NullLogger<GetInstanceTaskExecutor>.Instance);

    [Fact]
    public async Task LocalInvocation_RunsInsideTriggerLocalSpan_WithTargetTags()
    {
        var task = WorkflowTaskFactory.CreateGetInstanceTask(domain: LocalDomain, flow: "test-flow", key: "inst-key");

        Activity? observedDuringCall = null;
        var gateway = Substitute.For<IInstanceQueryGateway>();
        gateway.GetInstanceAsync(Arg.Any<GetInstanceInput>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                observedDuringCall = Activity.Current;
                return ConditionalResult<GetInstanceOutput>.Success(new GetInstanceOutput());
            });

        var runtime = Substitute.For<IRuntimeInfoProvider>();
        runtime.Domain.Returns(LocalDomain);

        var result = await CreateExecutor(gateway, runtime)
            .ExecuteAsync(CreateContext(task), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        observedDuringCall.ShouldNotBeNull();
        observedDuringCall!.OperationName.ShouldStartWith(TaskExecutionActivityHelper.OperationTriggerLocal);
        observedDuringCall.GetTagItem(TelemetryConstants.TagNames.TriggerTargetDomain).ShouldBe(LocalDomain);
        observedDuringCall.GetTagItem(TelemetryConstants.TagNames.TriggerTargetFlow).ShouldBe("test-flow");
        observedDuringCall.GetTagItem(TelemetryConstants.TagNames.TaskType).ShouldBe(nameof(TaskType.GetInstance));
    }

    [Fact]
    public async Task LocalInvocation_AnchorsChildTraceLane_OnTheTriggerLocalSpan_AndRestoresIt()
    {
        var task = WorkflowTaskFactory.CreateGetInstanceTask(domain: LocalDomain, flow: "test-flow", key: "inst-key");

        string? laneDuringCall = null;
        string? spanIdDuringCall = null;
        var gateway = Substitute.For<IInstanceQueryGateway>();
        gateway.GetInstanceAsync(Arg.Any<GetInstanceInput>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                laneDuringCall = WorkflowTraceLane.Current;
                spanIdDuringCall = Activity.Current?.Id;
                return ConditionalResult<GetInstanceOutput>.Success(new GetInstanceOutput());
            });

        var runtime = Substitute.For<IRuntimeInfoProvider>();
        runtime.Domain.Returns(LocalDomain);

        using var outerLane = WorkflowTraceLane.Use("00-11111111111111111111111111111111-1111111111111111-01");

        await CreateExecutor(gateway, runtime).ExecuteAsync(CreateContext(task), CancellationToken.None);

        // Anything enqueued during the local call would stamp the Trigger.Local span as its lane
        // anchor — the exact mechanism a subflow handoff uses.
        laneDuringCall.ShouldNotBeNull();
        laneDuringCall.ShouldBe(spanIdDuringCall);

        // The lane is scoped: after the invocation the outer lane is back.
        WorkflowTraceLane.Current.ShouldBe("00-11111111111111111111111111111111-1111111111111111-01");
    }

    [Fact]
    public async Task RemoteInvocation_DoesNotCreateTriggerLocalSpan()
    {
        var task = WorkflowTaskFactory.CreateGetInstanceTask(domain: "other-domain", flow: "test-flow", key: "inst-key");

        var gateway = Substitute.For<IInstanceQueryGateway>();
        var runtime = Substitute.For<IRuntimeInfoProvider>();
        runtime.Domain.Returns(LocalDomain);

        Activity? triggerLocal = null;
        using var probe = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TaskSourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = a =>
            {
                if (a.OperationName.StartsWith(TaskExecutionActivityHelper.OperationTriggerLocal, StringComparison.Ordinal))
                    triggerLocal = a;
            }
        };
        ActivitySource.AddActivityListener(probe);

        await CreateExecutor(gateway, runtime).ExecuteAsync(CreateContext(task), CancellationToken.None);

        triggerLocal.ShouldBeNull();
        await gateway.DidNotReceiveWithAnyArgs().GetInstanceAsync(default!, default);
    }
}
