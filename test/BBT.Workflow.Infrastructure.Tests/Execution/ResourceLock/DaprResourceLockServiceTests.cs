#pragma warning disable DAPR_DISTRIBUTEDLOCK

using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Infrastructure.Execution.ResourceLock;
using Dapr.Client;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Execution.ResourceLock;

/// <summary>
/// Unit tests for <see cref="DaprResourceLockService"/>.
/// Validates acquire/release/extend behavior and exception propagation
/// after removing catch-throw anti-pattern.
/// </summary>
public class DaprResourceLockServiceTests
{
    private const string LockStoreName = "lockstore";
    private const string ResourceKey = "seat:concert1:A1";
    private const string Owner = "instance-123";
    private const int TtlSeconds = 300;

    private readonly Mock<DaprClient> _mockDaprClient;
    private readonly DaprResourceLockService _service;

    public DaprResourceLockServiceTests()
    {
        _mockDaprClient = new Mock<DaprClient>();
        var logger = new Mock<ILogger<DaprResourceLockService>>();
        _service = new DaprResourceLockService(_mockDaprClient.Object, LockStoreName, logger.Object);
    }

    #region AcquireAsync

    [Fact]
    public async Task AcquireAsync_WhenLockSucceeds_ShouldReturnTrue()
    {
        var response = new TryLockResponse { Success = true };
        _mockDaprClient
            .Setup(c => c.Lock(LockStoreName, ResourceKey, Owner, TtlSeconds, It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var result = await _service.AcquireAsync(ResourceKey, Owner, TtlSeconds, CancellationToken.None);

        result.ShouldBeTrue();
    }

    [Fact]
    public async Task AcquireAsync_WhenLockFails_ShouldReturnFalse()
    {
        var response = new TryLockResponse { Success = false };
        _mockDaprClient
            .Setup(c => c.Lock(LockStoreName, ResourceKey, Owner, TtlSeconds, It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var result = await _service.AcquireAsync(ResourceKey, Owner, TtlSeconds, CancellationToken.None);

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task AcquireAsync_WhenDaprThrows_ShouldPropagateException()
    {
        _mockDaprClient
            .Setup(c => c.Lock(LockStoreName, ResourceKey, Owner, TtlSeconds, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Dapr unavailable"));

        await Should.ThrowAsync<InvalidOperationException>(
            () => _service.AcquireAsync(ResourceKey, Owner, TtlSeconds, CancellationToken.None));
    }

    #endregion

    #region ReleaseAsync

    [Fact]
    public async Task ReleaseAsync_WhenUnlockSucceeds_ShouldReturnTrue()
    {
        _mockDaprClient
            .Setup(c => c.Unlock(LockStoreName, ResourceKey, Owner, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UnlockResponse(LockStatus.Success));

        var result = await _service.ReleaseAsync(ResourceKey, Owner, CancellationToken.None);

        result.ShouldBeTrue();
    }

    [Fact]
    public async Task ReleaseAsync_WhenUnlockFails_ShouldReturnFalse()
    {
        _mockDaprClient
            .Setup(c => c.Unlock(LockStoreName, ResourceKey, Owner, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UnlockResponse(LockStatus.LockDoesNotExist));

        var result = await _service.ReleaseAsync(ResourceKey, Owner, CancellationToken.None);

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task ReleaseAsync_WhenDaprThrows_ShouldPropagateException()
    {
        _mockDaprClient
            .Setup(c => c.Unlock(LockStoreName, ResourceKey, Owner, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Dapr unavailable"));

        await Should.ThrowAsync<InvalidOperationException>(
            () => _service.ReleaseAsync(ResourceKey, Owner, CancellationToken.None));
    }

    #endregion

    #region ExtendAsync

    [Fact]
    public async Task ExtendAsync_WhenReAcquireSucceeds_ShouldReturnTrue()
    {
        var response = new TryLockResponse { Success = true };
        _mockDaprClient
            .Setup(c => c.Lock(LockStoreName, ResourceKey, Owner, TtlSeconds, It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var result = await _service.ExtendAsync(ResourceKey, Owner, TtlSeconds, CancellationToken.None);

        result.ShouldBeTrue();
    }

    [Fact]
    public async Task ExtendAsync_WhenReAcquireFails_ShouldReturnFalse()
    {
        var response = new TryLockResponse { Success = false };
        _mockDaprClient
            .Setup(c => c.Lock(LockStoreName, ResourceKey, Owner, TtlSeconds, It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var result = await _service.ExtendAsync(ResourceKey, Owner, TtlSeconds, CancellationToken.None);

        result.ShouldBeFalse();
    }

    [Fact]
    public async Task ExtendAsync_WhenDaprThrows_ShouldPropagateException()
    {
        _mockDaprClient
            .Setup(c => c.Lock(LockStoreName, ResourceKey, Owner, TtlSeconds, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Dapr unavailable"));

        await Should.ThrowAsync<InvalidOperationException>(
            () => _service.ExtendAsync(ResourceKey, Owner, TtlSeconds, CancellationToken.None));
    }

    #endregion
}
