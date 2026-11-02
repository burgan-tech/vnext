using System;
using System.Threading.Tasks;
using BBT.Aether.Results;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Gateway;

/// <summary>
/// The DirectTrigger key lookup runs as the target domain through the co-hosted database boundary, exactly as
/// the routed query gateway ran it before <see cref="LocalInstanceIdResolver"/> replaced it (vnext#1107).
/// </summary>
public sealed class LocalInstanceIdResolverTests
{
    [Fact]
    public async Task ResolveIdAsync_RunsThroughTheCoHostedBoundary_AsTheTargetDomain()
    {
        var boundary = Substitute.For<ICoHostedDatabaseBoundary>();
        var expected = Result<Guid>.Ok(Guid.NewGuid());
        boundary.RunAsync(Arg.Any<string?>(), Arg.Any<Func<Task<Result<Guid>>>>()).Returns(expected);
        var resolver = new LocalInstanceIdResolver(Substitute.For<IServiceScopeFactory>(), boundary);

        var result = await resolver.ResolveIdAsync("sales", "orders", "key-1");

        result.ShouldBe(expected);
        await boundary.Received(1).RunAsync("sales", Arg.Any<Func<Task<Result<Guid>>>>());
    }
}
