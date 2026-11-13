using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Uow;
using BBT.Workflow.Definitions;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Caching;

/// <summary>
/// Pins that <see cref="RuntimeCacheBackend{T}"/> decides which version a request means from the version
/// strings alone and then loads exactly one body — never every stored body of the component.
/// </summary>
public sealed class RuntimeCacheBackendTests
{
    private const string Domain = CacheSetTestHarness.TestDomain;
    private const string Key = CacheSetTestHarness.TestKey;

    private readonly IRuntimeService _runtime = Substitute.For<IRuntimeService>();
    private readonly RuntimeCacheBackend<View> _sut;

    public RuntimeCacheBackendTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_runtime);
        services.AddSingleton(Substitute.For<IRuntimeInfoProvider>());
        services.AddSingleton(Substitute.For<IUnitOfWorkManager>());

        _sut = new RuntimeCacheBackend<View>(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<RuntimeCacheBackend<View>>.Instance);
    }

    [Fact]
    public async Task LoadVersionsAsync_ReturnsTheStoredVersionStrings()
    {
        Published("1.0.0-pkg.1.0.9+core", "1.0.0-pkg.1.0.123+core");

        var result = await _sut.LoadVersionsAsync(Domain, Key);

        result.Value!.ShouldBe(["1.0.0-pkg.1.0.9+core", "1.0.0-pkg.1.0.123+core"]);
        await NeverLoadedEveryBody();
    }

    [Fact]
    public async Task FullVersionWithoutBuildMetadata_LoadsTheStoredRowAlone()
    {
        Published("1.0.0-pkg.1.0.99+core", "1.0.0-pkg.1.0.123+core");
        Body("1.0.0-pkg.1.0.123+core");

        var result = await _sut.LoadAsync(Domain, Key, "1.0.0-pkg.1.0.123");

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Version.ShouldBe("1.0.0-pkg.1.0.123+core");
        await NeverLoadedEveryBody();
    }

    [Fact]
    public async Task FullVersionMatchingTwoBuildMetadataSpellings_PicksTheOrdinalFirstEveryTime()
    {
        Published("2.1.0-pkg.1.0.168+customer", "2.1.0-pkg.1.0.168+core");
        Body("2.1.0-pkg.1.0.168+core");

        var result = await _sut.LoadAsync(Domain, Key, "2.1.0-pkg.1.0.168");

        result.Value!.Version.ShouldBe("2.1.0-pkg.1.0.168+core");
        await _runtime.DidNotReceive().GetAsync<View>(Key, "2.1.0-pkg.1.0.168+customer", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FullVersionNotPublished_IsNotFound()
    {
        Published("1.0.0-pkg.1.0.99+core");

        var result = await _sut.LoadAsync(Domain, Key, "1.0.0-pkg.1.0.123");

        result.IsSuccess.ShouldBeFalse();
        await NeverLoadedEveryBody();
    }

    [Fact]
    public async Task ArtifactVersion_IsResolvedByTheSingleRowLookup()
    {
        _runtime.GetAsync<View>(Key, "1.0.0", Arg.Any<CancellationToken>())
            .Returns(CacheSetTestHarness.CreateView("1.0.0-pkg.1.0.123+core"));

        var result = await _sut.LoadAsync(Domain, Key, "1.0.0");

        result.Value!.Version.ShouldBe("1.0.0-pkg.1.0.123+core");
        await NeverLoadedEveryBody();
    }

    private void Published(params string[] versions)
        => _runtime.GetVersionsAsync<View>(Key, Arg.Any<CancellationToken>())
            .Returns(new List<string>(versions));

    private void Body(string version)
        => _runtime.GetAsync<View>(Key, version, Arg.Any<CancellationToken>())
            .Returns(CacheSetTestHarness.CreateView(version));

    private Task NeverLoadedEveryBody()
        => _runtime.DidNotReceive().GetAsync<View>(Key, Arg.Any<CancellationToken>());
}
