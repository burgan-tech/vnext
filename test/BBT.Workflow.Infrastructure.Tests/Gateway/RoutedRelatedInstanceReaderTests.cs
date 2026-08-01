using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Gateway;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting.Related;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Gateway;

/// <summary>
/// Covers <see cref="RoutedRelatedInstanceReader"/>'s routing decision — local vs remote by domain —
/// and the DI registrations that back it. The router takes both sides as plain
/// <see cref="IRelatedInstanceReader"/> instances (not concrete types like
/// <see cref="RoutedInstanceQueryGateway"/> does), specifically so it can be constructed directly with
/// substitutes here; keyed-service attributes are ignored on direct construction.
/// </summary>
public sealed class RoutedRelatedInstanceReaderTests
{
    private static readonly Guid TargetId = Guid.Parse("12121212-1212-1212-1212-121212121212");

    private static RelatedInstanceRef Local() => new(TargetId, "lending", "loan-application", "2.1.0");
    private static RelatedInstanceRef Foreign() => new(TargetId, "compliance", "kyc-flow", "1.0.0");

    private sealed record Harness(
        RoutedRelatedInstanceReader Reader,
        IRelatedInstanceReader LocalReader,
        IRelatedInstanceReader RemoteReader);

    private static Harness CreateHarness()
    {
        var runtime = Substitute.For<IRuntimeInfoProvider>();
        runtime.IsDomainMatch("lending").Returns(true);
        runtime.IsDomainMatch("compliance").Returns(false);

        var local = Substitute.For<IRelatedInstanceReader>();
        var remote = Substitute.For<IRelatedInstanceReader>();

        local.ReadAsync(Arg.Any<RelatedInstanceRef>(), Arg.Any<CancellationToken>())
            .Returns(Result<RelatedInstanceSnapshot?>.Ok(null));
        remote.ReadAsync(Arg.Any<RelatedInstanceRef>(), Arg.Any<CancellationToken>())
            .Returns(Result<RelatedInstanceSnapshot?>.Ok(null));
        local.ReadManyAsync(Arg.Any<IReadOnlyList<RelatedInstanceRef>>(), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<RelatedInstanceSnapshot>>.Ok([]));
        remote.ReadManyAsync(Arg.Any<IReadOnlyList<RelatedInstanceRef>>(), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<RelatedInstanceSnapshot>>.Ok([]));

        return new Harness(new RoutedRelatedInstanceReader(runtime, local, remote), local, remote);
    }

    [Fact]
    public async Task ReadAsync_ShouldUseLocalReader_WhenDomainMatches()
    {
        var harness = CreateHarness();

        await harness.Reader.ReadAsync(Local(), CancellationToken.None);

        await harness.LocalReader.Received(1).ReadAsync(Arg.Any<RelatedInstanceRef>(), Arg.Any<CancellationToken>());
        await harness.RemoteReader.DidNotReceiveWithAnyArgs().ReadAsync(default!, default);
    }

    [Fact]
    public async Task ReadAsync_ShouldUseRemoteReader_WhenDomainDiffers()
    {
        var harness = CreateHarness();

        await harness.Reader.ReadAsync(Foreign(), CancellationToken.None);

        await harness.RemoteReader.Received(1).ReadAsync(Arg.Any<RelatedInstanceRef>(), Arg.Any<CancellationToken>());
        await harness.LocalReader.DidNotReceiveWithAnyArgs().ReadAsync(default!, default);
    }

    [Fact]
    public async Task ReadManyAsync_ShouldSplitByDomain()
    {
        var harness = CreateHarness();

        await harness.Reader.ReadManyAsync([Local(), Foreign()], CancellationToken.None);

        await harness.LocalReader.Received(1).ReadManyAsync(
            Arg.Is<IReadOnlyList<RelatedInstanceRef>>(refs => refs.Count == 1 && refs[0].Domain == "lending"),
            Arg.Any<CancellationToken>());
        await harness.RemoteReader.Received(1).ReadManyAsync(
            Arg.Is<IReadOnlyList<RelatedInstanceRef>>(refs => refs.Count == 1 && refs[0].Domain == "compliance"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReadManyAsync_ShouldNotCallRemote_WhenEveryRefIsLocal()
    {
        var harness = CreateHarness();

        await harness.Reader.ReadManyAsync([Local(), Local()], CancellationToken.None);

        await harness.RemoteReader.DidNotReceiveWithAnyArgs().ReadManyAsync(default!, default);
    }

    [Fact]
    public async Task ReadManyAsync_ShouldFail_WhenOneSideFails()
    {
        var harness = CreateHarness();
        harness.RemoteReader
            .ReadManyAsync(Arg.Any<IReadOnlyList<RelatedInstanceRef>>(), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<RelatedInstanceSnapshot>>.Fail(
                Error.Failure("RELATED_READ", "compliance unreachable")));

        var result = await harness.Reader.ReadManyAsync([Local(), Foreign()], CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Message.ShouldContain("compliance unreachable");
    }

    [Fact]
    public void AddInstanceGatewayServices_ShouldRegisterTheRelatedInstanceReader()
    {
        // The reader is an optional dependency of ScriptContextBuilder: if this registration is ever
        // dropped, every script silently gets a no-op accessor reporting "no parent, no correlations"
        // with no error anywhere. This test is the guard.
        var services = new ServiceCollection();

        services.AddInstanceGatewayServices();

        services.ShouldContain(descriptor =>
            descriptor.ServiceType == typeof(IRelatedInstanceReader) &&
            descriptor.ImplementationType == typeof(RoutedRelatedInstanceReader));
    }

    [Fact]
    public void AddInstanceGatewayServices_ShouldRegisterBothKeyedSides()
    {
        // The router cannot resolve without both keyed entries; a missing one would only surface at
        // first script execution rather than at startup.
        var services = new ServiceCollection();

        services.AddInstanceGatewayServices();

        services.ShouldContain(descriptor =>
            descriptor.ServiceType == typeof(IRelatedInstanceReader) &&
            descriptor.IsKeyedService &&
            Equals(descriptor.ServiceKey, RelatedReaderKeys.Local) &&
            descriptor.KeyedImplementationType == typeof(LocalRelatedInstanceReader));

        services.ShouldContain(descriptor =>
            descriptor.ServiceType == typeof(IRelatedInstanceReader) &&
            descriptor.IsKeyedService &&
            Equals(descriptor.ServiceKey, RelatedReaderKeys.Remote));
    }
}
