using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Caching;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Transitions.Factory;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using NSubstitute;
using Shouldly;
using Xunit;
using DefWorkflow = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Factory;

/// <summary>
/// Pins the carried-definition contract: when the caller already resolved the workflow for these
/// coordinates, the factory reuses it instead of paying another component-cache resolution — and it
/// still resolves normally when nothing was carried.
/// </summary>
public sealed class TransitionContextFactoryCarriedWorkflowTests
{
    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();
    private readonly IComponentCacheStore _componentCacheStore = Substitute.For<IComponentCacheStore>();
    private readonly IRuntimeInfoProvider _runtimeInfoProvider = Substitute.For<IRuntimeInfoProvider>();

    private TransitionContextFactory Sut() =>
        new(_instanceRepository, _componentCacheStore, _runtimeInfoProvider);

    private static WorkflowExecutionContext AContext(DefWorkflow? carried = null) => new()
    {
        Domain = "core",
        InstanceId = "11111111-1111-1111-1111-111111111111",
        WorkflowKey = "login-flow",
        WorkflowVersion = "1.1.0",
        TransitionKey = "start-login",
        ResolvedWorkflow = carried
    };

    [Fact]
    public async Task WhenTheCallerCarriedTheDefinition_TheFactoryDoesNotResolveItAgain()
    {
        var carried = DefWorkflow.Create();
        // The instance load is irrelevant here; failing it short-circuits the railway AFTER the
        // workflow step, which is the step under test.
        _instanceRepository.GetActiveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<Instance>.Fail(WorkflowErrors.InstanceNotFound("x")));

        await Sut().CreateAsync(AContext(carried), CancellationToken.None);

        await _componentCacheStore.DidNotReceiveWithAnyArgs()
            .GetFlowAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task WhenNothingWasCarried_TheFactoryResolvesTheDefinition()
    {
        _componentCacheStore.GetFlowAsync("core", "login-flow", "1.1.0", Arg.Any<CancellationToken>())
            .Returns(Result<DefWorkflow>.Ok(DefWorkflow.Create()));
        _instanceRepository.GetActiveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<Instance>.Fail(WorkflowErrors.InstanceNotFound("x")));

        await Sut().CreateAsync(AContext(), CancellationToken.None);

        await _componentCacheStore.Received(1)
            .GetFlowAsync("core", "login-flow", "1.1.0", Arg.Any<CancellationToken>());
    }

    [Fact]
    public void TheCarriedDefinitionNeverCrossesAHop()
    {
        // A job payload must not inherit a definition resolved in another hop: the next hop
        // resolves its own, which is what keeps a mid-flight publish visible at the hop boundary.
        var context = AContext(DefWorkflow.Create());

        var json = JsonSerializer.Serialize(context);

        json.ShouldNotContain("ResolvedWorkflow");
        typeof(TransitionJobPayload).GetProperty("ResolvedWorkflow").ShouldBeNull();
    }
}
