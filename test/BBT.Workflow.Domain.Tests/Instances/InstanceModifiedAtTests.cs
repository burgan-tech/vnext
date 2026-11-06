using System;
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// The aggregate stamps <see cref="Instance.ModifiedAt"/> where a transition decides its outcome, so the
/// value rides in the UPDATE that save sends anyway. Aether's audit interceptor never refreshes it: it
/// fills only a null value, and an instance is born with one.
/// </summary>
public class InstanceModifiedAtTests
{
    private static readonly DateTime LongAgo = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Instance InstanceIn(string state)
    {
        var instance = InstanceFactory.CreateDefault();
        instance.ChangeState(StateFactory.CreateDefault(state, StateType.Intermediate));
        instance.ModifiedAt = LongAgo;
        return instance;
    }

    [Fact]
    public void ChangeState_to_another_state_stamps_ModifiedAt_and_reports_the_move()
    {
        var instance = InstanceIn("draft");
        var before = DateTime.UtcNow;

        var moved = instance.ChangeState(StateFactory.CreateDefault("review", StateType.Intermediate));

        moved.ShouldBeTrue();
        instance.ModifiedAt.ShouldNotBeNull();
        instance.ModifiedAt.Value.ShouldBeGreaterThanOrEqualTo(before);
    }

    /// <summary>
    /// A same-state ChangeState changes no column, so it does not stamp; it reports that nothing moved
    /// and the pipeline defers the hop's stamp to the write that closes it (ChangeStateStep).
    /// </summary>
    [Fact]
    public void ChangeState_to_the_same_state_leaves_ModifiedAt_and_reports_no_move()
    {
        var instance = InstanceIn("draft");

        var moved = instance.ChangeState(StateFactory.CreateDefault("draft", StateType.Intermediate));

        moved.ShouldBeFalse();
        instance.ModifiedAt.ShouldBe(LongAgo);
    }

    /// <summary>
    /// A deferred Finish save passes <c>markModified: false</c>: the completion stamps instead.
    /// </summary>
    [Fact]
    public void ChangeState_without_markModified_moves_but_leaves_ModifiedAt()
    {
        var instance = InstanceIn("draft");

        var moved = instance.ChangeState(StateFactory.CreateDefault("review", StateType.Intermediate), markModified: false);

        moved.ShouldBeTrue();
        instance.GetCurrentState.ShouldBe("review");
        instance.ModifiedAt.ShouldBe(LongAgo);
    }

    /// <summary>
    /// A stage, tags or key change is written to the instance row anyway, so it stamps ModifiedAt in the
    /// same UPDATE — and an updateData that changes only these writes no data row that could carry it.
    /// </summary>
    [Fact]
    public void A_stage_tags_or_key_change_stamps_ModifiedAt()
    {
        var staged = InstanceIn("draft");
        staged.SetStage("review");
        staged.ModifiedAt.ShouldNotBe(LongAgo);

        var tagged = InstanceIn("draft");
        tagged.AddTags(["vip"]);
        tagged.ModifiedAt.ShouldNotBe(LongAgo);

        var keyed = InstanceIn("draft");
        keyed.SetKey("another-key");
        keyed.ModifiedAt.ShouldNotBe(LongAgo);
    }

    /// <summary>Setting the same value changes no column, so it must not stamp (no UPDATE of its own).</summary>
    [Fact]
    public void Setting_the_same_stage_tags_or_key_leaves_ModifiedAt()
    {
        var instance = InstanceFactory.CreateDefault("same-key");
        instance.SetStage("review");
        instance.AddTags(["vip"]);
        instance.ModifiedAt = LongAgo;

        instance.SetStage("review");
        instance.AddTags(["vip"]);
        instance.SetKey("same-key");

        instance.ModifiedAt.ShouldBe(LongAgo);
    }

    [Fact]
    public void Complete_stamps_ModifiedAt_with_CompletedAt()
    {
        var instance = InstanceIn("draft");

        instance.Complete("test-domain");

        instance.ModifiedAt.ShouldBe(instance.CompletedAt);
        instance.ModifiedAt.ShouldNotBe(LongAgo);
    }

    [Fact]
    public void Cancel_stamps_ModifiedAt_with_CompletedAt()
    {
        var instance = InstanceIn("draft");

        instance.Cancel("test-domain");

        instance.ModifiedAt.ShouldBe(instance.CompletedAt);
        instance.ModifiedAt.ShouldNotBe(LongAgo);
    }

    [Fact]
    public void Fault_stamps_ModifiedAt_with_CompletedAt()
    {
        var instance = InstanceIn("draft");

        instance.Fault("test-domain");

        instance.ModifiedAt.ShouldBe(instance.CompletedAt);
        instance.ModifiedAt.ShouldNotBe(LongAgo);
    }
}
