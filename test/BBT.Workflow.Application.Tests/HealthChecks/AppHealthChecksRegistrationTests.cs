using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace BBT.Workflow.HealthChecks;

public sealed class AppHealthChecksRegistrationTests
{
    [Fact]
    public void AddAppHealthChecks_RegistersSelfCheckOnly_NoDatabaseCheck()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        services.AddAppHealthChecks();

        var sp = services.BuildServiceProvider();
        var registrations = sp.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations;

        registrations.ShouldNotContain(r => r.Name == "database",
            "DB health check must not be registered in the shared extension");
        registrations.ShouldContain(r => r.Name == "self" && r.Tags.Contains("live"));
    }
}
