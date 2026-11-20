using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Guids;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Pipeline.Steps;
using BBT.Workflow.Execution.Transitions.Services;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Pipeline.Steps;

/// <summary>
/// vnext#1268: a transition mapping that throws fails the step with the mapper's own error, and the
/// step leaves the exception on the hop — the pipeline's fault incident takes its stack trace from there.
/// </summary>
[Collection(BBT.Workflow.Application.Tests.TracingDetailLevelCollection.Name)]
public class CreateTransitionRecordStepFailureCauseTests
{
    [Fact]
    public async Task MappingThatThrows_FailsTheStep_AndLeavesTheExceptionOnTheHop()
    {
        var thrown = new ArgumentException("the transition mapping found no customer");
        var mappingError = Error.Failure(WorkflowErrorCodes.ExecutionStepFailed, "Failed to execute transition mapping: " + thrown.Message);
        var dataMapper = Substitute.For<ITransitionDataMapper>();
        dataMapper.MapTransitionDataAsync(
                Arg.Any<object?>(), Arg.Any<Transition?>(), Arg.Any<Definitions.Workflow>(),
                Arg.Any<Instance>(), Arg.Any<IRuntimeInfoProvider>(),
                Arg.Any<Dictionary<string, string?>>(), Arg.Any<Action<Exception>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<Action<Exception>?>()!.Invoke(thrown);
                return Result<object?>.Fail(mappingError);
            });
        var step = new CreateTransitionRecordStep(
            Substitute.For<IInstanceTransitionRepository>(),
            Substitute.For<IInstanceRepository>(),
            Substitute.For<IInstanceDataWriteService>(),
            Substitute.For<IGuidGenerator>(),
            dataMapper,
            Substitute.For<IRuntimeInfoProvider>(),
            BBT.Workflow.Application.Files.FileTestDoubles.PassThroughOffload(),
            Substitute.For<ILogger<CreateTransitionRecordStep>>());
        var context = CreateContext();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.ExecutionStepFailed);
        context.FailureCause.ShouldBeSameAs(thrown);
    }

    private static TransitionExecutionContext CreateContext()
    {
        const string json = """
            {
                "type": "F", "timeout": null, "labels": [], "functions": [], "features": [],
                "states": [
                    {"key": "state1", "stateType": "Intermediate", "transitions": [
                        {"key": "approve", "from": "state1", "target": "state2", "triggerType": "Manual", "versionStrategy": "Patch", "labels": [], "onExecutionTasks": []}
                    ]},
                    {"key": "state2", "stateType": "Intermediate", "transitions": []}
                ],
                "sharedTransitions": [], "extensions": [],
                "startTransition": {"key": "start", "from": null, "target": "state1", "triggerType": "Manual", "versionStrategy": "Patch", "labels": [], "onExecutionTasks": [], "view": null}
            }
            """;
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        var workflow = JsonSerializer.Deserialize<Definitions.Workflow>(json, options)!;
        workflow.SetReference(new Reference("test-workflow", "test-domain", "sys-flows", "1.0.0"));
        var instance = Instance.Create(Guid.NewGuid(), "test-workflow", "1.0.0");
        var state = workflow.GetState("state1").Value!;
        instance.ChangeState(state);

        return new TransitionExecutionContext
        {
            InstanceId = instance.Id,
            Domain = "test-domain",
            WorkflowKey = "test-workflow",
            TransitionKey = "approve",
            Trigger = TriggerType.Manual,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = workflow,
            Current = state,
            Transition = Transition.Create("approve", "state1", "state2", TriggerType.Manual, "Patch"),
            Instance = instance,
            Data = new { amount = 1 },
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
    }
}
