using System.Collections.Generic;
using BBT.Aether;
using BBT.Aether.DistributedLock;
using BBT.Aether.DistributedLock.Dapr;
using BBT.Workflow.Infrastructure.Execution.Locks;
using Dapr.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Execution.Locks;

public sealed class DistributedLockRegistrationTests
{
    [Fact]
    public void Postgres_contract_inherits_the_aether_contract()
    {
        typeof(IDistributedLockService)
            .IsAssignableFrom(typeof(IPostgreSqlDistributedLockService))
            .ShouldBeTrue();
    }

    [Fact]
    public void AddDistributedLock_keeps_dapr_as_default_and_registers_postgres_separately()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DAPR_LOCK_STORE_NAME"] = "lock-store",
                ["WorkflowExecution:LockProvider"] = "Postgres",
                ["ConnectionStrings:Default"] =
                    "Host=localhost;Database=locks;Username=postgres;Password=postgres"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLogging();
        services.AddSingleton(Substitute.For<DaprClient>());
        services.AddSingleton(Substitute.For<IApplicationInfoAccessor>());

        services.AddDistributedLock(configuration);

        using var provider = services.BuildServiceProvider();
        var defaultLock = provider.GetRequiredService<IDistributedLockService>();
        var postgresLock = provider.GetRequiredService<IPostgreSqlDistributedLockService>();

        defaultLock.ShouldBeOfType<DaprDistributedLockService>();
        postgresLock.ShouldBeOfType<NpgsqlDistributedLockService>();
        ReferenceEquals(defaultLock, postgresLock).ShouldBeFalse();
    }
}
