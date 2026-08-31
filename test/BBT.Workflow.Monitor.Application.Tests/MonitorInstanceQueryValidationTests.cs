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
/// Verifies that the monitor instance list rejects a query it cannot execute as authored, without
/// touching the repository.
/// </summary>
/// <remarks>
/// The monitor's previous local validation had two holes that the shared
/// <c>InstanceQueryValidator</c> closes: an unsupported operator never threw at all, and a filter
/// truncated so it no longer ends in <c>}</c> failed the <c>DetectFormat</c> pre-check and was
/// skipped entirely. Both returned every instance with HTTP 200.
/// </remarks>
public sealed class MonitorInstanceQueryValidationTests
{
    private readonly IInstanceRepository _instanceRepository = Substitute.For<IInstanceRepository>();

    [Theory]
    // Unsupported operator using the schema-side spelling.
    [InlineData("""{"attributes":{"amount":{"gte":100}}}""")]
    // Entirely unknown operator.
    [InlineData("""{"attributes":{"amount":{"zzz":100}}}""")]
    // Truncated so it no longer ends with a brace.
    [InlineData("""{"attributes":{"amount":{"eq":100""")]
    // One brace short.
    [InlineData("""{"attributes":{"amount":{"eq":100}}""")]
    public async Task GetInstancesAsync_ShouldRejectBadFilter_WithoutQuerying(string filter)
    {
        var result = await CreateService().GetInstancesAsync(
            new MonitorGetInstancesInput { Domain = "d", Workflow = "w", Filter = filter },
            CancellationToken.None);

        Assert.False(result.IsSuccess);

        await _instanceRepository.DidNotReceiveWithAnyArgs().GetPagedResultsWithGroupsAsync(
            default, default, default, default, default, default, default, default);
    }

    [Fact]
    public async Task GetInstancesAsync_ShouldRejectBadSort_WithoutQuerying()
    {
        var result = await CreateService().GetInstancesAsync(
            new MonitorGetInstancesInput
            {
                Domain = "d",
                Workflow = "w",
                Sort = """{"field":"createdAt","direction":"sideways"}"""
            },
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(WorkflowErrorCodes.InstanceSortInvalid, result.Error.Code);

        await _instanceRepository.DidNotReceiveWithAnyArgs().GetPagedResultsWithGroupsAsync(
            default, default, default, default, default, default, default, default);
    }

    [Fact]
    public async Task GetInstancesAsync_ShouldSuggestTheCorrectOperator()
    {
        var result = await CreateService().GetInstancesAsync(
            new MonitorGetInstancesInput
            {
                Domain = "d",
                Workflow = "w",
                Filter = """{"attributes":{"amount":{"gte":100}}}"""
            },
            CancellationToken.None);

        Assert.Contains("Did you mean 'ge'?", result.Error.Message);
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
