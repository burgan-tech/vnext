using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Testing;
using BBT.Workflow.Definitions;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// Base test class for testing IInstanceTransitionRepository implementations.
/// Contains test methods that verify the repository contract and behavior.
/// </summary>
public abstract class InstanceTransitionRepositoryTests<TEntry> : DomainTestBase<TEntry>
    where TEntry : ModuleEntryPointBase, new()
{
    protected IInstanceTransitionRepository Repository => GetRequiredService<IInstanceTransitionRepository>();
    protected IInstanceRepository InstanceRepository => GetRequiredService<IInstanceRepository>();

    /// <summary>
    /// Tests that InsertAsync successfully creates a new instance transition in the database.
    /// </summary>
    [Fact]
    public async Task InsertAsync_ShouldCreateTransition()
    {
        // Arrange
        var instance = await CreateTestInstanceAsync();
        var transition = InstanceTransition.Create(
            Guid.NewGuid(),
            instance.Id,
            "test-transition",
            "InitialState",
            TriggerType.Manual,
            JsonData.CreateFrom("{}"),
            JsonData.CreateFrom("{}")
        );

        // Act
        var result = await Repository.InsertAsync(transition, true);

        // Assert
        result.ShouldNotBeNull();
        result.Id.ShouldBe(transition.Id);
        result.InstanceId.ShouldBe(instance.Id);
        result.FromState.ShouldBe("InitialState");
    }

    /// <summary>
    /// Tests that UpdateAsync successfully updates an existing instance transition.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_ShouldUpdateTransition()
    {
        // Arrange
        var instance = await CreateTestInstanceAsync();
        var transition = InstanceTransition.Create(
            Guid.NewGuid(),
            instance.Id,
            "test-transition",
            "InitialState",
            TriggerType.Manual,
            JsonData.CreateFrom("{}"),
            JsonData.CreateFrom("{}")
        );
        await Repository.InsertAsync(transition, true);

        // Act
        transition.Completed("FinalState");
        var result = await Repository.UpdateAsync(transition);

        // Assert
        result.ShouldNotBeNull();
        result.ToState.ShouldBe("FinalState");
        result.FinishedAt.ShouldNotBeNull();
    }

    /// <summary>
    /// Tests that UpdateCompletedAsync successfully updates transition completion details.
    /// </summary>
    [Fact]
    public async Task UpdateCompletedAsync_ShouldUpdateCompletionDetails()
    {
        // Arrange
        var instance = await CreateTestInstanceAsync();
        var transition = InstanceTransition.Create(
            Guid.NewGuid(),
            instance.Id,
            "test-transition",
            "InitialState",
            TriggerType.Manual,
            JsonData.CreateFrom("{}"),
            JsonData.CreateFrom("{}")
        );
        await Repository.InsertAsync(transition, true);

        // Act
        transition.Completed(
            "CompletedState",
            effectiveState: "sub-effective",
            effectiveStateType: StateType.Intermediate,
            effectiveStateSubType: StateSubType.Success,
            stage: "review");
        await Repository.UpdateCompletedAsync(transition, CancellationToken.None);

        // Assert — ALL columns Completed() mutates must be written by the set-based statement
        // itself. Three of them used to survive only via a tracked-entity flush, which both hid
        // the gap and double-wrote the row; this pin keeps the statement self-sufficient.
        // Forget the tracked (already mutated) entity so the read below comes from the store.
        await ForgetTrackedEntitiesAsync();
        var updated = await Repository.GetAsync(transition.Id);
        updated.ShouldNotBeNull();
        updated.ToState.ShouldBe("CompletedState");
        updated.EffectiveState.ShouldBe("sub-effective");
        updated.EffectiveStateType.ShouldBe(StateType.Intermediate);
        updated.EffectiveStateSubType.ShouldBe(StateSubType.Success);
        updated.Stage.ShouldBe("review");
        updated.FinishedAt.ShouldNotBeNull();
        updated.Duration.ShouldNotBeNull();
    }

    /// <summary>
    /// Tests that DeleteAsync successfully removes an instance transition from the database.
    /// </summary>
    [Fact]
    public async Task DeleteAsync_ShouldRemoveTransition()
    {
        // Arrange
        var instance = await CreateTestInstanceAsync();
        var transition = InstanceTransition.Create(
            Guid.NewGuid(),
            instance.Id,
            "test-transition",
            "InitialState",
            TriggerType.Manual,
            JsonData.CreateFrom("{}"),
            JsonData.CreateFrom("{}")
        );
        await Repository.InsertAsync(transition, true);

        // Act
        await Repository.DeleteAsync(transition, true);

        // Assert
        var deleted = await Repository.FindAsync(transition.Id);
        deleted.ShouldBeNull();
    }

    /// <summary>
    /// Drops every entity the implementation tracks, so a following read reflects only what reached the
    /// store. No-op for implementations without a change tracker.
    /// </summary>
    protected virtual Task ForgetTrackedEntitiesAsync() => Task.CompletedTask;

    /// <summary>
    /// Helper method to create a test instance for transition tests.
    /// </summary>
    private async Task<Instance> CreateTestInstanceAsync()
    {
        var instance = Instance.Create(Guid.NewGuid(), "test-workflow", "1.0.0","test-key");
        await InstanceRepository.InsertAsync(instance, true);
        return instance;
    }
}

