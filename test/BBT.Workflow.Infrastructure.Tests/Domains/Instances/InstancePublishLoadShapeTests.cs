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
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace BBT.Workflow.Domains.Instances;

/// <summary>
/// Pins the database commands of the definition publish path against a real PostgreSQL: the instance
/// is loaded for the append without any data row, and "does this version already exist" is answered
/// from the version strings alone. Publishing used to load every stored body through
/// <see cref="EfCoreInstanceRepository.FindByIdentifierWithFullDataAsync"/> — with ~190 versions of a
/// ~1.3 MB flow that took the orchestration host from ~400 MB to ~1.3 GB per publish.
/// </summary>
[Collection(EfCoreInstanceRepositoryPrepareSpanTests.SpanCollection)]
public sealed class InstancePublishLoadShapeTests : IAsyncLifetime
{
    /// <summary>
    /// The quoted body column as EF emits it. <c>"DataHash"</c> and <c>"InstancesData"</c> do not contain it.
    /// </summary>
    private const string DataColumn = "\"Data\"";

    private PostgreSqlContainer _postgresContainer = null!;
    private string _connectionString = null!;

    async Task IAsyncLifetime.InitializeAsync()
    {
        _postgresContainer = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("testdb")
            .WithUsername("test")
            .WithPassword("test")
            .Build();

        await _postgresContainer.StartAsync();
        _connectionString = _postgresContainer.GetConnectionString();

        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _postgresContainer.StopAsync();
        await _postgresContainer.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Append_load_reads_no_data_row_and_stays_tracked(bool latestOnly)
    {
        var seeded = await SeedAsync("publish-load-by-key");
        var recorder = new CommandRecorder();
        await using var ctx = CreateContext(recorder);
        var repository = CreateRepository(ctx, latestOnly);

        var loaded = await repository.FindByIdentifierForDataAppendAsync("publish-load-by-key", CancellationToken.None);

        loaded.ShouldNotBeNull();
        loaded!.Id.ShouldBe(seeded.Id);
        loaded.DataList.ShouldBeEmpty();
        loaded.IsDataPartiallyLoaded.ShouldBeTrue();
        loaded.ChildCorrelations.Count.ShouldBe(1);
        loaded.Tags.ShouldBe(["seeded"]);
        ctx.Entry(loaded).State.ShouldBe(EntityState.Unchanged);
        recorder.Commands.ShouldNotBeEmpty();
        recorder.Commands.ShouldAllBe(c => !c.Contains("\"InstancesData\""));
    }

    [Fact]
    public async Task Append_load_resolves_an_id_identifier()
    {
        var seeded = await SeedAsync("publish-load-by-id");
        await using var ctx = CreateContext();
        var repository = CreateRepository(ctx, latestOnly: false);

        var loaded = await repository.FindByIdentifierForDataAppendAsync(seeded.Id.ToString(), CancellationToken.None);

        loaded.ShouldNotBeNull();
        loaded!.Id.ShouldBe(seeded.Id);
        loaded.DataList.ShouldBeEmpty();
    }

    [Fact]
    public async Task Append_load_returns_null_for_an_unknown_identifier()
    {
        await using var ctx = CreateContext();
        var repository = CreateRepository(ctx, latestOnly: false);

        (await repository.FindByIdentifierForDataAppendAsync("never-published", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Tag_change_through_the_append_load_is_saved()
    {
        // The publish path applies the tags to this aggregate and saves it through UpdateAsync: the
        // lean load must stay change-tracked, as the full-data load was.
        var seeded = await SeedAsync("publish-tags");
        await using (var ctx = CreateContext())
        {
            var repository = CreateRepository(ctx, latestOnly: false);
            var loaded = await repository.FindByIdentifierForDataAppendAsync("publish-tags", CancellationToken.None);
            loaded!.AddTags(["seeded", "added"]);
            await repository.UpdateAsync(loaded, true, CancellationToken.None);
        }

        await using var verify = CreateContext();
        var row = await verify.Instances.AsNoTracking().SingleAsync(i => i.Id == seeded.Id);
        row.Tags.ShouldBe(["seeded", "added"]);
        (await verify.InstancesData.CountAsync(d => d.InstanceId == seeded.Id)).ShouldBe(4);
    }

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("1.1.0")]
    [InlineData("1.2.0")]
    [InlineData("1.3.0")]
    [InlineData("1.1")]
    [InlineData("1")]
    [InlineData("2")]
    [InlineData("latest")]
    [InlineData(null)]
    public async Task Version_check_gives_the_same_answer_as_FindData_on_the_full_history(string? requested)
    {
        // Parity with the check it replaces: Instance.FindData on the aggregate loaded with every row.
        var seeded = await SeedAsync($"publish-parity-{requested ?? "null"}");
        await using var ctx = CreateContext();
        var repository = CreateRepository(ctx, latestOnly: false);
        var full = await repository.FindByIdentifierWithFullDataAsync(seeded.Id.ToString(), CancellationToken.None);

        var expected = full!.FindData(requested)?.Version;
        var resolved = await repository.FindDataVersionAsync(seeded.Id, requested, CancellationToken.None);

        resolved.ShouldBe(expected);
    }

    [Fact]
    public async Task Version_check_reads_version_strings_only()
    {
        var seeded = await SeedAsync("publish-version-only");
        var recorder = new CommandRecorder();
        await using var ctx = CreateContext(recorder);
        var repository = CreateRepository(ctx, latestOnly: false);

        var existing = await repository.FindDataVersionAsync(seeded.Id, "1.1.0", CancellationToken.None);
        var missing = await repository.FindDataVersionAsync(seeded.Id, "1.3.0", CancellationToken.None);

        existing.ShouldBe("1.1.0");
        missing.ShouldBeNull();
        recorder.Commands.Count.ShouldBe(2);
        recorder.Commands.ShouldAllBe(c => c.Contains("\"InstancesData\""));
        recorder.BodyReads.ShouldBeEmpty();
    }

    [Fact]
    public async Task Version_check_is_scoped_to_the_instance()
    {
        var seeded = await SeedAsync("publish-scope-a");
        await SeedAsync("publish-scope-b", versions: ["9.9.9"]);
        await using var ctx = CreateContext();
        var repository = CreateRepository(ctx, latestOnly: false);

        (await repository.FindDataVersionAsync(seeded.Id, "9.9.9", CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task Row_lookup_loads_one_body_the_one_FindData_returns()
    {
        var seeded = await SeedAsync("publish-row");
        var recorder = new CommandRecorder();
        await using var ctx = CreateContext(recorder);
        var repository = CreateRepository(ctx, latestOnly: false);

        var row = await repository.FindDataByVersionAsync(seeded.Id, "1.1.0", CancellationToken.None);

        row.ShouldNotBeNull();
        row!.Version.ShouldBe("1.1.0");
        row.VersionNo.ShouldBe(2); // the highest VersionNo of the line, as FindData picks
        row.Data.Json.ShouldContain("1.1.0#2");
        ctx.Entry(row).State.ShouldBe(EntityState.Detached);
        recorder.Commands.Count.ShouldBe(2); // version strings, then the one row
        recorder.BodyReads.Count().ShouldBe(1);

        (await repository.FindDataByVersionAsync(seeded.Id, "1.3.0", CancellationToken.None)).ShouldBeNull();
    }

    /// <summary>
    /// One instance with one open correlation and four data rows: 1.0.0, 1.1.0 (VersionNo 1 and 2) and
    /// 1.2.0 (latest).
    /// </summary>
    private async Task<Instance> SeedAsync(string key, string[]? versions = null)
    {
        var instance = Instance.Create(Guid.NewGuid(), "sys-flows", "1.0.0", key);
        instance.AddTags(["seeded"]);

        var rows = versions is null
            ? new List<(string Version, int No)> { ("1.0.0", 1), ("1.1.0", 1), ("1.1.0", 2), ("1.2.0", 1) }
            : versions.Select(v => (v, 1)).ToList();
        for (var i = 0; i < rows.Count; i++)
        {
            var (version, no) = rows[i];
            var body = $"{{\"v\":\"{version}#{no}\"}}";
            instance.AcceptPersistedData(
                new InstanceData(Guid.NewGuid(), instance.Id, version, new JsonData(body), i == rows.Count - 1)
                    { VersionNo = no });
        }

        instance.AddCorrelation(InstanceCorrelation.Create(
            id: Guid.NewGuid(),
            instanceId: instance.Id,
            parentState: "review",
            subFlowInstanceId: Guid.NewGuid(),
            subFlowType: "S",
            subFlowDomain: "sub-domain",
            subFlowName: "sub-flow",
            subFlowVersion: "1.0.0"));

        await using var ctx = CreateContext();
        ctx.Instances.Add(instance);
        await ctx.SaveChangesAsync();
        return instance;
    }

    private WorkflowDbContext CreateContext(CommandRecorder? recorder = null)
    {
        var builder = new DbContextOptionsBuilder<WorkflowDbContext>().UseNpgsql(_connectionString);
        if (recorder is not null)
        {
            builder.AddInterceptors(recorder);
        }

        return new WorkflowDbContext(builder.Options, new StaticCurrentSchema("public"));
    }

    private static EfCoreInstanceRepository CreateRepository(WorkflowDbContext context, bool latestOnly) => new(
        new FixedDbContextProvider(context),
        new ServiceCollection().BuildServiceProvider(),
        Substitute.For<IRuntimeInfoProvider>(),
        Substitute.For<IDataSinkManager>(),
        new StaticCurrentSchema("public"),
        Substitute.For<ISchemaValidator>(),
        Options.Create(new WorkflowExecutionOptions { LatestOnlyInstanceLoading = latestOnly }),
        NullLogger<EfCoreInstanceRepository>.Instance);

    /// <summary>Records the text of every command EF sends, in order.</summary>
    private sealed class CommandRecorder : DbCommandInterceptor
    {
        public List<string> Commands { get; } = new();

        /// <summary>Commands that select the <c>InstancesData."Data"</c> body column.</summary>
        public IEnumerable<string> BodyReads => Commands.Where(c => c.Contains(DataColumn));

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText.TrimStart());
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText.TrimStart());
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class FixedDbContextProvider(WorkflowDbContext context)
        : IAetherDbContextProvider<WorkflowDbContext>
    {
        public Task<WorkflowDbContext> GetDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(context);
    }
}
