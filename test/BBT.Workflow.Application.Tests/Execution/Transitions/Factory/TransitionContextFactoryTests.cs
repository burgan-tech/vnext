using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Transitions.Factory;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Runtime;
using BBT.Workflow.Shared;
using Moq;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Factory;

public class TransitionContextFactoryTests
{
    [Fact]
    public void TerminationContextHelpers_ShouldCreateAndPromoteCascadeContext()
    {
        var instanceId = Guid.NewGuid();

        var direct = TerminationContext.Direct(instanceId);
        var cascade = direct.AsParentCascade();

        direct.Origin.ShouldBe(TerminationOrigin.Direct);
        direct.InitiatorInstanceId.ShouldBe(instanceId);
        direct.CascadeId.ShouldNotBe(Guid.Empty);
        cascade.Origin.ShouldBe(TerminationOrigin.ParentCascade);
        cascade.InitiatorInstanceId.ShouldBe(direct.InitiatorInstanceId);
        cascade.CascadeId.ShouldBe(direct.CascadeId);
    }

    [Fact]
    public void ToExecutionContext_ShouldPreserveTerminationContext()
    {
        var termination = TerminationContext.Direct(Guid.NewGuid());
        var input = new TransitionInput("test-domain", "test-workflow")
        {
            Termination = termination
        };

        var result = input.ToExecutionContext(Guid.NewGuid().ToString(), "1.0.0", "approve");

        result.Termination.ShouldBe(termination);
    }

    [Fact]
    public void EventContracts_ShouldExposeTypedTerminationData()
    {
        var parentId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        var termination = TerminationContext.Direct(parentId).AsParentCascade();
        var canceled = new InstanceSubCanceledEvent
        {
            InstanceId = parentId,
            Domain = "test-domain",
            Flow = "test-workflow",
            Version = "1.0.0",
            SubInstanceId = childId,
            CanceledState = "canceled",
            CanceledAt = DateTime.UtcNow,
            SubItemType = SubItemType.SubProcess,
            Sync = true,
            TerminationOrigin = termination.Origin,
            InitiatorInstanceId = termination.InitiatorInstanceId,
            CascadeId = termination.CascadeId
        };
        var faulted = new InstanceSubFaultedEvent
        {
            InstanceId = parentId,
            Domain = "test-domain",
            Flow = "test-workflow",
            Version = "1.0.0",
            SubInstanceId = childId,
            FaultedState = "faulted",
            FaultedAt = DateTime.UtcNow,
            SubItemType = SubItemType.SubFlow,
            TerminationOrigin = termination.Origin,
            InitiatorInstanceId = termination.InitiatorInstanceId,
            CascadeId = termination.CascadeId
        };
        var legacyFaulted = new InstanceSubFaultedEvent
        {
            InstanceId = parentId,
            Domain = "test-domain",
            Flow = "test-workflow",
            Version = "1.0.0",
            SubInstanceId = childId,
            FaultedState = "faulted",
            FaultedAt = DateTime.UtcNow
        };
        var cancelRequested = new ChildSubflowCancelRequestedEvent
        {
            InstanceId = childId,
            ParentInstanceId = parentId,
            Domain = "test-domain",
            Flow = "test-workflow",
            CompletedAt = DateTime.UtcNow,
            Termination = termination
        };
        var faultRequested = new ChildSubflowFaultRequestedEvent
        {
            InstanceId = childId,
            ParentInstanceId = parentId,
            Domain = "test-domain",
            Flow = "test-workflow",
            FaultedAt = DateTime.UtcNow,
            Termination = termination
        };

        canceled.SubItemType.ShouldBe(SubItemType.SubProcess);
        canceled.TerminationOrigin.ShouldBe(termination.Origin);
        faulted.SubItemType.ShouldBe(SubItemType.SubFlow);
        faulted.CascadeId.ShouldBe(termination.CascadeId);
        legacyFaulted.SubItemType.ShouldBeNull();
        legacyFaulted.TerminationOrigin.ShouldBeNull();
        legacyFaulted.InitiatorInstanceId.ShouldBeNull();
        legacyFaulted.CascadeId.ShouldBeNull();
        cancelRequested.Termination.ShouldBe(termination);
        faultRequested.Termination.ShouldBe(termination);

        var nullability = new NullabilityInfoContext();
        nullability.Create(typeof(ChildSubflowCancelRequestedEvent)
                .GetProperty(nameof(ChildSubflowCancelRequestedEvent.Termination))!)
            .ReadState.ShouldBe(NullabilityState.NotNull);
        nullability.Create(typeof(ChildSubflowFaultRequestedEvent)
                .GetProperty(nameof(ChildSubflowFaultRequestedEvent.Termination))!)
            .ReadState.ShouldBe(NullabilityState.NotNull);
    }

    [Fact]
    public async Task CreateAsync_ShouldPreserveTerminationContext()
    {
        const string domain = "test-domain";
        const string workflowKey = "test-workflow";
        var workflow = CreateWorkflow(workflowKey, domain);
        var instance = Instance.Create(Guid.NewGuid(), workflowKey, workflow.Version);
        instance.ChangeState(workflow.GetState("state1").Value!);
        var termination = new TerminationContext(
            TerminationOrigin.ParentCascade,
            Guid.NewGuid(),
            Guid.NewGuid());
        var input = new WorkflowExecutionContext
        {
            Domain = domain,
            InstanceId = instance.Id.ToString(),
            WorkflowKey = workflowKey,
            WorkflowVersion = workflow.Version,
            TransitionKey = "resume",
            TriggerType = TriggerType.Manual,
            Mode = ExecMode.Resume,
            Termination = termination
        };

        var instanceRepository = new Mock<IInstanceRepository>();
        instanceRepository
            .Setup(x => x.GetActiveAsync(input.InstanceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Instance>.Ok(instance));
        var componentCacheStore = new Mock<IComponentCacheStore>();
        componentCacheStore
            .Setup(x => x.GetFlowAsync(domain, workflowKey, workflow.Version, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Definitions.Workflow>.Ok(workflow));
        var sut = new TransitionContextFactory(
            instanceRepository.Object,
            componentCacheStore.Object,
            Mock.Of<IRuntimeInfoProvider>());

        var result = await sut.CreateAsync(input, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Termination.ShouldBe(termination);
    }

    [Fact]
    public async Task CreateAsync_ShouldPreserveIsPreReserved()
    {
        // A birth-Busy sub-item's start (and every job re-entry) carries IsPreReserved so the
        // Busy-as-mutex admission classifies it as owner re-entry instead of rejecting it with
        // 409 against its own reservation. The async accept path classifies on the FACTORY
        // output, so the flag must survive this mapping.
        const string domain = "test-domain";
        const string workflowKey = "test-workflow";
        var workflow = CreateWorkflow(workflowKey, domain);
        var instance = Instance.Create(Guid.NewGuid(), workflowKey, workflow.Version);
        instance.ChangeState(workflow.GetState("state1").Value!);
        var input = new WorkflowExecutionContext
        {
            Domain = domain,
            InstanceId = instance.Id.ToString(),
            WorkflowKey = workflowKey,
            WorkflowVersion = workflow.Version,
            TransitionKey = "resume",
            TriggerType = TriggerType.Manual,
            Mode = ExecMode.Sync,
            IsPreReserved = true
        };

        var instanceRepository = new Mock<IInstanceRepository>();
        instanceRepository
            .Setup(x => x.GetActiveAsync(input.InstanceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Instance>.Ok(instance));
        var componentCacheStore = new Mock<IComponentCacheStore>();
        componentCacheStore
            .Setup(x => x.GetFlowAsync(domain, workflowKey, workflow.Version, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Definitions.Workflow>.Ok(workflow));
        var sut = new TransitionContextFactory(
            instanceRepository.Object,
            componentCacheStore.Object,
            Mock.Of<IRuntimeInfoProvider>());

        var result = await sut.CreateAsync(input, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.IsPreReserved.ShouldBeTrue();
    }

    [Fact]
    public async Task CreateAsync_ShouldNormalizeHeadersCaseInsensitively()
    {
        const string domain = "test-domain";
        const string workflowKey = "test-workflow";
        var workflow = CreateWorkflow(workflowKey, domain);
        var instance = Instance.Create(Guid.NewGuid(), workflowKey, workflow.Version);
        instance.ChangeState(workflow.GetState("state1").Value!);
        var parentId = Guid.NewGuid().ToString();
        var input = new WorkflowExecutionContext
        {
            Domain = domain,
            InstanceId = instance.Id.ToString(),
            WorkflowKey = workflowKey,
            WorkflowVersion = workflow.Version,
            TransitionKey = "resume",
            TriggerType = TriggerType.Manual,
            Mode = ExecMode.Resume,
            // Upstream hop lower-cased the header key.
            Headers = new Dictionary<string, string?> { ["x-parent-instance-id"] = parentId }
        };

        var instanceRepository = new Mock<IInstanceRepository>();
        instanceRepository
            .Setup(x => x.GetActiveAsync(input.InstanceId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Instance>.Ok(instance));
        var componentCacheStore = new Mock<IComponentCacheStore>();
        componentCacheStore
            .Setup(x => x.GetFlowAsync(domain, workflowKey, workflow.Version, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Definitions.Workflow>.Ok(workflow));
        var sut = new TransitionContextFactory(
            instanceRepository.Object,
            componentCacheStore.Object,
            Mock.Of<IRuntimeInfoProvider>());

        var result = await sut.CreateAsync(input, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        // Canonical (mixed-case) lookup must resolve the lower-cased incoming header.
        result.Value!.Headers.TryGetValue("X-Parent-Instance-Id", out var resolved).ShouldBeTrue();
        resolved.ShouldBe(parentId);
    }

    private static Definitions.Workflow CreateWorkflow(string key, string domain)
    {
        const string json = """
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

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        var workflow = JsonSerializer.Deserialize<Definitions.Workflow>(json, options)!;
        workflow.SetReference(new Reference(key, domain, "sys-flows", "1.0.0"));
        return workflow;
    }
}
