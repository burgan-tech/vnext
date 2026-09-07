using System;
using Microsoft.Extensions.DependencyInjection;
using BBT.Workflow.Remote;
using BBT.Workflow.Remote.Configuration;
using BBT.Workflow.Remote.Extensions;
using BBT.Workflow.Runtime;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Remote;

/// <summary>
/// Pins the registration shape of <c>AddRemoteService</c>: the implementation is registered through a
/// factory, so a host that wires the gateways without domain discovery still starts.
/// </summary>
/// <remarks>
/// The remote app services depend on <c>IDomainDiscoveryResolver</c>, which only
/// <c>AddDomainDiscovery</c> registers. DbMigrator registers the gateways through
/// <c>AddInfrastructureModule</c> and never calls it — and never resolves a remote service either.
/// A plain <c>AddTransient&lt;TClient, TImplementation&gt;()</c> lets <c>ValidateOnBuild</c> inspect
/// the constructor and abort that host at startup; the pre-Dapr typed <c>AddHttpClient</c> was a
/// factory and never did. Found in the local cross-domain lab, where the migrator image exited with
/// "Unable to resolve service for type IDomainDiscoveryResolver".
/// </remarks>
public sealed class RemoteServiceRegistrationTests
{
    /// <summary>Stands in for <c>IDomainDiscoveryResolver</c>: required by the client, registered by nobody.</summary>
    public interface IUnregisteredDependency;

    /// <summary>Minimal remote client whose constructor cannot be satisfied by the container.</summary>
    public sealed class ProbeClient(IRemoteTransport<ProbeClient> transport, IUnregisteredDependency dependency)
    {
        public IRemoteTransport<ProbeClient> Transport => transport;
        public IUnregisteredDependency Dependency => dependency;
    }

    [Fact]
    public void ValidateOnBuild_Should_Pass_When_Client_Dependency_Is_Not_Registered()
    {
        var services = BuildServices();

        var act = () => services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });

        // The migrator contract: the host starts; the missing resolver is not its concern.
        act.ShouldNotThrow();
    }

    [Fact]
    public void Resolving_Client_Should_Fail_At_First_Use_When_Dependency_Is_Missing()
    {
        var provider = BuildServices().BuildServiceProvider();

        // Failure is deferred to the host that actually makes a cross-domain call — not hidden.
        Should.Throw<InvalidOperationException>(() => provider.GetRequiredService<ProbeClient>())
            .Message.ShouldContain(nameof(IUnregisteredDependency));
    }

    [Fact]
    public void Resolving_Client_Should_Succeed_When_Dependency_Is_Registered()
    {
        var services = BuildServices();
        services.AddSingleton(Substitute.For<IUnregisteredDependency>());

        var client = services.BuildServiceProvider().GetRequiredService<ProbeClient>();

        client.Transport.ShouldBeOfType<RemoteTransportRouter<ProbeClient>>();
        client.Dependency.ShouldNotBeNull();
    }

    private static ServiceCollection BuildServices()
    {
        var services = new ServiceCollection();

        var runtimeInfo = Substitute.For<IRuntimeInfoProvider>();
        runtimeInfo.Domain.Returns("credit");
        runtimeInfo.Version.Returns("test");
        services.AddSingleton(runtimeInfo);
        services.Configure<RemoteOptions>(_ => { });

        services.AddRemoteService<ProbeClient, ProbeClient>(new RemoteOptions
        {
            BaseUrl = "https://unused.test",
            TimeoutSeconds = 30
        });

        return services;
    }
}
