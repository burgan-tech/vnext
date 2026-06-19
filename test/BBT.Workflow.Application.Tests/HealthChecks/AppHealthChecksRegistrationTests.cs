using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Shouldly;
using Xunit;

namespace BBT.Workflow.HealthChecks;

/// <summary>
/// Verifies the <c>AddAppHealthChecks</c> registration contract:
/// database check presence is controlled by the <c>includeDatabaseCheck</c> flag.
/// </summary>
public class AppHealthChecksRegistrationTests
{
    private static IServiceCollection BuildServices()
    {
        var services = new ServiceCollection();

        // Minimal IConfiguration so GetConfiguration() inside the extension does not throw.
        var config = new ConfigurationBuilder()
            .AddJsonStream(new System.IO.MemoryStream(
                System.Text.Encoding.UTF8.GetBytes(
                    """{"ConnectionStrings":{"Default":"Host=localhost;Database=test;Username=test;Password=test"}}""")))
            .Build();

        services.AddSingleton<IConfiguration>(config);
        return services;
    }

    [Fact]
    public void Without_database_check_no_database_registration_exists()
    {
        var services = BuildServices();
        services.AddAppHealthChecks(includeDatabaseCheck: false);

        var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<HealthCheckServiceOptions>>().Value;

        options.Registrations.Any(r => r.Name == "database").ShouldBeFalse();
    }

    [Fact]
    public void With_database_check_database_registration_exists_with_ready_tag_and_timeout()
    {
        var services = BuildServices();
        services.AddAppHealthChecks(includeDatabaseCheck: true);

        var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<HealthCheckServiceOptions>>().Value;

        var db = options.Registrations.SingleOrDefault(r => r.Name == "database");
        db.ShouldNotBeNull();
        db.Tags.ShouldContain("ready");
        db.Timeout.ShouldBe(System.TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Self_check_always_registered_with_live_tag()
    {
        var services = BuildServices();
        services.AddAppHealthChecks(includeDatabaseCheck: false);

        var sp = services.BuildServiceProvider();
        var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<HealthCheckServiceOptions>>().Value;

        var self = options.Registrations.SingleOrDefault(r => r.Name == "self");
        self.ShouldNotBeNull();
        self.Tags.ShouldContain("live");
    }
}
