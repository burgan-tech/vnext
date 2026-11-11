using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.DataSink;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.DataSink;

/// <summary>
/// Unit tests for DataSinkManager
/// </summary>
public sealed class DataSinkManagerTests
{
    private readonly IDataSinkRegistry _mockRegistry;
    private readonly ILogger<DataSinkManager> _mockLogger;
    private readonly DataSinkManager _manager;

    public DataSinkManagerTests()
    {
        _mockRegistry = Substitute.For<IDataSinkRegistry>();
        _mockLogger = Substitute.For<ILogger<DataSinkManager>>();
        _manager = new DataSinkManager(_mockRegistry, _mockLogger);
    }

    #region Constructor Tests

    [Fact]
    public void Constructor_Should_Throw_When_Registry_Is_Null()
    {
        // Act & Assert
        Should.Throw<ArgumentNullException>(() => new DataSinkManager(null!, _mockLogger));
    }

    [Fact]
    public void Constructor_Should_Throw_When_Logger_Is_Null()
    {
        // Act & Assert
        Should.Throw<ArgumentNullException>(() => new DataSinkManager(_mockRegistry, null!));
    }

    [Fact]
    public void Constructor_Should_Create_Instance_Successfully()
    {
        // Act & Assert
        Should.NotThrow(() => new DataSinkManager(_mockRegistry, _mockLogger));
    }

    #endregion

    #region HandleInsertAsync Tests

    [Fact]
    public async Task HandleInsertAsync_Should_Throw_When_Entity_Is_Null()
    {
        // Act & Assert
        await Should.ThrowAsync<ArgumentNullException>(
            async () => await _manager.HandleInsertAsync<TestEntity>(null!)
        );
    }

    [Fact]
    public async Task HandleInsertAsync_Should_Process_Entity_Through_All_DataSinks()
    {
        // Arrange
        var entity = new TestEntity { };
        var mockDataSink1 = Substitute.For<IDataSink<TestEntity>>();
        var mockDataSink2 = Substitute.For<IDataSink<TestEntity>>();
        
        _mockRegistry.GetDataSinks<TestEntity>()
            .Returns(new[] { mockDataSink1, mockDataSink2 });

        // Act
        await _manager.HandleInsertAsync(entity);

        // Assert
        await mockDataSink1.Received(1).HandleInsertAsync(entity, Arg.Any<CancellationToken>());
        await mockDataSink2.Received(1).HandleInsertAsync(entity, Arg.Any<CancellationToken>());
    }

    [Fact]
    public Task HandleInsertAsync_Should_Handle_No_Registered_DataSinks()
    {
        // Arrange
        var entity = new TestEntity { };
        _mockRegistry.GetDataSinks<TestEntity>()
            .Returns(Enumerable.Empty<IDataSink<TestEntity>>());

        // Act & Assert
        Should.NotThrow(async () => await _manager.HandleInsertAsync(entity));
        return Task.CompletedTask;
    }

    [Fact]
    public async Task HandleInsertAsync_Should_Use_CancellationToken()
    {
        // Arrange
        var entity = new TestEntity { };
        var mockDataSink = Substitute.For<IDataSink<TestEntity>>();
        var cts = new CancellationTokenSource();
        var cancellationToken = cts.Token;
        
        _mockRegistry.GetDataSinks<TestEntity>()
            .Returns(new[] { mockDataSink });

        // Act
        await _manager.HandleInsertAsync(entity, cancellationToken);

        // Assert
        await mockDataSink.Received(1).HandleInsertAsync(entity, cancellationToken);
    }

    [Fact]
    public async Task HandleInsertAsync_Should_Process_All_DataSinks_In_Parallel()
    {
        // Arrange — every sink blocks until released, so a sequential manager would only ever have
        // called the first one. Deterministic: no wall-clock assertion.
        var entity = new TestEntity { };
        var gates = new[] { new TaskCompletionSource(), new TaskCompletionSource(), new TaskCompletionSource() };
        var sinks = gates.Select(gate =>
        {
            var sink = Substitute.For<IDataSink<TestEntity>>();
            sink.HandleInsertAsync(entity, Arg.Any<CancellationToken>()).Returns(gate.Task);
            return sink;
        }).ToArray();
        _mockRegistry.GetDataSinks<TestEntity>().Returns(sinks);

        // Act
        var run = _manager.HandleInsertAsync(entity);

        // Assert — all three started while none has finished
        run.IsCompleted.ShouldBeFalse();
        foreach (var sink in sinks)
        {
            _ = sink.Received(1).HandleInsertAsync(entity, Arg.Any<CancellationToken>());
        }

        foreach (var gate in gates)
        {
            gate.SetResult();
        }

        await run;
    }

    #endregion

    #region HandleUpdateAsync Tests

    [Fact]
    public async Task HandleUpdateAsync_Should_Throw_When_Entity_Is_Null()
    {
        // Act & Assert
        await Should.ThrowAsync<ArgumentNullException>(
            async () => await _manager.HandleUpdateAsync<TestEntity>(null!)
        );
    }

    [Fact]
    public async Task HandleUpdateAsync_Should_Process_Entity_Through_All_DataSinks()
    {
        // Arrange
        var entity = new TestEntity { };
        var mockDataSink1 = Substitute.For<IDataSink<TestEntity>>();
        var mockDataSink2 = Substitute.For<IDataSink<TestEntity>>();
        
        _mockRegistry.GetDataSinks<TestEntity>()
            .Returns(new[] { mockDataSink1, mockDataSink2 });

        // Act
        await _manager.HandleUpdateAsync(entity);

        // Assert
        await mockDataSink1.Received(1).HandleUpdateAsync(entity, Arg.Any<CancellationToken>());
        await mockDataSink2.Received(1).HandleUpdateAsync(entity, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleUpdateAsync_Should_Handle_No_Registered_DataSinks()
    {
        // Arrange
        var entity = new TestEntity { };
        _mockRegistry.GetDataSinks<TestEntity>()
            .Returns(Enumerable.Empty<IDataSink<TestEntity>>());

        // Act & Assert
        await Should.NotThrowAsync(async () => await _manager.HandleUpdateAsync(entity));
    }

    #endregion

    #region HandleDeleteAsync Tests

    [Fact]
    public async Task HandleDeleteAsync_Should_Throw_When_Entity_Is_Null()
    {
        // Act & Assert
        await Should.ThrowAsync<ArgumentNullException>(
            async () => await _manager.HandleDeleteAsync<TestEntity>(null!)
        );
    }

    [Fact]
    public async Task HandleDeleteAsync_Should_Process_Entity_Through_All_DataSinks()
    {
        // Arrange
        var entity = new TestEntity { };
        var mockDataSink1 = Substitute.For<IDataSink<TestEntity>>();
        var mockDataSink2 = Substitute.For<IDataSink<TestEntity>>();
        
        _mockRegistry.GetDataSinks<TestEntity>()
            .Returns(new[] { mockDataSink1, mockDataSink2 });

        // Act
        await _manager.HandleDeleteAsync(entity);

        // Assert
        await mockDataSink1.Received(1).HandleDeleteAsync(entity, Arg.Any<CancellationToken>());
        await mockDataSink2.Received(1).HandleDeleteAsync(entity, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleDeleteAsync_Should_Handle_No_Registered_DataSinks()
    {
        // Arrange
        var entity = new TestEntity { };
        _mockRegistry.GetDataSinks<TestEntity>()
            .Returns(Enumerable.Empty<IDataSink<TestEntity>>());

        // Act & Assert
        await Should.NotThrowAsync(async () => await _manager.HandleDeleteAsync(entity));
    }

    #endregion

    #region FlushAllAsync Tests

    [Fact]
    public async Task FlushAllAsync_Should_Flush_All_Registered_DataSinks()
    {
        // Arrange
        var mockDataSink1 = Substitute.For<IDataSink>();
        var mockDataSink2 = Substitute.For<IDataSink>();
        var mockDataSink3 = Substitute.For<IDataSink>();
        
        _mockRegistry.GetAllDataSinks()
            .Returns(new[] { mockDataSink1, mockDataSink2, mockDataSink3 });

        // Act
        await _manager.FlushAllAsync();

        // Assert
        await mockDataSink1.Received(1).FlushAsync(Arg.Any<CancellationToken>());
        await mockDataSink2.Received(1).FlushAsync(Arg.Any<CancellationToken>());
        await mockDataSink3.Received(1).FlushAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FlushAllAsync_Should_Handle_No_Registered_DataSinks()
    {
        // Arrange
        _mockRegistry.GetAllDataSinks()
            .Returns(Enumerable.Empty<IDataSink>());

        // Act & Assert
        await Should.NotThrowAsync(async () => await _manager.FlushAllAsync());
    }

    [Fact]
    public async Task FlushAllAsync_Should_Use_CancellationToken()
    {
        // Arrange
        var mockDataSink = Substitute.For<IDataSink>();
        var cts = new CancellationTokenSource();
        var cancellationToken = cts.Token;
        
        _mockRegistry.GetAllDataSinks()
            .Returns(new[] { mockDataSink });

        // Act
        await _manager.FlushAllAsync(cancellationToken);

        // Assert
        await mockDataSink.Received(1).FlushAsync(cancellationToken);
    }

    #endregion

    #region FlushAsync (Entity Type Specific) Tests

    [Fact]
    public async Task FlushAsync_Generic_Should_Flush_DataSinks_For_Specific_Entity_Type()
    {
        // Arrange
        var mockDataSink1 = Substitute.For<IDataSink<TestEntity>>();
        var mockDataSink2 = Substitute.For<IDataSink<TestEntity>>();
        
        _mockRegistry.GetDataSinks<TestEntity>()
            .Returns(new[] { mockDataSink1, mockDataSink2 });

        // Act
        await _manager.FlushAsync<TestEntity>();

        // Assert
        await mockDataSink1.Received(1).FlushAsync(Arg.Any<CancellationToken>());
        await mockDataSink2.Received(1).FlushAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FlushAsync_Generic_Should_Handle_No_Registered_DataSinks()
    {
        // Arrange
        _mockRegistry.GetDataSinks<TestEntity>()
            .Returns(Enumerable.Empty<IDataSink<TestEntity>>());

        // Act & Assert
        await Should.NotThrowAsync(async () => await _manager.FlushAsync<TestEntity>());
    }

    [Fact]
    public async Task FlushAsync_Generic_Should_Use_CancellationToken()
    {
        // Arrange
        var mockDataSink = Substitute.For<IDataSink<TestEntity>>();
        var cts = new CancellationTokenSource();
        var cancellationToken = cts.Token;
        
        _mockRegistry.GetDataSinks<TestEntity>()
            .Returns(new[] { mockDataSink });

        // Act
        await _manager.FlushAsync<TestEntity>(cancellationToken);

        // Assert
        await mockDataSink.Received(1).FlushAsync(cancellationToken);
    }

    #endregion

    #region Error Handling Tests

    [Fact]
    public async Task HandleInsertAsync_Should_Propagate_Exception_From_DataSink()
    {
        // Arrange
        var entity = new TestEntity { };
        var mockDataSink = Substitute.For<IDataSink<TestEntity>>();
        var expectedException = new InvalidOperationException("Data sink error");
        
        mockDataSink.HandleInsertAsync(entity, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(expectedException));
        
        _mockRegistry.GetDataSinks<TestEntity>()
            .Returns(new[] { mockDataSink });

        // Act & Assert
        var exception = await Should.ThrowAsync<InvalidOperationException>(
            async () => await _manager.HandleInsertAsync(entity)
        );
        
        exception.Message.ShouldBe("Data sink error");
    }

    #endregion

    #region Test Helper Classes

    public class TestEntity
    {
    }

    #endregion
}

