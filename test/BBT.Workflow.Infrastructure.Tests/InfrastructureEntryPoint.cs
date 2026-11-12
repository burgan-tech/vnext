using System;
using BBT.Aether.Domain.Services;
using BBT.Aether.MultiSchema;
using BBT.Aether.Uow;
using BBT.Aether.Testing;
using BBT.Aether.Threading;
using BBT.Workflow.Data;
using BBT.Workflow.Runtime;
using Dapr.Jobs.Extensions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace BBT.Workflow;

public class InfrastructureEntryPoint : ModuleEntryPointBase
{
    private SqliteConnection? _sqliteConnection;
    private readonly FixtureFallbackAmbientUnitOfWorkAccessor _ambientUnitOfWork = new();

    public override void Load(IServiceCollection services)
    {
        services.AddDaprClient();
        services.AddDaprJobsClient();

        services.AddInfrastructureModule();
        services.AddAetherMapperlyMapper([
            typeof(WorkflowDomainModuleServiceCollectionExtensions), // Domain
            typeof(WorkflowApplicationModuleServiceCollectionExtensions) // Application
        ]);
        services.AddNetCoreDistributedCache(sc => { sc.AddDistributedMemoryCache(); });

        services.AddSingleton<IDataSeedService, WorkflowTestDataSeedService>();
        // The hosts get IRuntimeInfoProvider from the Domain module, which this fixture does not load;
        // EfCoreInstanceRepository takes it as a constructor dependency.
        services.AddSingleton(Substitute.For<IRuntimeInfoProvider>());

        // Repository calls need a schema and an active unit of work (Aether's schema-bound DbContext
        // provider); the shared repository contract tests call them directly, so the fixture supplies both.
        services.AddSingleton<ICurrentSchema, SqliteFallbackCurrentSchema>();
        services.AddSingleton(_ambientUnitOfWork);
        services.AddSingleton<IAmbientUnitOfWorkAccessor>(_ambientUnitOfWork);

        ConfigureInMemorySqlite(services);
    }

    public override void OnInitialize(IServiceProvider serviceProvider)
    {
        using var scope = serviceProvider.CreateScope();
        scope.ServiceProvider
            .GetRequiredService<WorkflowDbContext>()
            .GetService<IRelationalDatabaseCreator>().CreateTables();

        SeedTestData(serviceProvider);

        // One non-transactional unit of work for the test's lifetime (each test builds its own provider
        // and in-memory database); repositories save through it immediately.
        _ambientUnitOfWork.Fallback = serviceProvider
            .GetRequiredService<IUnitOfWorkManager>()
            .Begin(new UnitOfWorkOptions { IsTransactional = false });
    }

    private void SeedTestData(IServiceProvider serviceProvider)
    {
        AsyncHelper.RunSync(async () =>
        {
            using var scope = serviceProvider.CreateScope();
            await scope.ServiceProvider
                .GetRequiredService<IDataSeedService>()
                .SeedAsync(new SeedContext());
        });
    }

    private void ConfigureInMemorySqlite(IServiceCollection services)
    {
        _sqliteConnection = CreateDatabaseAndGetConnection(services);
        services.AddAetherDbContext<WorkflowDbContext>(new SqliteAetherProvider(_sqliteConnection), "Data Source=:memory:");
    }

    private static SqliteConnection CreateDatabaseAndGetConnection(IServiceCollection services)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }
}