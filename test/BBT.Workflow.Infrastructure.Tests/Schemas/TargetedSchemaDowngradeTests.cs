using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BBT.Aether.MultiSchema;
using BBT.Workflow.Data;
using BBT.Workflow.Schemas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace BBT.Workflow.Schemas.Tests;

/// <summary>
/// Runs the real migration chain against PostgreSQL through the production
/// <see cref="MultiSchemaMigrator{TContext}"/> (schema-aware SQL generator, per-schema history
/// table) and pins the targeted-migration contract behind the DbMigrator <c>downgrade</c> command:
/// plan → revert (Down, newest first) → converge back up, destructive-operation detection, the
/// refusal for schemas migrated by a newer build, and dry-run script generation.
/// The revert range deliberately crosses <c>MoveInstanceIncidentsToTable</c>, a migration whose
/// <c>Down()</c> drops a table — the exact case the data-loss gate exists for.
/// </summary>
public sealed class TargetedSchemaDowngradeTests : IAsyncLifetime
{
    /// <summary>A target several migrations below the assembly head, on the far side of the incidents table move.</summary>
    private const string Target = "20260901065706_AddSubflowSettlementMarker";
    private const string Schema = "downgrade_lab";

    private PostgreSqlContainer _postgres = null!;
    private MultiSchemaMigrator<WorkflowDbContext> _migrator = null!;

    async Task IAsyncLifetime.InitializeAsync()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("testdb").WithUsername("test").WithPassword("test")
            .Build();
        await _postgres.StartAsync();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = _postgres.GetConnectionString()
            })
            .Build();

        _migrator = new MultiSchemaMigrator<WorkflowDbContext>(
            configuration,
            new DefaultSchemaNameFormatter(),
            NullLogger<MultiSchemaMigrator<WorkflowDbContext>>.Instance,
            new StaticCurrentSchema(Schema),
            Options.Create(new SchemaMigrationOptions()));
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _postgres.StopAsync();
        await _postgres.DisposeAsync();
    }

    [Fact]
    public async Task Downgrade_RevertsToTarget_DetectsDataLoss_AndConvergesBackUp()
    {
        // Forward to the assembly head, exactly like the DbMigrator's default run.
        await _migrator.MigrateSchemaAsync(Schema);

        var initial = await _migrator.GetStatusAsync(Schema);
        initial.PendingMigrations.ShouldBeEmpty();
        initial.UnknownAppliedMigrations.ShouldBeEmpty();
        initial.AppliedHead.ShouldNotBeNull();
        string.CompareOrdinal(initial.AppliedHead, Target).ShouldBeGreaterThan(0);

        // Plan: reverts are newest-first, stop at the target, and the range crosses the incidents
        // table move — the plan must surface that Down() as destructive.
        var plan = await _migrator.PlanMigrationToTargetAsync(Schema, Target);
        plan.IsNoOp.ShouldBeFalse();
        plan.TargetMigration.ShouldBe(Target);
        plan.MigrationsToApply.ShouldBeEmpty();
        plan.MigrationsToRevert.ShouldNotBeEmpty();
        plan.MigrationsToRevert.ShouldBe(
            plan.MigrationsToRevert.OrderByDescending(id => id, StringComparer.Ordinal).ToList());
        plan.MigrationsToRevert.ShouldNotContain(Target);
        plan.MigrationsToRevert.ShouldContain("20260906105544_MoveInstanceIncidentsToTable");
        plan.DestructiveOperations.ShouldContain(op =>
            op.StartsWith("20260906105544_MoveInstanceIncidentsToTable") && op.Contains("DROP TABLE"));

        // Model-breaking detection: this build's runtime maps InstanceIncidents (the table the revert
        // drops) and Instances."Type" (added by a migration in the revert range) — running the SAME
        // runtime version against the downgraded schema would fail at first touch, so the plan must
        // say so; the runner refuses without the explicit --for-runtime-rollback intent.
        plan.ModelBreakingOperations.ShouldContain(op =>
            op.StartsWith("20260906105544_MoveInstanceIncidentsToTable") && op.Contains("InstanceIncidents"));
        plan.ModelBreakingOperations.ShouldContain(op =>
            op.StartsWith("20260916140000_AddInstanceType") && op.Contains("\"Instances\".\"Type\""));

        // An index-only revert range is model-neutral: nothing in the compiled model references an
        // index, so a same-version downgrade of these needs no rollback intent (and loses no data).
        var indexOnlyPlan = await _migrator.PlanMigrationToTargetAsync(
            Schema, "20260917150000_AddHumanTaskOwnColumnsIndex");
        indexOnlyPlan.MigrationsToRevert.ShouldNotBeEmpty();
        indexOnlyPlan.ModelBreakingOperations.ShouldBeEmpty();
        indexOnlyPlan.DestructiveOperations.ShouldBeEmpty();

        // The dry-run script is the same converge, as reviewable SQL: it must drop the incidents
        // table in the lab schema and maintain the per-schema history table.
        var script = await _migrator.GenerateScriptToTargetAsync(Schema, Target);
        script.ShouldContain("DROP TABLE");
        script.ShouldContain("\"InstanceIncidents\"");
        script.ShouldContain(Schema);                    // schema-qualified like the direct apply
        script.ShouldContain("__Workflow_Migrations");   // history bookkeeping travels with the DDL

        // Apply the downgrade: history head lands exactly on the target and the dropped table is gone.
        await _migrator.MigrateSchemaToTargetAsync(Schema, Target);

        var downgraded = await _migrator.GetStatusAsync(Schema);
        downgraded.AppliedHead.ShouldBe(Target);
        downgraded.PendingMigrations.ShouldBe(plan.MigrationsToRevert.OrderBy(id => id, StringComparer.Ordinal).ToList());
        (await TableExistsAsync("InstanceIncidents")).ShouldBeFalse();

        // Converge back up (what the next forward deploy's migrator run does) — table returns.
        await _migrator.MigrateSchemaAsync(Schema);
        var restored = await _migrator.GetStatusAsync(Schema);
        restored.AppliedHead.ShouldBe(initial.AppliedHead);
        restored.PendingMigrations.ShouldBeEmpty();
        (await TableExistsAsync("InstanceIncidents")).ShouldBeTrue();

        // A no-op plan when already at the target — and the "0" full-wipe sentinel stays refused.
        await _migrator.MigrateSchemaToTargetAsync(Schema, initial.AppliedHead!);
        (await _migrator.PlanMigrationToTargetAsync(Schema, initial.AppliedHead!)).IsNoOp.ShouldBeTrue();
        await Should.ThrowAsync<InvalidOperationException>(
            () => _migrator.PlanMigrationToTargetAsync(Schema, "0"));
        await Should.ThrowAsync<InvalidOperationException>(
            () => _migrator.PlanMigrationToTargetAsync(Schema, "not-a-real-migration"));
    }

    [Fact]
    public async Task SchemaMigratedByANewerBuild_IsReportedUnknown_AndThePlanRefusesIt()
    {
        await _migrator.MigrateSchemaAsync(Schema);

        // Simulate a schema touched by a NEWER runtime: a history row this assembly does not ship.
        const string futureMigration = "20991231235959_FromANewerBuild";
        await ExecuteAsync(
            $"INSERT INTO {Schema}.\"__Workflow_Migrations\" (\"MigrationId\", \"ProductVersion\") " +
            $"VALUES ('{futureMigration}', 'test')");

        var status = await _migrator.GetStatusAsync(Schema);
        status.UnknownAppliedMigrations.ShouldBe(new[] { futureMigration });

        // The plan carries the refusal signal the downgrade runner gates the whole run on: this
        // binary cannot revert a migration whose Down() code it does not contain.
        var plan = await _migrator.PlanMigrationToTargetAsync(Schema, Target);
        plan.UnknownAppliedMigrations.ShouldBe(new[] { futureMigration });

        await ExecuteAsync(
            $"DELETE FROM {Schema}.\"__Workflow_Migrations\" WHERE \"MigrationId\" = '{futureMigration}'");
    }

    /// <summary>
    /// The messaging chain (fixed sys_queues schema, its own migration ids) goes through the same
    /// primitives the downgrade runner uses for <c>--messaging-target</c>: plan (destructive +
    /// model-breaking detection against the MessagingDbContext model), converge down, converge up.
    /// </summary>
    [Fact]
    public async Task MessagingChain_PlansAndConvergesToTarget_BothDirections()
    {
        const string messagingTarget = "20260627085107_BackgroundJob_ArmingToken";
        const string distributedLocksMigration = "20260711180608_AddDistributedLocksToMessagingContext";

        await using (var ctx = CreateMessagingContext())
        {
            await ctx.Database.MigrateAsync();

            var plan = await MigrationChainInspector.PlanAsync(ctx, "sys_queues", messagingTarget, default);
            plan.MigrationsToRevert.ShouldContain(distributedLocksMigration);
            plan.DestructiveOperations.ShouldContain(op => op.Contains("DistributedLocks"));
            plan.ModelBreakingOperations.ShouldContain(op => op.Contains("DistributedLocks"));

            await ctx.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>()
                .MigrateAsync(messagingTarget);
            var (applied, pending, unknown) = await MigrationChainInspector.GetChainStateAsync(ctx, default);
            applied[^1].ShouldBe(messagingTarget);
            pending.ShouldContain(distributedLocksMigration);
            unknown.ShouldBeEmpty();

            await ctx.Database.MigrateAsync();
            (applied, pending, _) = await MigrationChainInspector.GetChainStateAsync(ctx, default);
            applied[^1].ShouldBe(distributedLocksMigration);
            pending.ShouldBeEmpty();
        }
    }

    private MessagingDbContext CreateMessagingContext()
    {
        var options = new DbContextOptionsBuilder<MessagingDbContext>()
            .UseNpgsql(_postgres.GetConnectionString(),
                npgsql => npgsql.MigrationsHistoryTable("__Workflow_Migrations", "sys_queues"))
            .Options;
        return new MessagingDbContext(options);
    }

    private async Task<bool> TableExistsAsync(string table)
    {
        await using var ctx = CreateProbeContext();
        await using var command = ctx.Database.GetDbConnection().CreateCommand();
        command.CommandText =
            $"SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = '{Schema}' AND table_name = '{table}')";
        if (command.Connection!.State != System.Data.ConnectionState.Open)
            await command.Connection.OpenAsync();
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var ctx = CreateProbeContext();
        await ctx.Database.ExecuteSqlRawAsync(sql);
    }

    private WorkflowDbContext CreateProbeContext()
    {
        var options = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            // Per-schema model cache: without it this class's non-"public" schema would pin the
            // type-keyed compiled model for every other WorkflowDbContext test in the process.
            .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, SchemaAwareModelCacheKeyFactory>()
            .Options;
        return new WorkflowDbContext(options, new StaticCurrentSchema(Schema));
    }
}
