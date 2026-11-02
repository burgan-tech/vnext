using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Shared;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Tasks.Persistence.Strategies;

/// <summary>
/// Unit tests for task persistence strategies to ensure SOLID principles compliance
/// and correct behavior based on TaskTrigger types.
/// </summary>
public class TaskPersistenceStrategyTests
{
    private readonly IInstanceTaskRepository _mockRepository;
    private readonly InstanceTask _instanceTask;
    private readonly IUnitOfWorkManager _unitOfWorkManager;
    private readonly IUnitOfWork _unitOfWork;

    public TaskPersistenceStrategyTests()
    {
        _mockRepository = Substitute.For<IInstanceTaskRepository>();
        _instanceTask = new InstanceTask(Guid.NewGuid(), Guid.NewGuid(), "test-task", TaskTrigger.OnExecute, 1);
        _mockRepository.InsertAsync(
                Arg.Any<InstanceTask>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<InstanceTask>());
        _unitOfWorkManager = Substitute.For<IUnitOfWorkManager>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _unitOfWorkManager.Begin(Arg.Any<UnitOfWorkOptions>()).Returns(_unitOfWork);
    }

    private StandardTaskPersistenceStrategy CreateStandardStrategy() =>
        new(_mockRepository, _unitOfWorkManager);

    [Fact]
    public void StandardTaskPersistenceStrategy_CanHandle_ShouldReturnTrueOnlyForFlowOrigin()
    {
        // Arrange
        var strategy = CreateStandardStrategy();
        strategy.CanHandle(TaskExecutionOrigin.Flow).ShouldBeTrue();
        strategy.CanHandle(TaskExecutionOrigin.Function).ShouldBeFalse();
        strategy.CanHandle(TaskExecutionOrigin.Extension).ShouldBeFalse();
    }

    [Fact]
    public void NonPersistentTaskPersistenceStrategy_CanHandle_ShouldReturnTrueForFunctionAndExtension()
    {
        // Arrange
        var strategy = new ExtensionTaskPersistenceStrategy();

        strategy.CanHandle(TaskExecutionOrigin.Flow).ShouldBeFalse();
        strategy.CanHandle(TaskExecutionOrigin.Function).ShouldBeTrue();
        strategy.CanHandle(TaskExecutionOrigin.Extension).ShouldBeTrue();
    }

    [Fact]
    public async Task StandardTaskPersistenceStrategy_HandleCreationAsync_ProbesButDoesNotInsert()
    {
        // One write per task: creation only resolves which entity the completion will write. A
        // first execution leaves the caller's entity unwritten; the "busy" row is gone on purpose
        // (it cost every task a second autocommit transaction, i.e. a second WAL fsync).
        var strategy = CreateStandardStrategy();

        var persisted = await strategy.HandleCreationAsync(
            _instanceTask, TaskTrigger.OnExecute, 1, cancellationToken: CancellationToken.None);

        persisted.ShouldBeSameAs(_instanceTask);
        await _mockRepository.Received(1).FindByTransitionAndTaskAsync(
            _instanceTask.TransitionId, _instanceTask.TaskId, TaskTrigger.OnExecute, 1, CancellationToken.None);
        await _mockRepository.DidNotReceiveWithAnyArgs().InsertAsync(default!, default, default);
        // No database transaction: the per-instance Busy/CAS mutex already serializes task
        // execution, and InstanceTask raises no domain events for a transaction to coordinate.
        _unitOfWorkManager.Received(1).Begin(Arg.Is<UnitOfWorkOptions>(options =>
            options.Scope == UnitOfWorkScopeOption.RequiresNew && !options.IsTransactional));
        await _unitOfWork.Received(1).CommitAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StandardTaskPersistenceStrategy_HandleCreationAsync_WithSkipLookup_TouchesNothing()
    {
        // A freshly inserted transition record cannot have journal rows, so the caller may skip
        // the idempotency probe entirely — with no insert at creation either, the step is free.
        var strategy = CreateStandardStrategy();

        var persisted = await strategy.HandleCreationAsync(
            _instanceTask, TaskTrigger.OnExecute, 1, skipLookup: true, cancellationToken: CancellationToken.None);

        persisted.ShouldBeSameAs(_instanceTask);
        await _mockRepository.DidNotReceiveWithAnyArgs().FindByTransitionAndTaskAsync(default, default!, default, default, default);
        await _mockRepository.DidNotReceiveWithAnyArgs().InsertAsync(default!, default, default);
        _unitOfWorkManager.DidNotReceiveWithAnyArgs().Begin(default!);
    }

    [Fact]
    public async Task StandardTaskPersistenceStrategy_HandleCreationAsync_WhenJournalExists_ReusesIt()
    {
        var existing = new InstanceTask(
            Guid.NewGuid(), _instanceTask.TransitionId, _instanceTask.TaskId, TaskTrigger.OnExecute, 1);
        _mockRepository.FindByTransitionAndTaskAsync(
                _instanceTask.TransitionId,
                _instanceTask.TaskId,
                TaskTrigger.OnExecute,
                1,
                CancellationToken.None)
            .Returns(existing);
        var strategy = CreateStandardStrategy();

        var persisted = await strategy.HandleCreationAsync(
            _instanceTask, TaskTrigger.OnExecute, 1, cancellationToken: CancellationToken.None);

        persisted.ShouldBeSameAs(existing);
        await _mockRepository.DidNotReceiveWithAnyArgs().InsertAsync(default!, default, default);
        await _unitOfWork.Received(1).CommitAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StandardTaskPersistenceStrategy_HandleCompletionAsync_FirstExecution_InsertsOnce()
    {
        // The journal's only hot write: the row lands with its final state in ONE insert.
        var strategy = CreateStandardStrategy();
        await strategy.HandleCreationAsync(
            _instanceTask, TaskTrigger.OnExecute, 1, skipLookup: true, cancellationToken: CancellationToken.None);
        _instanceTask.Completed(new JsonData("{}"), isBusinessSuccess: true);

        await strategy.HandleCompletionAsync(_instanceTask, CancellationToken.None);

        await _mockRepository.Received(1).InsertAsync(_instanceTask, true, CancellationToken.None);
        await _mockRepository.DidNotReceiveWithAnyArgs().MarkCompletedAsync(default!, default);
        await _mockRepository.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default, default);
        _unitOfWorkManager.Received(1).Begin(Arg.Is<UnitOfWorkOptions>(options =>
            options.Scope == UnitOfWorkScopeOption.RequiresNew && !options.IsTransactional));
        await _unitOfWork.Received(1).CommitAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StandardTaskPersistenceStrategy_HandleCompletionAsync_ReusedRow_UpdatesSetBased()
    {
        // A retry found the previous attempt's row: completion updates it set-based, never a
        // full-row attach-and-update, and never a second insert.
        var existing = new InstanceTask(
            Guid.NewGuid(), _instanceTask.TransitionId, _instanceTask.TaskId, TaskTrigger.OnExecute, 1);
        _mockRepository.FindByTransitionAndTaskAsync(
                _instanceTask.TransitionId, _instanceTask.TaskId, TaskTrigger.OnExecute, 1, CancellationToken.None)
            .Returns(existing);
        var strategy = CreateStandardStrategy();
        var reused = await strategy.HandleCreationAsync(
            _instanceTask, TaskTrigger.OnExecute, 1, cancellationToken: CancellationToken.None);
        reused.Completed(new JsonData("{}"), isBusinessSuccess: true);

        await strategy.HandleCompletionAsync(reused, CancellationToken.None);

        await _mockRepository.Received(1).MarkCompletedAsync(reused, CancellationToken.None);
        await _mockRepository.DidNotReceiveWithAnyArgs().InsertAsync(default!, default, default);
        await _mockRepository.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default, default);
    }

    [Fact]
    public async Task StandardTaskPersistenceStrategy_HandleCompletionAsync_RunsThroughTheBookkeepingWriter()
    {
        // The commit policy (synchronous or asynchronous commit) is the writer's; the strategy
        // only hands it the write.
        var writer = Substitute.For<BBT.Workflow.Execution.Persistence.IBookkeepingWriter>();
        writer.RunAsync(Arg.Any<Func<CancellationToken, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<CancellationToken, Task>>()(call.Arg<CancellationToken>()));
        var strategy = new StandardTaskPersistenceStrategy(_mockRepository, _unitOfWorkManager, writer);
        _instanceTask.Completed(new JsonData("{}"), isBusinessSuccess: true);

        await strategy.HandleCompletionAsync(_instanceTask, CancellationToken.None);

        await writer.Received(1).RunAsync(Arg.Any<Func<CancellationToken, Task>>(), CancellationToken.None);
        await _mockRepository.Received(1).InsertAsync(_instanceTask, true, CancellationToken.None);
    }

    [Fact]
    public void ExtensionTaskPersistenceStrategy_CanHandle_ShouldReturnTrueForNonPersistentOrigins()
    {
        // Arrange
        var strategy = new ExtensionTaskPersistenceStrategy();

        // Act & Assert
        strategy.CanHandle(TaskExecutionOrigin.Function).ShouldBeTrue();
        strategy.CanHandle(TaskExecutionOrigin.Extension).ShouldBeTrue();
        strategy.CanHandle(TaskExecutionOrigin.Flow).ShouldBeFalse();
    }

    [Fact]
    public async Task ExtensionTaskPersistenceStrategy_HandleCreationAsync_ShouldNotPersist()
    {
        // Arrange
        var strategy = new ExtensionTaskPersistenceStrategy();

        // Act
        await strategy.HandleCreationAsync(
            _instanceTask, TaskTrigger.OnExecute, 1, cancellationToken: CancellationToken.None);

        // Assert
        // No exception should be thrown and method should complete successfully
        // No persistence operations should occur
    }

    [Fact]
    public async Task ExtensionTaskPersistenceStrategy_HandleCompletionAsync_ShouldNotPersist()
    {
        // Arrange
        var strategy = new ExtensionTaskPersistenceStrategy();

        // Act
        await strategy.HandleCompletionAsync(_instanceTask, CancellationToken.None);

        // Assert
        // No exception should be thrown and method should complete successfully
        // No persistence operations should occur
    }

    [Theory]
    [InlineData(TaskExecutionOrigin.Flow, typeof(StandardTaskPersistenceStrategy))]
    [InlineData(TaskExecutionOrigin.Function, typeof(ExtensionTaskPersistenceStrategy))]
    [InlineData(TaskExecutionOrigin.Extension, typeof(ExtensionTaskPersistenceStrategy))]
    public void TaskPersistenceStrategyFactory_GetStrategy_ShouldReturnCorrectStrategy(
        TaskExecutionOrigin origin, Type expectedStrategyType)
    {
        // Arrange
        var strategies = new List<ITaskPersistenceStrategy>
        {
            CreateStandardStrategy(),
            new ExtensionTaskPersistenceStrategy()
        };
        var factory = new TaskPersistenceStrategyFactory(strategies);

        // Act
        var result = factory.GetStrategy(origin);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeOfType(expectedStrategyType);
        result.Value!.CanHandle(origin).ShouldBeTrue();
    }

    [Fact]
    public void TaskPersistenceStrategyFactory_GetStrategy_ShouldReturnFailWhenNoStrategyFound()
    {
        // Arrange
        var strategies = new List<ITaskPersistenceStrategy>(); // Empty list
        var factory = new TaskPersistenceStrategyFactory(strategies);

        // Act
        var result = factory.GetStrategy(TaskExecutionOrigin.Flow);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.Error.Prefix.ShouldBe(ErrorCodes.Prefixes.NotFound);
        result.Error.Message!.ShouldContain("No task persistence strategy found for origin: Flow");
    }

    [Fact]
    public void TaskPersistenceStrategies_ShouldFollowSingleResponsibilityPrinciple()
    {
        // Arrange
        var standardStrategy = CreateStandardStrategy();
        var extensionStrategy = new ExtensionTaskPersistenceStrategy();

        // Assert
        // Each strategy should only handle specific TaskTrigger types
        standardStrategy.CanHandle(TaskExecutionOrigin.Extension).ShouldBeFalse();
        extensionStrategy.CanHandle(TaskExecutionOrigin.Flow).ShouldBeFalse();
        
        // Strategies should have distinct responsibilities
        standardStrategy.GetType().ShouldNotBe(extensionStrategy.GetType());
    }
}
