using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.MultiSchema;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Data;
using BBT.Workflow.DataSink;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domains.Instances;

/// <summary>
/// Pins, against a real PostgreSQL, the two queries a floating component version is resolved with: the
/// version list reads the version column only — never the jsonb body, whose size times the publish history
/// is what ran the runtime out of memory — and the winner is then read as exactly one row.
/// </summary>
public sealed class ComponentVersionLookupIntegrationTests(AttributeIndexPostgresFixture fixture) : IAsyncLifetime,
    IClassFixture<AttributeIndexPostgresFixture>
{
    private const string Key = "contract-approval-workflow";

    private static readonly string[] Published =
    [
        "1.0.0-pkg.1.0.9+core",
        "1.0.0-pkg.1.0.99+core",
        "1.0.0-pkg.1.0.123+core",
        "1.1.0-pkg.1.0.1+core"
    ];

    private string _connection = null!;
    private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();

    public async Task InitializeAsync()
    {
        var database = "versions_" + Guid.NewGuid().ToString("N");
        await using (var admin = new NpgsqlConnection(fixture.Postgres.GetConnectionString()))
        {
            await admin.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin);
            await command.ExecuteNonQueryAsync();
        }

        _connection = new NpgsqlConnectionStringBuilder(fixture.Postgres.GetConnectionString()) { Database = database }.ConnectionString;
        await using var db = CreateContext();
        await db.Database.EnsureCreatedAsync();

        // One component instance whose rows are its published versions, as the definition store keeps them.
        var component = Instance.Create(Guid.NewGuid(), "sys-flows", "1.0.0", Key);
        db.Instances.Add(component);
        for (var i = 0; i < Published.Length; i++)
        {
            component.AcceptPersistedData(new InstanceData(Guid.NewGuid(), component.Id, Published[i],
                new JsonData("{\"body\":\"" + Published[i] + "\"}"), i == Published.Length - 1) { VersionNo = 1 });
        }

        // A re-write of 1.0.0-pkg.1.0.123+core: same version string, next ordinal, newer body.
        component.AcceptPersistedData(new InstanceData(Guid.NewGuid(), component.Id, "1.0.0-pkg.1.0.123+core",
            new JsonData("{\"body\":\"rewritten\"}"), false) { VersionNo = 2 });

        // Another key's versions must never leak into the list.
        var other = Instance.Create(Guid.NewGuid(), "sys-flows", "1.0.0", "other-flow");
        db.Instances.Add(other);
        other.AcceptPersistedData(new InstanceData(Guid.NewGuid(), other.Id, "9.0.0-pkg.1.0.0+core",
            new JsonData("{}"), true) { VersionNo = 1 });

        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        _services.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task GetActiveVersionsByKey_ReadsTheVersionColumnOnly()
    {
        var capture = new ReadCapture();
        await using var db = CreateContext(capture);

        var versions = await CreateRepository(db).GetActiveVersionsByKeyAsync(Key);

        versions.OrderBy(v => v, StringComparer.Ordinal).ShouldBe(Published.OrderBy(v => v, StringComparer.Ordinal));
        capture.Commands.ShouldHaveSingleItem();
        capture.Commands[0].ShouldNotContain("\"Data\"", customMessage: "the version list must not read any body");
    }

    [Fact]
    public async Task FindActiveData_ArtifactVersion_ReturnsTheHighestPackageAndItsLatestWrite()
    {
        await using var db = CreateContext();

        var row = await CreateRepository(db).FindActiveDataAsync(Key, "1.0.0");

        row.ShouldNotBeNull();
        row.InstanceData.Version.ShouldBe("1.0.0-pkg.1.0.123+core");
        row.InstanceData.VersionNo.ShouldBe(2);
        row.InstanceData.Data.JsonElement.GetProperty("body").GetString().ShouldBe("rewritten");
    }

    [Fact]
    public async Task FindActiveData_ArtifactVersion_ReadsOneBodyOnly()
    {
        var capture = new ReadCapture();
        await using var db = CreateContext(capture);

        await CreateRepository(db).FindActiveDataAsync(Key, "1.0");

        capture.Commands.Count(c => c.Contains("\"Data\"")).ShouldBe(1);
        capture.Commands.Single(c => c.Contains("\"Data\"")).ShouldContain("LIMIT");
    }

    private WorkflowDbContext CreateContext(ReadCapture? capture = null)
    {
        var options = new DbContextOptionsBuilder<WorkflowDbContext>().UseNpgsql(_connection);
        if (capture != null) options.AddInterceptors(capture);
        return new WorkflowDbContext(options.Options, new StaticCurrentSchema("public"));
    }

    private EfCoreInstanceRepository CreateRepository(WorkflowDbContext db)
    {
        var validator = Substitute.For<ISchemaValidator>();
        validator.ValidateSchemaAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(call => call.ArgAt<string>(0));
        validator.ValidateSchemaSync(Arg.Any<string?>()).Returns(call => call.ArgAt<string>(0));
        validator.ValidateTableName(Arg.Any<string?>()).Returns(call => call.ArgAt<string>(0));
        return new EfCoreInstanceRepository(new FixedProvider(db), _services, Substitute.For<IRuntimeInfoProvider>(),
            Substitute.For<IDataSinkManager>(), new StaticCurrentSchema("public"), validator,
            Options.Create(new WorkflowExecutionOptions { LatestOnlyInstanceLoading = true }),
            NullLogger<EfCoreInstanceRepository>.Instance, new QueryMonitor(new InstanceQueryOptions()));
    }

    private sealed class FixedProvider(WorkflowDbContext context) : IAetherDbContextProvider<WorkflowDbContext>
    {
        public Task<WorkflowDbContext> GetDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(context);
    }

    private sealed class QueryMonitor(InstanceQueryOptions options) : IOptionsMonitor<InstanceQueryOptions>
    {
        public InstanceQueryOptions CurrentValue { get; } = options;
        public InstanceQueryOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<InstanceQueryOptions, string?> listener) => null;
    }

    private sealed class ReadCapture : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }
}
