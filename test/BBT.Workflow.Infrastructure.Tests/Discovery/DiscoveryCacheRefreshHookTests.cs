using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Discovery;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Discovery;

/// <summary>
/// Pins <see cref="DiscoveryCacheRefreshHook"/> — the post-deployment hook that is the discovery
/// cache's only automatic invalidation now that entries carry no expiry.
/// </summary>
public sealed class DiscoveryCacheRefreshHookTests
{
    [Fact]
    public async Task The_refresh_is_forced()
    {
        var refresher = Substitute.For<IDiscoveryCacheRefresher>();
        refresher.RefreshAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(DiscoveryCacheRefreshOutcome.Refreshed);

        var result = await new DiscoveryCacheRefreshHook(Options(discoveryEnabled: true), refresher)
            .ExecuteAsync(new PublishCompletedInput(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("Refreshed");

        // Unforced, the marker would make the call a no-op for the rest of the window — and the
        // deployment that just finished is precisely the caller who knows the window's answer is out
        // of date.
        await refresher.Received(1).RefreshAsync(true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_registry_read_is_a_failed_hook()
    {
        var refresher = Substitute.For<IDiscoveryCacheRefresher>();
        refresher.RefreshAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(DiscoveryCacheRefreshOutcome.Failed);

        var result = await new DiscoveryCacheRefreshHook(Options(discoveryEnabled: true), refresher)
            .ExecuteAsync(new PublishCompletedInput(), CancellationToken.None);

        // The CD pipeline has to be able to see this: nothing else will retry it.
        result.IsSuccess.ShouldBeFalse();
    }

    [Theory]
    [InlineData(DiscoveryCacheRefreshOutcome.SkippedNotOwner)]
    [InlineData(DiscoveryCacheRefreshOutcome.SkippedWindowFresh)]
    public async Task A_skipped_refresh_is_a_named_success(DiscoveryCacheRefreshOutcome outcome)
    {
        var refresher = Substitute.For<IDiscoveryCacheRefresher>();
        refresher.RefreshAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(outcome);

        var result = await new DiscoveryCacheRefreshHook(Options(discoveryEnabled: true), refresher)
            .ExecuteAsync(new PublishCompletedInput(), CancellationToken.None);

        // Another replica reading the registry right now writes to the shared layer, which is the
        // outcome this hook wanted. Reporting it as a failure would have CD pipelines retrying a
        // refresh that already happened.
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(outcome.ToString());
    }

    [Fact]
    public async Task No_refresher_reports_disabled_rather_than_vanishing()
    {
        var result = await new DiscoveryCacheRefreshHook(Options(discoveryEnabled: true), refresher: null)
            .ExecuteAsync(new PublishCompletedInput(), CancellationToken.None);

        // Registered under Provider=dapr and with the cache off. An absent HOOK would make "there is
        // no cache here" and "the endpoint forgot to run it" indistinguishable in the answer a CD
        // pipeline gates on.
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(PublishCompletedHookOutcomes.Disabled);
    }

    [Fact]
    public async Task Discovery_switched_off_reports_disabled_without_touching_the_refresher()
    {
        // Found by running it: the cache registration checks Provider and Cache:Enabled but NOT
        // ServiceDiscovery:Enabled, so a single-domain runtime with discovery off still has a
        // refresher. Calling it sent a bulk read to the default registry address, nothing answered,
        // and every deployment of that runtime got success:false for a feature it had turned off.
        var refresher = Substitute.For<IDiscoveryCacheRefresher>();

        var result = await new DiscoveryCacheRefreshHook(Options(discoveryEnabled: false), refresher)
            .ExecuteAsync(new PublishCompletedInput(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(PublishCompletedHookOutcomes.Disabled);
        await refresher.DidNotReceive().RefreshAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    private static IOptions<ServiceDiscoveryOptions> Options(bool discoveryEnabled)
        => Microsoft.Extensions.Options.Options.Create(
            new ServiceDiscoveryOptions { Enabled = discoveryEnabled });
}
