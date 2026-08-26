using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
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
        _instanceTask = new InstanceTask(Guid.NewGuid(), Guid.NewGuid(), "test-task");
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
    public async Task StandardTaskPersistenceStrategy_HandleCreationAsync_ShouldCallRepositoryInsert()
    {
        // Arrange
        var strategy = CreateStandardStrategy();

        // Act
        var persisted = await strategy.HandleCreationAsync(_instanceTask, cancellationToken: CancellationToken.None);

        // Assert
        await _mockRepository.Received(1).InsertAsync(_instanceTask, true, CancellationToken.None);
        persisted.ShouldBeSameAs(_instanceTask);
        _unitOfWorkManager.Received(1).Begin(Arg.Is<UnitOfWorkOptions>(options =>
            options.Scope == UnitOfWorkScopeOption.RequiresNew && options.IsTransactional));
        await _unitOfWork.Received(1).CommitAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StandardTaskPersistenceStrategy_HandleCreationAsync_WithSkipLookup_InsertsWithoutProbing()
    {
        // A freshly inserted transition record cannot have journal rows, so the caller may skip
        // the idempotency probe entirely — the guaranteed-empty SELECT per task is the cost this
        // flag exists to remove.
        var strategy = CreateStandardStrategy();

        var persisted = await strategy.HandleCreationAsync(
            _instanceTask, skipLookup: true, cancellationToken: CancellationToken.None);

        persisted.ShouldBeSameAs(_instanceTask);
        await _mockRepository.DidNotReceiveWithAnyArgs().FindByTransitionAndTaskAsync(default, default!, default);
        await _mockRepository.Received(1).InsertAsync(_instanceTask, true, CancellationToken.None);
        await _unitOfWork.Received(1).CommitAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StandardTaskPersistenceStrategy_HandleCreationAsync_WhenJournalExists_ReusesIt()
    {
        var existing = new InstanceTask(Guid.NewGuid(), _instanceTask.TransitionId, _instanceTask.TaskId);
        _mockRepository.FindByTransitionAndTaskAsync(
                _instanceTask.TransitionId,
                _instanceTask.TaskId,
                CancellationToken.None)
            .Returns(existing);
        var strategy = CreateStandardStrategy();

        var persisted = await strategy.HandleCreationAsync(_instanceTask, cancellationToken: CancellationToken.None);

        persisted.ShouldBeSameAs(existing);
        await _mockRepository.DidNotReceiveWithAnyArgs().InsertAsync(default!, default, default);
        await _unitOfWork.Received(1).CommitAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StandardTaskPersistenceStrategy_HandleCompletionAsync_ShouldCallRepositoryUpdate()
    {
        // Arrange
        var strategy = CreateStandardStrategy();

        // Act
        await strategy.HandleCompletionAsync(_instanceTask, CancellationToken.None);

        // Assert — one set-based completion write, never a full-row attach-and-update
        await _mockRepository.Received(1).MarkCompletedAsync(_instanceTask, CancellationToken.None);
        await _mockRepository.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default, default);
        _unitOfWorkManager.Received(1).Begin(Arg.Is<UnitOfWorkOptions>(options =>
            options.Scope == UnitOfWorkScopeOption.RequiresNew && options.IsTransactional));
        await _unitOfWork.Received(1).CommitAsync(CancellationToken.None);
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
        await strategy.HandleCreationAsync(_instanceTask, cancellationToken: CancellationToken.None);

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
