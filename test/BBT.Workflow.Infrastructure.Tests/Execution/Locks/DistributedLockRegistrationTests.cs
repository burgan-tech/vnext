using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BBT.Aether;
using BBT.Aether.DistributedLock;
using BBT.Aether.DistributedLock.Dapr;
using BBT.Workflow.BackgroundJobs.Options;
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
        var defaultLockAgain = provider.GetRequiredService<IDistributedLockService>();
        var postgresLock = provider.GetRequiredService<IPostgreSqlDistributedLockService>();
        var postgresLockAgain = provider.GetRequiredService<IPostgreSqlDistributedLockService>();

        defaultLock.ShouldBeOfType<DaprDistributedLockService>();
        postgresLock.ShouldBeOfType<NpgsqlDistributedLockService>();
        defaultLockAgain.ShouldBeSameAs(defaultLock);
        postgresLockAgain.ShouldBeSameAs(postgresLock);
        ReferenceEquals(defaultLock, postgresLock).ShouldBeFalse();
    }

    [Fact]
    public void Shipped_orchestration_configuration_disables_lease_extension_for_the_dapr_default()
    {
        var repositoryRoot = FindRepositoryRoot();
        var configuration = new ConfigurationBuilder()
            .SetBasePath(repositoryRoot)
            .AddJsonFile(
                "orchestration/BBT.Workflow.Orchestration.HttpApi.Host/appsettings.json",
                optional: false)
            .Build();
        var options = new WorkflowExecutionOptions();

        configuration.GetSection(WorkflowExecutionOptions.SectionName).Bind(options);

        configuration["WorkflowExecution:LockProvider"].ShouldBeNull();
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BBT.Workflow.slnx")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate the vNext repository root.");
    }
}
