using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Logging;
using BBT.Workflow.SubFlow;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace BBT.Workflow.Application.Tests.SubFlow;

public sealed class ChildSubflowCancellationServiceTests
{
    [Fact]
    public async Task CancelChildSubflowAsync_ShouldForwardTerminationContextToCancelTransition()
    {
        var instanceId = Guid.NewGuid();
        var termination = new TerminationContext(
            TerminationOrigin.ParentCascade,
            Guid.NewGuid(),
            Guid.NewGuid());
        var cancellationToken = new CancellationTokenSource().Token;
        var gateway = Substitute.For<IInstanceCommandGateway>();
        gateway.TransitionAsync(
                Arg.Any<Guid>(),
                Arg.Any<string>(),
                Arg.Any<TransitionInput>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<TransitionOutput>.Ok(new TransitionOutput()));
        var sut = new ChildSubflowCancellationService(
            gateway,
            Substitute.For<ILogger<ChildSubflowCancellationService>>());

        await sut.CancelChildSubflowAsync(
            instanceId,
            "child-domain",
            "child-flow",
            "1.0.0",
            termination,
            cancellationToken);

        await gateway.Received(1).TransitionAsync(
            instanceId,
            WellKnownTransitionKeys.Cancel,
            Arg.Is<TransitionInput>(input => input.Termination == termination),
            cancellationToken);
    }
}
