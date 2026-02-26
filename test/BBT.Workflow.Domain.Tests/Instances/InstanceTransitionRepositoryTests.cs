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
        var result = await Repository.InsertAsync(transition);

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
        await Repository.InsertAsync(transition);

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
        await Repository.InsertAsync(transition);

        // Act
        transition.Completed("CompletedState");
        await Repository.UpdateCompletedAsync(transition, CancellationToken.None);

        // Assert
        var updated = await Repository.GetAsync(transition.Id);
        updated.ShouldNotBeNull();
        updated.ToState.ShouldBe("CompletedState");
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
        await Repository.InsertAsync(transition);

        // Act
        await Repository.DeleteAsync(transition);

        // Assert
        var deleted = await Repository.FindAsync(transition.Id);
        deleted.ShouldBeNull();
    }

    /// <summary>
    /// Helper method to create a test instance for transition tests.
    /// </summary>
    private async Task<Instance> CreateTestInstanceAsync()
    {
        var instance = Instance.Create(Guid.NewGuid(), "test-workflow", "test-key");
        await InstanceRepository.InsertAsync(instance);
        return instance;
    }
}

