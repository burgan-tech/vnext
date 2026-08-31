using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.MultiSchema;
using BBT.Workflow.Caching;
using BBT.Workflow.Instances;
using BBT.Workflow.Monitor.Instances;
using BBT.Workflow.Monitor.Instances.DTOs;
using NSubstitute;
using Xunit;

namespace BBT.Workflow.Monitor.Application.Tests;

/// <summary>
/// Pins that the version-addressed monitor endpoints load through the dedicated full-history
/// repository path. They read <see cref="Instance.DataList"/> directly (explicit versions,
/// version listing, diff), so the default detail load — which can be trimmed to the IsLatest row
/// by LatestOnlyInstanceLoading — must never feed them: it would silently produce a one-entry
/// history and false "dataVersionNotFound" answers instead of failing fast.
/// </summary>
public sealed class MonitorInstanceDataHistoryTests
{
    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();

    [Fact]
    public async Task GetInstanceDataAsync_ShouldLoadThroughFullHistoryPath()
    {
        var instance = Instance.Create(Guid.NewGuid(), "test-flow", "1.0.0", "test-key");
        _instanceRepository.FindByIdentifierWithFullHistoryAsync(instance.Id.ToString(), Arg.Any<CancellationToken>())
            .Returns(instance);

        var result = await CreateService().GetInstanceDataAsync(
            new MonitorGetInstanceDataInput { Instance = instance.Id.ToString() },
            CancellationToken.None);

        Assert.True(result.IsSuccess);
        await _instanceRepository.Received(1)
            .FindByIdentifierWithFullHistoryAsync(instance.Id.ToString(), Arg.Any<CancellationToken>());
        await _instanceRepository.DidNotReceiveWithAnyArgs().FindByIdentifierAsReadOnlyAsync(default!);
    }

    [Fact]
    public async Task GetInstanceDataDiffAsync_ShouldLoadThroughFullHistoryPath()
    {
        var instanceRef = Guid.NewGuid().ToString();
        _instanceRepository.FindByIdentifierWithFullHistoryAsync(instanceRef, Arg.Any<CancellationToken>())
            .Returns((Instance?)null);

        var result = await CreateService().GetInstanceDataDiffAsync(
            new MonitorGetInstanceDataDiffInput { Instance = instanceRef, From = "1.0.0", To = "1.0.1" },
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("instance.notFound", result.Error.Code);
        await _instanceRepository.Received(1)
            .FindByIdentifierWithFullHistoryAsync(instanceRef, Arg.Any<CancellationToken>());
        await _instanceRepository.DidNotReceiveWithAnyArgs().FindByIdentifierAsReadOnlyAsync(default!);
    }

    private MonitorInstanceQueryService CreateService() => new(
        Substitute.For<IServiceProvider>(),
        _instanceRepository,
        Substitute.For<IInstanceTransitionRepository>(),
        Substitute.For<IInstanceTaskRepository>(),
        Substitute.For<IInstanceActionRepository>(),
        Substitute.For<IComponentCacheStore>(),
        Substitute.For<IInstanceCorrelationRepository>(),
        Substitute.For<ICurrentSchema>());
}
