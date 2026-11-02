using System;
using System.Linq;
using System.Reflection;
using BBT.Aether.Users;
using BBT.Workflow.Instances;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Authorization.RowSecurity;

/// <summary>
/// Pins the DI wiring of row security (vnext#1107): <see cref="InstanceQueryAppService"/> takes
/// <see cref="IRowSecurityService"/> as an OPTIONAL constructor parameter (null = the pre-feature path, kept for
/// hand-built test factories). A host that resolved the app service without the service would serve every list and
/// GET unfiltered, silently. This test builds the application module and asserts the container injects it.
/// </summary>
/// <remarks>
/// Every other constructor dependency of the app service that is an interface is stood in with an NSubstitute fake,
/// so the graph resolves without Infrastructure; <see cref="IRowSecurityService"/> itself, and
/// <see cref="RowSecurityService"/>'s own options, come from the real <c>AddApplicationModule</c> registrations.
/// </remarks>
public sealed class RowSecurityDiRegistrationTests
{
    [Fact]
    public void ApplicationModule_InjectsRowSecurityService_IntoInstanceQueryAppService()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        services.AddApplicationModule();

        var ctor = typeof(InstanceQueryAppService).GetConstructors().ShouldHaveSingleItem();
        foreach (var parameter in ctor.GetParameters())
        {
            var type = parameter.ParameterType;
            if (!type.IsInterface || type == typeof(IRowSecurityService) || type == typeof(IServiceProvider) ||
                (type.IsGenericType && type.GetGenericTypeDefinition() is var g &&
                 (g == typeof(Microsoft.Extensions.Options.IOptions<>) || g == typeof(Microsoft.Extensions.Logging.ILogger<>))))
                continue;
            var fake = Substitute.For([type], []);
            services.AddScoped(type, _ => fake);
        }
        services.AddScoped(_ => Substitute.For<ICurrentUser>());

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetService<IRowSecurityService>().ShouldBeOfType<RowSecurityService>();
        var appService = scope.ServiceProvider.GetRequiredService<IInstanceQueryAppService>().ShouldBeOfType<InstanceQueryAppService>();
        var captured = typeof(InstanceQueryAppService)
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(f => f.FieldType == typeof(IRowSecurityService))
            .ShouldHaveSingleItem("the primary-constructor parameter rowSecurityService is captured in one field");
        captured.GetValue(appService).ShouldBeOfType<RowSecurityService>();
    }
}
