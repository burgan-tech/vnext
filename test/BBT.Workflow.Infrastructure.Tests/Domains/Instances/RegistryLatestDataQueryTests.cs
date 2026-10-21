using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.MultiSchema;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Data;
using BBT.Workflow.DataSink;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace BBT.Workflow.Domains.Instances;

/// <summary>
/// Pins <see cref="EfCoreInstanceRepository.FindLatestDataByKeyAsync"/> against a real PostgreSQL. It is
/// the read behind the flow default-version pointer (<c>sys-version-registry</c>): it must see the last
/// committed row whatever the instance status is (a registry transition commits Busy, a broken one stays
/// Faulted), pick the latest row in SQL instead of loading the data history, and choose deterministically
/// when several instances share the key.
/// </summary>
public sealed class RegistryLatestDataQueryTests : IAsyncLifetime
{
    private const string Flow = "sys-version-registry";

    private PostgreSqlContainer _postgres = null!;
    private string _connectionString = null!;

    async Task IAsyncLifetime.InitializeAsync()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("testdb").WithUsername("test").WithPassword("test")
            .Build();
        await _postgres.StartAsync();
        _connectionString = _postgres.GetConnectionString();

        await using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _postgres.StopAsync();
        await _postgres.DisposeAsync();
    }

    [Theory]
    [InlineData(InstanceStatusCode.Active)]
    [InlineData(InstanceStatusCode.Busy)]
    [InlineData(InstanceStatusCode.Faulted)]
    [InlineData(InstanceStatusCode.Passive)]
    public async Task Returns_The_Latest_Row_Regardless_Of_Status(InstanceStatusCode status)
    {
        var key = $"flow-{Guid.NewGuid():N}";
        await SeedAsync(key, ToStatus(status), DateTime.UtcNow,
            ("1.0.0", """{"defaultVersion":"2.0"}""", false),
            ("1.1.0", """{"defaultVersion":"2.1"}""", true));

        await using var ctx = CreateContext();
        var data = await CreateRepository(ctx).FindLatestDataByKeyAsync(key, CancellationToken.None);

        data.ShouldNotBeNull();
        data!.Version.ShouldBe("1.1.0");
        data.Data.Json.ShouldContain("2.1");
        ctx.ChangeTracker.Entries().ShouldBeEmpty();
    }

    [Fact]
    public async Task Returns_Null_When_No_Instance_Has_The_Key()
    {
        await using var ctx = CreateContext();
        var data = await CreateRepository(ctx)
            .FindLatestDataByKeyAsync($"missing-{Guid.NewGuid():N}", CancellationToken.None);

        data.ShouldBeNull();
    }

    [Fact]
    public async Task Takes_The_Most_Recently_Created_Instance_When_Several_Share_The_Key()
    {
        var key = $"flow-{Guid.NewGuid():N}";
        var now = DateTime.UtcNow;
        await SeedAsync(key, InstanceStatus.Completed, now.AddMinutes(-10),
            ("1.0.0", """{"defaultVersion":"1.0"}""", true));
        await SeedAsync(key, InstanceStatus.Active, now,
            ("1.0.0", """{"defaultVersion":"3.0"}""", true));

        await using var ctx = CreateContext();
        var data = await CreateRepository(ctx).FindLatestDataByKeyAsync(key, CancellationToken.None);

        data!.Data.Json.ShouldContain("3.0");
    }

    [Fact]
    public async Task Falls_Back_To_The_Last_Entered_Row_When_None_Is_Marked_Latest()
    {
        var key = $"flow-{Guid.NewGuid():N}";
        await SeedAsync(key, InstanceStatus.Active, DateTime.UtcNow,
            ("1.0.0", """{"defaultVersion":"2.0"}""", false),
            ("1.10.0", """{"defaultVersion":"2.1"}""", false));

        await using var ctx = CreateContext();
        var data = await CreateRepository(ctx).FindLatestDataByKeyAsync(key, CancellationToken.None);

        data!.Version.ShouldBe("1.10.0");
    }

    /// <summary>Test-local stand-in: <see cref="InstanceStatus"/> values are not compile-time constants.</summary>
    public enum InstanceStatusCode
    {
        Active,
        Busy,
        Faulted,
        Passive
    }

    private static InstanceStatus ToStatus(InstanceStatusCode code) => code switch
    {
        InstanceStatusCode.Busy => InstanceStatus.Busy,
        InstanceStatusCode.Faulted => InstanceStatus.Faulted,
        InstanceStatusCode.Passive => InstanceStatus.Passive,
        _ => InstanceStatus.Active
    };

    private async Task SeedAsync(
        string key, InstanceStatus status, DateTime createdAt,
        params (string Version, string Json, bool IsLatest)[] rows)
    {
        await using var ctx = CreateContext();
        var instance = Instance.Create(Guid.NewGuid(), Flow, "1.0.0", key);
        instance.CreatedAt = createdAt;
        ctx.Instances.Add(instance);
        ctx.Entry(instance).Property(i => i.Status).CurrentValue = status;

        var enteredAt = createdAt;
        foreach (var (version, json, isLatest) in rows)
        {
            var data = new InstanceData(Guid.NewGuid(), instance.Id, version, new JsonData(json), isLatest);
            enteredAt = enteredAt.AddSeconds(1);
            ctx.InstancesData.Add(data);
            ctx.Entry(data).Property(d => d.EnteredAt).CurrentValue = enteredAt;
        }

        await ctx.SaveChangesAsync();
    }

    private WorkflowDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseNpgsql(_connectionString)
            .Options;
        return new WorkflowDbContext(options, new StaticCurrentSchema("public"));
    }

    private static EfCoreInstanceRepository CreateRepository(WorkflowDbContext context) => new(
        new FixedDbContextProvider(context),
        new ServiceCollection().BuildServiceProvider(),
        Substitute.For<IRuntimeInfoProvider>(),
        Substitute.For<IDataSinkManager>(),
        new StaticCurrentSchema("public"),
        Substitute.For<ISchemaValidator>(),
        Options.Create(new WorkflowExecutionOptions()),
        NullLogger<EfCoreInstanceRepository>.Instance);

    private sealed class FixedDbContextProvider(WorkflowDbContext context)
        : IAetherDbContextProvider<WorkflowDbContext>
    {
        public Task<WorkflowDbContext> GetDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(context);
    }
}
