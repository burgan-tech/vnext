using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Clock;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.Guids;
using BBT.Aether.MultiSchema;
using BBT.Aether.Uow;
using BBT.Aether.Uow.EntityFrameworkCore;
using BBT.Aether.Users;
using BBT.Workflow.Instances;
using BBT.Workflow.Schemas;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;
using OpenTelemetry.Trace;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace BBT.Workflow.Data;

/// <summary>
/// Pins the two forms of the <see cref="WorkflowDbContext"/> model against a real PostgreSQL, wired
/// exactly as the hosts wire it (<c>WorkflowApiBaseServiceCollectionExtensions.AddDbContext</c>):
/// <list type="bullet">
///   <item>unit-of-work contexts share ONE placeholder model, whatever their schema, and still read and
///   write only their own schema (Aether's interceptor qualifies every command);</item>
///   <item><see cref="MultiSchemaMigrator{TContext}"/> and design-time contexts keep a model bound to
///   their schema, so runtime migrations create each flow's tables in that flow's schema.</item>
/// </list>
/// </summary>
public sealed class WorkflowDbContextSchemaModelTests : IAsyncLifetime
{
    private const string SchemaA = "model_flow_a";
    private const string SchemaB = "model_flow_b";

    private PostgreSqlContainer _postgres = null!;
    private ServiceProvider _services = null!;
    private IConfiguration _configuration = null!;

    async Task IAsyncLifetime.InitializeAsync()
    {
        _postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("testdb").WithUsername("test").WithPassword("test")
            .Build();
        await _postgres.StartAsync();

        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = _postgres.GetConnectionString(),
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton(_configuration);
        services.AddLogging();
        services.AddSingleton(Substitute.For<ICurrentUser>());
        services.AddSingleton(Substitute.For<IGuidGenerator>());
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(_ => DateTime.UtcNow);
        services.AddSingleton(clock);
        services.AddSingleton<ISchemaNameFormatter, DefaultSchemaNameFormatter>();
        services.AddSingleton<ICurrentSchema, CurrentSchema>();
        services.AddSingleton(Substitute.For<BBT.Workflow.Runtime.IDomainDatabaseResolver>()); // default database
        services.AddDbContext(_configuration); // the hosts' registration, unchanged
        _services = services.BuildServiceProvider();

        // Runtime migration path: each schema migrated by the real migrator, as on flow publish.
        foreach (var schema in new[] { SchemaA, SchemaB })
        {
            await CreateMigrator().MigrateSchemaAsync(schema);
        }
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _services.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Migrator_CreatesTheTablesInEachFlowSchema()
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT table_schema FROM information_schema.tables
            WHERE table_name = 'Instances' ORDER BY table_schema
            """, connection);
        var schemas = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync()) schemas.Add(reader.GetString(0));
        }

        schemas.ShouldBe([SchemaA, SchemaB]);
    }

    [Fact]
    public async Task UnitOfWorkContexts_ShareOneModel_MappedToThePlaceholder()
    {
        object modelA = null!, modelB = null!;

        await InUnitOfWorkAsync(SchemaA, transactional: false, ctx =>
        {
            ctx.UsesSchemaPlaceholderModel.ShouldBeTrue();
            modelA = ctx.Model;
            ctx.Model.GetEntityTypes()
                .Where(e => e.GetTableName() is not null && e.ClrType.Namespace?.StartsWith("BBT.Workflow") == true)
                .Select(e => e.GetSchema())
                .Distinct()
                .ShouldBe([AetherSchemaModel.Placeholder]);
            return Task.CompletedTask;
        });
        await InUnitOfWorkAsync(SchemaB, transactional: false, ctx =>
        {
            modelB = ctx.Model;
            return Task.CompletedTask;
        });

        modelB.ShouldBeSameAs(modelA);
    }

    [Fact]
    public void ContextsWithoutTheAetherExtension_KeepAModelBoundToTheirSchema()
    {
        // Directly resolved contexts (DbMigrator, EnsureCreated) use the hosts' options, which carry the
        // schema-aware key factory but not Aether's placeholder extension.
        var options = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, SchemaAwareModelCacheKeyFactory>()
            .Options;
        using var contextA = new WorkflowDbContext(options, new StaticCurrentSchema(SchemaA));
        using var contextB = new WorkflowDbContext(options, new StaticCurrentSchema(SchemaB));
        using var design = new WorkflowDbContextDesignFactory().CreateDbContext([]);

        contextA.UsesSchemaPlaceholderModel.ShouldBeFalse();
        design.UsesSchemaPlaceholderModel.ShouldBeFalse();
        contextA.Model.FindEntityType(typeof(Instance))!.GetSchema().ShouldBe(SchemaA);
        contextB.Model.FindEntityType(typeof(Instance))!.GetSchema().ShouldBe(SchemaB);
    }

    [Fact]
    public async Task PlaceholderModelWithoutTheAetherInterceptor_FailsClosed()
    {
        // The placeholder model on options that lack Aether's QualifiedNamesCommandInterceptor (a
        // misconfigured or reordered registration). The command reaches Postgres with the bare placeholder
        // as its schema; no such schema exists, so it fails instead of reading any flow's rows. The runtime
        // never creates a schema with that name (DbMigrator and MultiSchemaMigrator use real schema names).
        var builder = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, SchemaAwareModelCacheKeyFactory>();
        builder.UseAetherQualifiedNamesModel();
        var options = builder.Options;
        await using var context = new WorkflowDbContext(options, new StaticCurrentSchema(SchemaA));

        context.UsesSchemaPlaceholderModel.ShouldBeTrue();
        var error = await Should.ThrowAsync<PostgresException>(() => context.Set<Instance>().CountAsync());
        // Postgres reports a qualified name in a missing schema as an undefined relation.
        error.SqlState.ShouldBe(PostgresErrorCodes.UndefinedTable);
        error.MessageText.ShouldContain(AetherSchemaModel.Placeholder);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnitOfWorkContexts_WriteAndReadOnlyTheirOwnSchema(bool transactional)
    {
        var key = "iso-" + Guid.NewGuid().ToString("N")[..8];
        var inA = Instance.Create(Guid.NewGuid(), "flow-a", "1.0.0", key);
        var inB = Instance.Create(Guid.NewGuid(), "flow-b", "1.0.0", key);

        // Both schemas in ONE unit of work: on the transactional root they share one connection.
        await InUnitOfWorkAsync([(SchemaA, inA), (SchemaB, inB)], transactional);

        await InUnitOfWorkAsync(SchemaA, transactional, async ctx =>
        {
            var ids = await ctx.Instances.AsNoTracking().Where(i => i.Key == key).Select(i => i.Id).ToListAsync();
            ids.ShouldBe([inA.Id]);
        });
        await InUnitOfWorkAsync(SchemaB, transactional, async ctx =>
        {
            var ids = await ctx.Instances.AsNoTracking().Where(i => i.Key == key).Select(i => i.Id).ToListAsync();
            ids.ShouldBe([inB.Id]);
        });

        (await CountInSchemaAsync(SchemaA, key)).ShouldBe(1);
        (await CountInSchemaAsync(SchemaB, key)).ShouldBe(1);
    }

    [Fact]
    public async Task OneCompiledQuery_ServesEverySchema()
    {
        // The same LINQ shape compiled once for the shared model must still be qualified per command.
        var key = "cq-" + Guid.NewGuid().ToString("N")[..8];
        var inA = Instance.Create(Guid.NewGuid(), "flow-a", "1.0.0", key);
        var inB = Instance.Create(Guid.NewGuid(), "flow-b", "1.0.0", key);
        await InUnitOfWorkAsync([(SchemaA, inA), (SchemaB, inB)], transactional: true);

        var compiled = EF.CompileAsyncQuery((WorkflowDbContext c, string k) =>
            c.Instances.AsNoTracking().Where(i => i.Key == k).Select(i => i.Id).FirstOrDefault());

        Guid fromA = default, fromB = default;
        await InUnitOfWorkAsync(SchemaA, false, async ctx => fromA = await compiled(ctx, key));
        await InUnitOfWorkAsync(SchemaB, false, async ctx => fromB = await compiled(ctx, key));

        fromA.ShouldBe(inA.Id);
        fromB.ShouldBe(inB.Id);
    }

    [Fact]
    public async Task RawSqlWithTheRealSchema_ComposesWithThePlaceholderModel()
    {
        // Repository raw SQL names the schema itself (from ICurrentSchema); EF composes model tables
        // (placeholder) around it. Both halves must land in the same schema.
        var key = "raw-" + Guid.NewGuid().ToString("N")[..8];
        var inA = Instance.Create(Guid.NewGuid(), "flow-a", "1.0.0", key);
        inA.AcceptPersistedData(new InstanceData(Guid.NewGuid(), inA.Id, "1.0.0", new JsonData("{\"x\":1}"), true) { VersionNo = 1 });
        await InUnitOfWorkAsync([(SchemaA, inA)], transactional: false);

        await InUnitOfWorkAsync(SchemaA, false, async ctx =>
        {
            var rows = await ctx.Instances
                .FromSqlRaw($"SELECT s.* FROM \"{SchemaA}\".\"Instances\" s")
                .AsNoTracking()
                .Where(i => i.Key == key)
                .Select(i => new { i.Id, Data = ctx.InstancesData.Count(d => d.InstanceId == i.Id) })
                .ToListAsync();
            rows.Count.ShouldBe(1);
            rows[0].Id.ShouldBe(inA.Id);
            rows[0].Data.ShouldBe(1);
        });
    }

    [Fact]
    public async Task CommandSpans_NameTheRealSchema_NotThePlaceholder()
    {
        // The real OpenTelemetry EF Core instrumentation records the query text before any interceptor
        // runs; the span must still name the schema the command ran against.
        var spans = new List<System.Diagnostics.Activity>();
        using (OpenTelemetry.Sdk.CreateTracerProviderBuilder()
                   .AddEntityFrameworkCoreInstrumentation()
                   .AddProcessor(new CaptureProcessor(spans))
                   .Build())
        {
            await InUnitOfWorkAsync(SchemaB, transactional: false,
                ctx => ctx.Instances.AsNoTracking().Where(i => i.Key == "span-probe").ToListAsync());
        }

        var texts = spans
            .Select(s => s.GetTagItem("db.query.text") as string ?? s.GetTagItem("db.statement") as string)
            .Where(t => t is not null && t.Contains("\"Instances\""))
            .ToList();
        texts.ShouldNotBeEmpty();
        texts.ShouldAllBe(t => t!.Contains($"\"{SchemaB}\".\"Instances\"") && !t.Contains(AetherSchemaModel.Placeholder));
    }

    private sealed class CaptureProcessor(List<System.Diagnostics.Activity> sink)
        : OpenTelemetry.BaseProcessor<System.Diagnostics.Activity>
    {
        public override void OnEnd(System.Diagnostics.Activity data)
        {
            lock (sink) sink.Add(data);
        }
    }

    private MultiSchemaMigrator<WorkflowDbContext> CreateMigrator() => new(
        _configuration,
        new DefaultSchemaNameFormatter(),
        NullLogger<MultiSchemaMigrator<WorkflowDbContext>>.Instance,
        new CurrentSchema(new DefaultSchemaNameFormatter()),
        Options.Create(new SchemaMigrationOptions()));

    private async Task InUnitOfWorkAsync(string schema, bool transactional, Func<WorkflowDbContext, Task> body)
    {
        await using var scope = _services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        await using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
            .Begin(new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = transactional });
        using (sp.GetRequiredService<ICurrentSchema>().Change(schema))
        {
            var ctx = await sp.GetRequiredService<IAetherDbContextProvider<WorkflowDbContext>>().GetDbContextAsync();
            await body(ctx);
        }

        await uow.CommitAsync();
    }

    private async Task InUnitOfWorkAsync((string Schema, Instance Instance)[] writes, bool transactional)
    {
        await using var scope = _services.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        await using var uow = sp.GetRequiredService<IUnitOfWorkManager>()
            .Begin(new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = transactional });
        foreach (var (schema, instance) in writes)
        {
            using (sp.GetRequiredService<ICurrentSchema>().Change(schema))
            {
                var ctx = await sp.GetRequiredService<IAetherDbContextProvider<WorkflowDbContext>>().GetDbContextAsync();
                ctx.Instances.Add(instance);
                await ctx.SaveChangesAsync();
            }
        }

        await uow.CommitAsync();
    }

    private async Task<long> CountInSchemaAsync(string schema, string key)
    {
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM \"{schema}\".\"Instances\" WHERE \"Key\" = @k", connection);
        command.Parameters.AddWithValue("k", key);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
