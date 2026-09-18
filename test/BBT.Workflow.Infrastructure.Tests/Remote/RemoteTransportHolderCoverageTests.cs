using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BBT.Workflow.Remote;
using BBT.Workflow.Remote.Configuration;
using BBT.Workflow.Remote.Extensions;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Remote;

/// <summary>
/// Every class that holds an <see cref="IRemoteTransport{TClient}"/> for itself must be registered
/// through <c>AddRemoteService</c>, because that is the only thing that registers the shell.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="RemoteServiceRegistrationTests"/> pins the SHAPE of that helper. This pins its
/// COVERAGE, which is a different failure: a new remote client registered with a plain
/// <c>AddScoped</c> compiles, passes every unit test that injects a fake, and then cannot be
/// constructed at runtime — its transport is unregistered.
/// </para>
/// <para>
/// It is worse than a cross-domain feature being broken. A routed gateway takes its remote half as
/// a CONCRETE type, so an unregistered remote half fails on every request through that gateway,
/// same-domain ones included. Caught exactly this way on <c>RemoteHumanTaskLeafGateway</c>.
/// </para>
/// </remarks>
public sealed class RemoteTransportHolderCoverageTests
{
    /// <summary>
    /// Finds every type in the infrastructure assembly whose constructor asks for the transport
    /// shell parameterized with itself — the signature of "I am my own remote client".
    /// </summary>
    private static IEnumerable<Type> SelfTransportHolders() =>
        typeof(RemoteTransportRouter<>).Assembly
            .GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false })
            .Where(type => type.GetConstructors()
                .SelectMany(ctor => ctor.GetParameters())
                .Any(parameter =>
                    parameter.ParameterType.IsGenericType
                    && parameter.ParameterType.GetGenericTypeDefinition() == typeof(IRemoteTransport<>)
                    && parameter.ParameterType.GetGenericArguments()[0] == type));

    [Fact]
    public void EverySelfTransportHolderHasItsShellRegistered()
    {
        var holders = SelfTransportHolders().ToList();

        // A guard that guards nothing is worse than no guard: if the detection ever stops matching,
        // this fails loudly instead of passing on an empty set.
        holders.ShouldNotBeEmpty(
            "the detection no longer recognises any remote client — fix the predicate, not this line");

        var registered = BuildRegisteredServices()
            .Select(descriptor => descriptor.ServiceType)
            .Where(type => type.IsGenericType
                           && type.GetGenericTypeDefinition() == typeof(IRemoteTransport<>))
            .Select(type => type.GetGenericArguments()[0])
            .ToHashSet();

        var missing = holders.Where(holder => !registered.Contains(holder)).Select(t => t.Name).ToList();

        missing.ShouldBeEmpty(
            $"these hold IRemoteTransport<self> but are not registered through AddRemoteService: "
            + $"{string.Join(", ", missing)}. A plain AddScoped constructs them with no shell — no "
            + "timeout, no retry, no circuit breaker, no HTTP-vs-Dapr routing — and resolution fails.");
    }

    private static IServiceCollection BuildRegisteredServices()
    {
        var services = new ServiceCollection();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{RemoteOptions.SectionName}:BaseUrl"] = "https://unused.test",
                [$"{RemoteOptions.SectionName}:TimeoutSeconds"] = "30"
            })
            .Build();

        services.AddSingleton<IConfiguration>(configuration);

        var runtimeInfo = Substitute.For<IRuntimeInfoProvider>();
        runtimeInfo.Domain.Returns("core");
        runtimeInfo.Version.Returns("test");
        services.AddSingleton(runtimeInfo);

        services.AddVNextApiServices();

        return services;
    }
}
