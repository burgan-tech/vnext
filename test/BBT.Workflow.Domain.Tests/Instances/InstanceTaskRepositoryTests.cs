using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Testing;
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;
using TaskStatus = BBT.Workflow.Definitions.TaskStatus;

namespace BBT.Workflow.Instances;

/// <summary>
/// Base test class for testing IInstanceTaskRepository implementations.
/// Runs against a real relational provider so set-based statements are actually translated —
/// mock-level tests cannot catch an untranslatable SetProperty lambda.
/// </summary>
public abstract class InstanceTaskRepositoryTests<TEntry> : DomainTestBase<TEntry>
    where TEntry : ModuleEntryPointBase, new()
{
    protected IInstanceTaskRepository Repository => GetRequiredService<IInstanceTaskRepository>();
    protected IInstanceTransitionRepository TransitionRepository => GetRequiredService<IInstanceTransitionRepository>();
    protected IInstanceRepository InstanceRepository => GetRequiredService<IInstanceRepository>();

    /// <summary>
    /// Pins that MarkCompletedAsync translates and writes every column the completion path
    /// mutates. The JsonData members are OwnsOne-mapped, so the statement must target the owned
    /// scalar (t.X.Json) — targeting the navigation itself fails translation at runtime.
    /// </summary>
    [Fact]
    public async Task MarkCompletedAsync_ShouldPersistAllCompletionColumns()
    {
        // Arrange
        var transition = await CreateTestTransitionAsync();
        var task = new InstanceTask(Guid.NewGuid(), transition.Id, "test-task", TaskTrigger.OnExecute, 1);
        await Repository.InsertAsync(task, true);

        // Act — mirror the completion path: request/invocation captured, then completed.
        task.SetRequest(JsonData.CreateFrom("""{"input":1}"""));
        task.SetInvocationResult(JsonData.CreateFrom("""{"statusCode":200}"""));
        task.Completed(JsonData.CreateFrom("""{"output":2}"""), isBusinessSuccess: true);
        await Repository.MarkCompletedAsync(task, CancellationToken.None);

        // Assert — fresh no-tracking read so the values come from the database row.
        var updated = await Repository.GetByIdAsReadOnlyAsync(task.Id);
        updated.ShouldNotBeNull();
        updated.Status.ShouldBe(TaskStatus.Completed);
        updated.BusinessStatus.ShouldBe(BusinessStatus.Success);
        updated.Request.Json.ShouldBe(task.Request.Json);
        updated.Response.Json.ShouldBe(task.Response.Json);
        updated.InvocationResult.Json.ShouldBe(task.InvocationResult.Json);
        updated.FinishedAt.ShouldNotBeNull();
        updated.Duration.ShouldNotBeNull();
    }

    /// <summary>
    /// The faulted path shares MarkCompletedAsync — pins that a Faulted journal row persists its
    /// status pair the same way.
    /// </summary>
    [Fact]
    public async Task MarkCompletedAsync_ShouldPersistFaultedStatus()
    {
        // Arrange
        var transition = await CreateTestTransitionAsync();
        var task = new InstanceTask(Guid.NewGuid(), transition.Id, "test-task", TaskTrigger.OnExecute, 1);
        await Repository.InsertAsync(task, true);

        // Act
        task.Faulted("boom");
        await Repository.MarkCompletedAsync(task, CancellationToken.None);

        // Assert
        var updated = await Repository.GetByIdAsReadOnlyAsync(task.Id);
        updated.ShouldNotBeNull();
        updated.Status.ShouldBe(TaskStatus.Faulted);
        updated.Response.Json.ShouldBe(task.Response.Json);
        updated.FinishedAt.ShouldNotBeNull();
    }

    /// <summary>
    /// Helper method to create a persisted instance + transition pair for task journal tests.
    /// </summary>
    private async Task<InstanceTransition> CreateTestTransitionAsync()
    {
        var instance = Instance.Create(Guid.NewGuid(), "test-workflow", "1.0.0", "test-key");
        await InstanceRepository.InsertAsync(instance);
        var transition = InstanceTransition.Create(
            Guid.NewGuid(),
            instance.Id,
            "test-transition",
            "InitialState",
            TriggerType.Manual,
            JsonData.CreateFrom("{}"),
            JsonData.CreateFrom("{}")
        );
        await TransitionRepository.InsertAsync(transition);
        return transition;
    }
}
