using System;
using System.Diagnostics;
using System.Threading.Tasks;
using BBT.Workflow.Logging;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Telemetry;

/// <summary>
/// Pins the ambient trace-lane anchor: scoping/restore, the deliberate "null preserves" rule,
/// async flow, and the subflow child-lane transition.
/// </summary>
public sealed class WorkflowTraceLaneTests
{
    private const string AnchorA = "00-11111111111111111111111111111111-1111111111111111-01";
    private const string AnchorB = "00-22222222222222222222222222222222-2222222222222222-01";

    [Fact]
    public void Use_sets_the_anchor_and_dispose_restores_the_previous_one()
    {
        WorkflowTraceLane.Current.ShouldBeNull();

        using (WorkflowTraceLane.Use(AnchorA))
        {
            WorkflowTraceLane.Current.ShouldBe(AnchorA);

            using (WorkflowTraceLane.Use(AnchorB))
            {
                WorkflowTraceLane.Current.ShouldBe(AnchorB);
            }

            WorkflowTraceLane.Current.ShouldBe(AnchorA);
        }

        WorkflowTraceLane.Current.ShouldBeNull();
    }

    [Fact]
    public void Use_with_a_null_anchor_preserves_the_enclosing_lane()
    {
        // A legacy payload carries no anchor. Inside a live HTTP request's lane it must still
        // flatten into that lane rather than starting a nested one.
        using (WorkflowTraceLane.Use(AnchorA))
        using (WorkflowTraceLane.Use(null))
        {
            WorkflowTraceLane.Current.ShouldBe(AnchorA);
        }
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var scope = WorkflowTraceLane.Use(AnchorA);
        scope.Dispose();
        scope.Dispose();

        WorkflowTraceLane.Current.ShouldBeNull();
    }

    [Fact]
    public async Task Anchor_flows_across_await_and_into_Task_Run()
    {
        using (WorkflowTraceLane.Use(AnchorA))
        {
            await Task.Yield();
            WorkflowTraceLane.Current.ShouldBe(AnchorA);

            var observed = await Task.Run(() => WorkflowTraceLane.Current);
            observed.ShouldBe(AnchorA);
        }
    }

    [Fact]
    public void UseCurrentActivity_without_an_ambient_activity_yields_no_anchor()
    {
        Activity.Current = null;

        using (WorkflowTraceLane.UseCurrentActivity())
        {
            WorkflowTraceLane.Current.ShouldBeNull();
        }
    }

    [Fact]
    public void UseCurrentActivity_anchors_on_the_ambient_span_and_keeps_the_parent_lane()
    {
        using (WorkflowTraceLane.Use(AnchorA, AnchorB))
        {
            var ambient = new Activity("server");
            ambient.SetIdFormat(ActivityIdFormat.W3C);
            ambient.Start();
            try
            {
                using (WorkflowTraceLane.UseCurrentActivity())
                {
                    WorkflowTraceLane.Current.ShouldBe(ambient.Id);
                    WorkflowTraceLane.ParentLane.ShouldBe(AnchorB);
                }
            }
            finally
            {
                ambient.Stop();
                Activity.Current = null;
            }
        }
    }

    [Fact]
    public void EnterChildLane_makes_the_handing_off_span_the_child_anchor_and_records_the_lane_left()
    {
        using (WorkflowTraceLane.Use(AnchorA))
        {
            var handoff = new Activity("PostCommit.ForwardToSubflowJob");
            handoff.SetIdFormat(ActivityIdFormat.W3C);
            handoff.Start();
            try
            {
                using (WorkflowTraceLane.EnterChildLane())
                {
                    // The subflow's hops anchor here — flat underneath the forward span...
                    WorkflowTraceLane.Current.ShouldBe(handoff.Id);
                    // ...and the resume knows which lane to return to.
                    WorkflowTraceLane.ParentLane.ShouldBe(AnchorA);
                }

                WorkflowTraceLane.Current.ShouldBe(AnchorA);
                WorkflowTraceLane.ParentLane.ShouldBeNull();
            }
            finally
            {
                handoff.Stop();
                Activity.Current = null;
            }
        }
    }

    [Fact]
    public void Nested_child_lanes_keep_depth_at_one_level_per_subflow()
    {
        // A -> B -> C: every level anchors on the span that handed off to it, and knows only the
        // lane directly above. Depth grows with subflow nesting, never with chain length.
        var forwardB = new Activity("PostCommit.ForwardToSubflowJob/B");
        forwardB.SetIdFormat(ActivityIdFormat.W3C);
        forwardB.Start();

        using (WorkflowTraceLane.Use(AnchorA))
        using (WorkflowTraceLane.EnterChildLane())
        {
            WorkflowTraceLane.Current.ShouldBe(forwardB.Id);
            WorkflowTraceLane.ParentLane.ShouldBe(AnchorA);

            forwardB.Stop();
            var forwardC = new Activity("PostCommit.ForwardToSubflowJob/C");
            forwardC.SetIdFormat(ActivityIdFormat.W3C);
            forwardC.Start();
            try
            {
                using (WorkflowTraceLane.EnterChildLane())
                {
                    WorkflowTraceLane.Current.ShouldBe(forwardC.Id);
                    WorkflowTraceLane.ParentLane.ShouldBe(forwardB.Id);
                }
            }
            finally
            {
                forwardC.Stop();
                Activity.Current = null;
            }
        }
    }
}
