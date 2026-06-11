using System;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.PostCommit;
using BBT.Workflow.Instances;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Execution.Transitions.Context;

/// <summary>
/// Unit tests for the <see cref="ContinuationSet"/> value object and the
/// <see cref="PipelineDirectives.ToContinuations"/> non-consuming projection.
/// </summary>
public class ContinuationsTests
{
    [Fact]
    public void Empty_ShouldHaveNoWork()
    {
        ContinuationSet.Empty.HasWork.ShouldBeFalse();
        ContinuationSet.Empty.Next.ShouldBeNull();
        ContinuationSet.Empty.PostCommitJobs.ShouldBeEmpty();
        ContinuationSet.Empty.TerminalReached.ShouldBeFalse();
        ContinuationSet.Empty.Epilogue.ShouldBe(EpilogueMode.Run);
    }

    [Fact]
    public void ToContinuations_ShouldReflectNextTransitionAndTerminal()
    {
        var directives = new PipelineDirectives();
        directives.RequestNextTransition(new NextTransitionRequest("approve"));
        directives.MarkTerminal();
        directives.RequestEpilogue(EpilogueMode.Skip);

        var continuations = directives.ToContinuations();

        continuations.Next.ShouldNotBeNull();
        continuations.Next!.TransitionKey.ShouldBe("approve");
        continuations.TerminalReached.ShouldBeTrue();
        continuations.Epilogue.ShouldBe(EpilogueMode.Skip);
        continuations.HasWork.ShouldBeTrue();
    }

    [Fact]
    public void ToContinuations_ShouldReflectPostCommitJobs()
    {
        var directives = new PipelineDirectives();
        var job = new StartSubflowJob(Guid.NewGuid(), "Review");
        directives.EnqueuePostCommit(job);

        var continuations = directives.ToContinuations();

        continuations.PostCommitJobs.Count.ShouldBe(1);
        continuations.PostCommitJobs[0].ShouldBe(job);
        continuations.HasWork.ShouldBeTrue();
    }

    [Fact]
    public void ToContinuations_ShouldBePureRead_DoesNotConsumeDirectives()
    {
        var directives = new PipelineDirectives();
        directives.RequestNextTransition(new NextTransitionRequest("approve"));
        directives.EnqueuePostCommit(new StartSubflowJob(Guid.NewGuid(), "Review"));
        directives.RequestResumeFrom(79);

        // Project twice; directives must be untouched between calls.
        var first = directives.ToContinuations();
        var second = directives.ToContinuations();

        first.Next!.TransitionKey.ShouldBe("approve");
        second.Next!.TransitionKey.ShouldBe("approve");
        second.PostCommitJobs.Count.ShouldBe(1);
        second.ResumeFromOrder.ShouldBe(79);

        // The real consume paths still work afterwards.
        directives.ConsumeNextTransition()!.TransitionKey.ShouldBe("approve");
        directives.ConsumePostCommitJobs().Count.ShouldBe(1);
        directives.ConsumeResumeFrom().ShouldBe(79);

        // After consuming, a fresh projection reflects the cleared state.
        var afterConsume = directives.ToContinuations();
        afterConsume.Next.ShouldBeNull();
        afterConsume.PostCommitJobs.ShouldBeEmpty();
        afterConsume.ResumeFromOrder.ShouldBeNull();
        afterConsume.HasWork.ShouldBeFalse();
    }

    [Fact]
    public void ToContinuations_Snapshot_IsStable_AgainstLaterMutation()
    {
        var directives = new PipelineDirectives();
        directives.EnqueuePostCommit(new StartSubflowJob(Guid.NewGuid(), "First"));

        var snapshot = directives.ToContinuations();
        snapshot.PostCommitJobs.Count.ShouldBe(1);

        // Mutating the directives after projecting must not change the snapshot.
        directives.EnqueuePostCommit(new StartSubflowJob(Guid.NewGuid(), "Second"));

        snapshot.PostCommitJobs.Count.ShouldBe(1);
        directives.ToContinuations().PostCommitJobs.Count.ShouldBe(2);
    }
}
