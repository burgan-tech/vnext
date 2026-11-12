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
/// Which transition body append stamps <c>Instances.ModifiedAt</c>. updateData is the one transition
/// whose outcome writes nothing to the instance row, so its data row is the modification and asks the
/// writer to stamp. Every other transition's body is appended without a stamp: its outcome (state change,
/// completion, fault, Busy release) stamps the instance once, and a per-append stamp would only add row
/// versions to the hottest table.
/// </summary>
[Collection(BBT.Workflow.Application.Tests.TracingDetailLevelCollection.Name)]
public class CreateTransitionRecordStepModifiedAtTests
{
    private const string UpdateDataKey = "sync-data";

    private readonly IInstanceDataWriteService _dataWriteService = Substitute.For<IInstanceDataWriteService>();
    private readonly CreateTransitionRecordStep _step;

    public CreateTransitionRecordStepModifiedAtTests()
    {
        var dataMapper = Substitute.For<ITransitionDataMapper>();
        dataMapper.MapTransitionDataAsync(
                Arg.Any<object?>(), Arg.Any<Transition?>(), Arg.Any<Definitions.Workflow>(),
                Arg.Any<Instance>(), Arg.Any<IRuntimeInfoProvider>(),
                Arg.Any<Dictionary<string, string?>>(), Arg.Any<CancellationToken>())
            .Returns(Result<object?>.Ok(JsonDocument.Parse("""{"amount":1}""").RootElement.Clone()));

        _step = new CreateTransitionRecordStep(
            Substitute.For<IInstanceTransitionRepository>(),
            Substitute.For<IInstanceRepository>(),
            _dataWriteService,
            Substitute.For<IGuidGenerator>(),
            dataMapper,
            Substitute.For<IRuntimeInfoProvider>(),
            BBT.Workflow.Application.Files.FileTestDoubles.PassThroughOffload(),
            Substitute.For<ILogger<CreateTransitionRecordStep>>());
    }

    [Fact]
    public async Task UpdateData_body_append_stamps_the_instance()
    {
        var result = await _step.ExecuteAsync(CreateContext(UpdateDataKey, target: "$self"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _dataWriteService.Received(1).AppendAsync(
            Arg.Any<Instance>(), Arg.Any<JsonData>(), Arg.Any<VersionStrategy?>(),
            Arg.Any<CancellationToken>(), Arg.Any<Definitions.Workflow?>(), touchInstance: true);
    }

    [Fact]
    public async Task Ordinary_transition_body_append_does_not_stamp_the_instance()
    {
        var result = await _step.ExecuteAsync(CreateContext("approve", target: "state2"), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _dataWriteService.Received(1).AppendAsync(
            Arg.Any<Instance>(), Arg.Any<JsonData>(), Arg.Any<VersionStrategy?>(),
            Arg.Any<CancellationToken>(), Arg.Any<Definitions.Workflow?>(), touchInstance: false);
    }

    private static TransitionExecutionContext CreateContext(string transitionKey, string target)
    {
        var workflow = CreateWorkflow();
        var instance = Instance.Create(Guid.NewGuid(), "test-workflow", "1.0.0");
        var state = workflow.GetState("state1").Value!;
        instance.ChangeState(state);

        return new TransitionExecutionContext
        {
            InstanceId = instance.Id,
            Domain = "test-domain",
            WorkflowKey = "test-workflow",
            TransitionKey = transitionKey,
            Trigger = TriggerType.Manual,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = workflow,
            Current = state,
            Transition = Transition.Create(transitionKey, "state1", target, TriggerType.Manual, "Patch"),
            Instance = instance,
            Data = null,
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16]
        };
    }

    private static Definitions.Workflow CreateWorkflow()
    {
        var json = """
                   {
                       "type": "F",
                       "timeout": null,
                       "labels": [],
                       "functions": [],
                       "features": [],
                       "states": [
                           {"key": "state1", "stateType": "Intermediate", "transitions": [
                               {"key": "approve", "from": "state1", "target": "state2", "triggerType": "Manual", "versionStrategy": "Patch", "labels": [], "onExecutionTasks": []}
                           ]},
                           {"key": "state2", "stateType": "Intermediate", "transitions": []}
                       ],
                       "sharedTransitions": [],
                       "extensions": [],
                       "startTransition": {"key": "start", "from": null, "target": "state1", "triggerType": "Manual", "versionStrategy": "Patch", "labels": [], "onExecutionTasks": [], "view": null},
                       "updateData": {"key": "sync-data", "target": "$self", "triggerType": "Manual", "versionStrategy": "Patch", "labels": []}
                   }
                   """;
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        var workflow = JsonSerializer.Deserialize<Definitions.Workflow>(json, options)!;
        workflow.SetReference(new Reference("test-workflow", "test-domain", "sys-flows", "1.0.0"));
        return workflow;
    }
}
