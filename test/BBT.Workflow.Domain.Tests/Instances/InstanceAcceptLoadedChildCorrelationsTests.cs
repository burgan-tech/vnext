using System;
using System.Linq;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// Pins <see cref="Instance.AcceptLoadedChildCorrelations"/>: the read-only finders derive the
/// aggregate's correlation collection from one correlation query, and the result must have exactly
/// the shape the detail load's filtered include (<c>!IsCompleted</c>) produced.
/// </summary>
public class InstanceAcceptLoadedChildCorrelationsTests
{
    private static InstanceCorrelation CreateCorrelation(Guid parentId, string type, bool completed)
    {
        var correlation = InstanceCorrelation.Create(
            id: Guid.NewGuid(),
            instanceId: parentId,
            parentState: "review",
            subFlowInstanceId: Guid.NewGuid(),
            subFlowType: type,
            subFlowDomain: "sub-domain",
            subFlowName: "sub-flow",
            subFlowVersion: "1.0.0");

        if (completed)
            correlation.ApplyTerminalOutcome(SubItemTerminalOutcome.Completed,
                new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc));

        return correlation;
    }

    [Fact]
    public void Keeps_only_open_rows_so_the_aggregate_matches_the_filtered_include()
    {
        var instance = Instance.Create(Guid.NewGuid(), "flow", "1.0.0", "key");
        var openSubFlow = CreateCorrelation(instance.Id, "S", completed: false);
        var openSubProcess = CreateCorrelation(instance.Id, "P", completed: false);
        var completedSubFlow = CreateCorrelation(instance.Id, "S", completed: true);

        instance.AcceptLoadedChildCorrelations([completedSubFlow, openSubProcess, openSubFlow]);

        instance.ChildCorrelations.Select(c => c.Id)
            .ShouldBe(new[] { openSubFlow.Id, openSubProcess.Id }.Order(), ignoreOrder: true);
        instance.ChildCorrelations.ShouldNotContain(completedSubFlow);
        instance.Subflow.ShouldBe(openSubFlow);
        instance.HasActiveSubFlow.ShouldBeTrue();
    }

    [Fact]
    public void Does_not_duplicate_a_row_that_is_already_present()
    {
        var instance = Instance.Create(Guid.NewGuid(), "flow", "1.0.0", "key");
        var open = CreateCorrelation(instance.Id, "S", completed: false);

        instance.AcceptLoadedChildCorrelations([open]);
        instance.AcceptLoadedChildCorrelations([open]);

        instance.ChildCorrelations.Count.ShouldBe(1);
    }

    [Fact]
    public void Leaves_the_collection_empty_when_every_row_is_completed()
    {
        var instance = Instance.Create(Guid.NewGuid(), "flow", "1.0.0", "key");

        instance.AcceptLoadedChildCorrelations([CreateCorrelation(instance.Id, "S", completed: true)]);

        instance.ChildCorrelations.ShouldBeEmpty();
        instance.Subflow.ShouldBeNull();
        instance.HasActiveSubFlow.ShouldBeFalse();
    }
}
