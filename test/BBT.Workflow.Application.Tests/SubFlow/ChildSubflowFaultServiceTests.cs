using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.SubFlow;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.SubFlow;

public sealed class ChildSubflowFaultServiceTests
{
    [Fact]
    public async Task FaultChildAsync_ShouldPreserveCascadeOnNestedDownwardEvent()
    {
        var child = InstanceFactory.CreateDefault();
        var grandchildId = Guid.NewGuid();
        child.AddCorrelation(InstanceCorrelation.Create(
            Guid.NewGuid(),
            child.Id,
            "waiting-grandchild",
            grandchildId,
            SubFlowType.SubFlow.Code,
            "grandchild-domain",
            "grandchild-flow",
            "1.0.0"));
        var termination = new TerminationContext(
            TerminationOrigin.ParentCascade,
            Guid.NewGuid(),
            Guid.NewGuid());
        var repository = Substitute.For<IInstanceRepository>();
        repository.FindAsync(child.Id, true, Arg.Any<CancellationToken>())
            .Returns(child);
        var sut = new ChildSubflowFaultService(
            repository,
            Substitute.For<ILogger<ChildSubflowFaultService>>());

        await sut.FaultChildAsync(
            child.Id,
            "child-domain",
            "child-flow",
            Guid.NewGuid(),
            termination,
            CancellationToken.None);

        var nestedRequest = child.GetDomainEvents()
            .Select(domainEvent => domainEvent.Event)
            .OfType<ChildSubflowFaultRequestedEvent>()
            .Single();
        nestedRequest.InstanceId.ShouldBe(grandchildId);
        nestedRequest.Termination.Origin.ShouldBe(TerminationOrigin.ParentCascade);
        nestedRequest.Termination.InitiatorInstanceId.ShouldBe(termination.InitiatorInstanceId);
        nestedRequest.Termination.CascadeId.ShouldBe(termination.CascadeId);
    }
}
