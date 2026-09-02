using System.Collections.Generic;
using System.Linq;
using BBT.Workflow.Authorization;
using BBT.Workflow.Authorization.Configuration;
using BBT.Workflow.Authorization.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Authorization;

/// <summary>
/// The provider is chosen once at startup. These pin the two properties that decide what a
/// misconfigured deployment does: an absent or unrecognized provider must degrade to the runtime's
/// original behaviour rather than fail startup or silently deny every request.
/// </summary>
public sealed class CallerRoleProviderOptionsTests
{
    [Fact]
    public void NoSection_ResolvesTheDefaultProvider() =>
        RegisteredResolver(null).ShouldBe(typeof(DefaultCallerRoleResolver));

    [Theory]
    [InlineData("default")]
    [InlineData("unrecognized-provider")]
    [InlineData("")]
    public void NonMorphIdmProvider_ResolvesTheDefaultProvider(string provider) =>
        RegisteredResolver(new() { ["CallerRoleProvider:Provider"] = provider })
            .ShouldBe(typeof(DefaultCallerRoleResolver));

    [Fact]
    public void MorphIdm_IsMatchedCaseInsensitively() =>
        RegisteredResolver(new()
            {
                ["CallerRoleProvider:Provider"] = "Morph-IDM",
                ["CallerRoleProvider:MorphIdm:BaseUrl"] = "https://idm.test"
            })
            .ShouldBe(typeof(MorphIdmCallerRoleResolver));

    /// <summary>
    /// The typed client must be scoped — that lifetime is what makes the resolver's memoization mean
    /// "once per request" rather than "once per process" or "once per call".
    /// </summary>
    [Fact]
    public void MorphIdmResolver_IsRegisteredScoped()
    {
        var services = Build(new()
        {
            ["CallerRoleProvider:Provider"] = "morph-idm",
            ["CallerRoleProvider:MorphIdm:BaseUrl"] = "https://idm.test"
        });

        services.Last(d => d.ServiceType == typeof(ICallerRoleResolver))
            .Lifetime.ShouldBe(ServiceLifetime.Scoped);
    }

    /// <summary>
    /// AddHttpClient registers its typed client through a factory rather than an implementation type,
    /// so the morph-idm registration is identified by the absence of an implementation type.
    /// </summary>
    private static System.Type RegisteredResolver(Dictionary<string, string?>? settings)
    {
        var descriptor = Build(settings).Last(d => d.ServiceType == typeof(ICallerRoleResolver));
        return descriptor.ImplementationType ?? typeof(MorphIdmCallerRoleResolver);
    }

    private static IServiceCollection Build(Dictionary<string, string?>? settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? [])
            .Build();

        var services = new ServiceCollection();
        services.AddCallerRoleResolver(configuration);
        return services;
    }
}
