using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Transitions.Factory;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using NSubstitute;
using Xunit;
using DefWorkflow = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.Application.Tests.Execution.Transitions.Factory;

/// <summary>
/// vnext#1252: the reserved cancel is the one request a Faulted instance admits, so only a cancel loads the instance
/// through <see cref="IInstanceRepository.GetActiveOrFaultedAsync"/>; every other request keeps the terminal guard
/// of <see cref="IInstanceRepository.GetActiveAsync"/>.
/// </summary>
public sealed class TransitionContextFactoryFaultedCancelTests
{
    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();
    private readonly IComponentCacheStore _componentCacheStore = Substitute.For<IComponentCacheStore>();

    public TransitionContextFactoryFaultedCancelTests()
    {
        // The load result is irrelevant: failing it ends the railway right after the step under test.
        _instanceRepository.GetActiveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<Instance>.Fail(WorkflowErrors.InstanceNotFound("x")));
        _instanceRepository.GetActiveOrFaultedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<Instance>.Fail(WorkflowErrors.InstanceNotFound("x")));
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("Cancel")]
    public async Task TheReservedCancel_LoadsAFaultedInstanceToo(string transitionKey)
    {
        await Sut().CreateAsync(AContext(transitionKey), CancellationToken.None);

        await _instanceRepository.Received(1).GetActiveOrFaultedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _instanceRepository.DidNotReceive().GetActiveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnAsyncCancel_LoadsOnlyAnActiveInstance()
    {
        // An async cancel reaching a Faulted row faulted after the intake's snapshot; queued, its job would be
        // cancelled by the fault's own cleanup, so it is refused and runs inline when sent again.
        await Sut().CreateAsync(AContext("cancel", ExecMode.Async), CancellationToken.None);

        await _instanceRepository.Received(1).GetActiveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _instanceRepository.DidNotReceive().GetActiveOrFaultedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("approve")]
    [InlineData("exit")]
    [InlineData("update-parent-data")]
    public async Task AnyOtherKey_LoadsOnlyAnActiveInstance(string transitionKey)
    {
        await Sut().CreateAsync(AContext(transitionKey), CancellationToken.None);

        await _instanceRepository.Received(1).GetActiveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _instanceRepository.DidNotReceive().GetActiveOrFaultedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private TransitionContextFactory Sut() =>
        new(_instanceRepository, _componentCacheStore, Substitute.For<IRuntimeInfoProvider>());

    private static WorkflowExecutionContext AContext(string transitionKey, ExecMode mode = ExecMode.Sync) => new()
    {
        Mode = mode,
        Domain = "core",
        InstanceId = "11111111-1111-1111-1111-111111111111",
        WorkflowKey = "order-flow",
        WorkflowVersion = "1.0.0",
        TransitionKey = transitionKey,
        ResolvedWorkflow = DefWorkflow.Create()
    };
}
