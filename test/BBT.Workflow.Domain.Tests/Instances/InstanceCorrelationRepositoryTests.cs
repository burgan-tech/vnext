using System;
using System.Threading.Tasks;
using BBT.Aether.Testing;
using BBT.Workflow.Definitions;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// Base test class for testing IInstanceCorrelationRepository implementations.
/// Contains test methods that verify the repository contract and behavior.
/// </summary>
public abstract class InstanceCorrelationRepositoryTests<TEntry> : DomainTestBase<TEntry>
    where TEntry : ModuleEntryPointBase, new()
{
    protected IInstanceCorrelationRepository Repository => GetRequiredService<IInstanceCorrelationRepository>();
    protected IInstanceRepository InstanceRepository => GetRequiredService<IInstanceRepository>();

    /// <summary>
    /// Tests that InsertAsync successfully creates a new instance correlation in the database.
    /// </summary>
    [Fact]
    public async Task InsertAsync_ShouldCreateCorrelation()
    {
        // Arrange
        var parentInstance = await CreateTestInstanceAsync("parent");
        var subInstance = await CreateTestInstanceAsync("sub");
        
        var correlation = InstanceCorrelation.Create(
            Guid.NewGuid(),
            parentInstance.Id,
            "TestState",
            subInstance.Id,
            SubFlowType.SubFlow.Code,
            "test",
            "test-workflow",
            "1.0"
        );

        // Act
        var result = await Repository.InsertAsync(correlation);

        // Assert
        result.ShouldNotBeNull();
        result.Id.ShouldBe(correlation.Id);
        result.ParentInstanceId.ShouldBe(parentInstance.Id);
        result.SubFlowInstanceId.ShouldBe(subInstance.Id);
        result.IsCompleted.ShouldBeFalse();
    }

    /// <summary>
    /// Tests that GetActiveByParentAsync returns all active correlations for a parent instance.
    /// </summary>
    [Fact]
    public async Task GetActiveByParentAsync_ShouldReturnActiveCorrelations()
    {
        // Arrange
        var parentInstance = await CreateTestInstanceAsync("parent");
        var subInstance1 = await CreateTestInstanceAsync("sub1");
        var subInstance2 = await CreateTestInstanceAsync("sub2");
        
        var correlation1 = InstanceCorrelation.Create(
            Guid.NewGuid(),
            parentInstance.Id,
            "TestState",
            subInstance1.Id,
            SubFlowType.SubFlow.Code,
            "test",
            "test-workflow1",
            "1.0"
        );
        
        var correlation2 = InstanceCorrelation.Create(
            Guid.NewGuid(),
            parentInstance.Id,
            "TestState",
            subInstance2.Id,
            SubFlowType.SubProcess.Code,
            "test",
            "test-workflow2",
            "1.0"
        );

        await Repository.InsertAsync(correlation1);
        await Repository.InsertAsync(correlation2);

        // Act
        var results = await Repository.GetActiveByParentAsync(parentInstance.Id);

        // Assert
        results.ShouldNotBeNull();
        results.Count.ShouldBe(2);
        results.ShouldAllBe(c => c.ParentInstanceId == parentInstance.Id);
        results.ShouldAllBe(c => !c.IsCompleted);
    }

    /// <summary>
    /// Tests that AnyActiveSubFlowByParentAsync returns true when active SubFlow exists.
    /// </summary>
    [Fact]
    public async Task AnyActiveSubFlowByParentAsync_ShouldReturnTrueWhenExists()
    {
        // Arrange
        var parentInstance = await CreateTestInstanceAsync("parent");
        var subInstance = await CreateTestInstanceAsync("sub");
        
        var correlation = InstanceCorrelation.Create(
            Guid.NewGuid(),
            parentInstance.Id,
            "TestState",
            subInstance.Id,
            SubFlowType.SubFlow.Code,
            "test",
            "test-workflow",
            "1.0"
        );

        await Repository.InsertAsync(correlation);

        // Act
        var result = await Repository.AnyActiveSubFlowByParentAsync(parentInstance.Id);

        // Assert
        result.ShouldBeTrue();
    }

    /// <summary>
    /// Tests that AnyActiveSubFlowByParentAsync returns false when no active SubFlow exists.
    /// </summary>
    [Fact]
    public async Task AnyActiveSubFlowByParentAsync_ShouldReturnFalseWhenNotExists()
    {
        // Arrange
        var parentInstance = await CreateTestInstanceAsync("parent");

        // Act
        var result = await Repository.AnyActiveSubFlowByParentAsync(parentInstance.Id);

        // Assert
        result.ShouldBeFalse();
    }

    /// <summary>
    /// Tests that FindActiveSubFlowByParentAsync returns the active SubFlow correlation.
    /// </summary>
    [Fact]
    public async Task FindActiveSubFlowByParentAsync_ShouldReturnActiveSubFlow()
    {
        // Arrange
        var parentInstance = await CreateTestInstanceAsync("parent");
        var subInstance = await CreateTestInstanceAsync("sub");
        
        var correlation = InstanceCorrelation.Create(
            Guid.NewGuid(),
            parentInstance.Id,
            "TestState",
            subInstance.Id,
            SubFlowType.SubFlow.Code,
            "test",
            "test-workflow",
            "1.0"
        );

        await Repository.InsertAsync(correlation);

        // Act
        var result = await Repository.FindActiveSubFlowByParentAsync(parentInstance.Id);

        // Assert
        result.ShouldNotBeNull();
        result.ParentInstanceId.ShouldBe(parentInstance.Id);
        result.SubFlowType.ShouldBe(SubFlowType.SubFlow);
        result.IsCompleted.ShouldBeFalse();
    }

    /// <summary>
    /// Tests that FindBySubInstanceIdAsync returns the correlation for a SubFlow instance.
    /// </summary>
    [Fact]
    public async Task FindBySubInstanceIdAsync_ShouldReturnCorrelation()
    {
        // Arrange
        var parentInstance = await CreateTestInstanceAsync("parent");
        var subInstance = await CreateTestInstanceAsync("sub");
        
        var correlation = InstanceCorrelation.Create(
            Guid.NewGuid(),
            parentInstance.Id,
            "TestState",
            subInstance.Id,
            SubFlowType.SubFlow.Code,
            "test",
            "test-workflow",
            "1.0"
        );

        await Repository.InsertAsync(correlation);

        // Act
        var result = await Repository.FindBySubInstanceIdAsync(subInstance.Id);

        // Assert
        result.ShouldNotBeNull();
        result.SubFlowInstanceId.ShouldBe(subInstance.Id);
        result.ParentInstanceId.ShouldBe(parentInstance.Id);
    }

    /// <summary>
    /// Tests that UpdateAsync successfully marks a correlation as completed.
    /// </summary>
    [Fact]
    public async Task UpdateAsync_ShouldMarkAsCompleted()
    {
        // Arrange
        var parentInstance = await CreateTestInstanceAsync("parent");
        var subInstance = await CreateTestInstanceAsync("sub");
        
        var correlation = InstanceCorrelation.Create(
            Guid.NewGuid(),
            parentInstance.Id,
            "TestState",
            subInstance.Id,
            SubFlowType.SubFlow.Code,
            "test",
            "test-workflow",
            "1.0"
        );

        await Repository.InsertAsync(correlation);

        // Act
        correlation.Completed();
        var result = await Repository.UpdateAsync(correlation);

        // Assert
        result.ShouldNotBeNull();
        result.IsCompleted.ShouldBeTrue();
    }

    /// <summary>
    /// Helper method to create a test instance for correlation tests.
    /// </summary>
    private async Task<Instance> CreateTestInstanceAsync(string key)
    {
        var instance = Instance.Create(Guid.NewGuid(), "test-workflow", "1.0.0",key);
        await InstanceRepository.InsertAsync(instance);
        return instance;
    }
}

