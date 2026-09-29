using BBT.Workflow.Data;
using BBT.Workflow.Runtime;
using BBT.Workflow.Schemas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.DbMigrator;

/// <summary>
/// Runs the DbMigrator's <c>downgrade</c> and <c>status</c> commands. Downgrade converges every
/// targeted schema to an explicit migration (EF reverts above the target, applies below it) in
/// three stages: plan everything first, gate on the plans (a schema ahead of this binary refuses
/// the whole run; destructive reverts require the explicit data-loss acknowledgment), then either
/// write per-schema SQL scripts (<c>--script</c>, dry run) or apply in parallel under the same
/// per-schema distributed lock as the forward path. The workflow chain and the messaging chain
/// (<c>sys_queues</c>) are separate migration histories with separate targets.
/// </summary>
public sealed class SchemaDowngradeRunner(
    IServiceScopeFactory scopeFactory,
    ILogger<SchemaDowngradeRunner> logger)
{
    private const string MessagingChainName = "sys_queues (messaging)";

    /// <summary>Whether the last run completed with every schema handled successfully.</summary>
    public bool Success { get; private set; }

    /// <summary>Read-only status report: applied head / pending / unknown per schema, both chains.</summary>
    public async Task RunStatusAsync(MigratorCommand command, CancellationToken cancellationToken = default)
    {
        Success = false;

        var schemas = await ResolveWorkflowSchemasAsync(command, cancellationToken);
        if (schemas is null)
            return;

        foreach (var schema in schemas)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var targeted = scope.ServiceProvider.GetRequiredService<ITargetedSchemaMigrator>();
            var status = await targeted.GetStatusAsync(schema, cancellationToken);
            LogStatus(status);
        }

        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var (applied, pending, unknown) = await MigrationChainInspector.GetChainStateAsync(
                CreateMessagingContext(scope.ServiceProvider), cancellationToken);
            LogStatus(new SchemaMigrationStatus(
                MessagingChainName, applied.Count > 0 ? applied[^1] : null, applied.Count, pending, unknown));
        }

        Success = true;
    }

    /// <summary>Converges schemas to the command's target(s); see the class remarks for the stages.</summary>
    public async Task RunDowngradeAsync(MigratorCommand command, CancellationToken cancellationToken = default)
    {
        Success = false;

        // Stage 1 — plan every schema before touching anything, so a typo'd target, a schema that is
        // ahead of this binary, or an unacknowledged destructive revert refuses the WHOLE run instead
        // of stopping halfway with half the fleet converged.
        List<SchemaMigrationPlan> workflowPlans = [];
        if (command.Target is not null)
        {
            var schemas = await ResolveWorkflowSchemasAsync(command, cancellationToken);
            if (schemas is null)
                return;

            foreach (var schema in schemas)
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var targeted = scope.ServiceProvider.GetRequiredService<ITargetedSchemaMigrator>();
                workflowPlans.Add(await targeted.PlanMigrationToTargetAsync(schema, command.Target, cancellationToken));
            }
        }

        SchemaMigrationPlan? messagingPlan = null;
        if (command.MessagingTarget is not null)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            messagingPlan = await MigrationChainInspector.PlanAsync(
                CreateMessagingContext(scope.ServiceProvider),
                MessagingChainName, command.MessagingTarget, cancellationToken);
        }

        List<SchemaMigrationPlan> allPlans = messagingPlan is null
            ? workflowPlans
            : [.. workflowPlans, messagingPlan];
        foreach (var plan in allPlans)
            LogPlan(plan);

        // Gate 1 — a schema whose history contains migrations this assembly does not ship can only
        // be reverted by the newer build that shipped them.
        var ahead = allPlans.Where(p => p.UnknownAppliedMigrations.Count > 0).ToList();
        if (ahead.Count > 0)
        {
            foreach (var plan in ahead)
            {
                logger.LogError(
                    "Schema {Schema} has applied migrations this build does not contain: {Unknown}. " +
                    "Run the downgrade from the newer runtime image (the Down() code only exists there)",
                    plan.Schema, string.Join(", ", plan.UnknownAppliedMigrations));
            }
            return;
        }

        var destructive = allPlans.SelectMany(p => p.DestructiveOperations).ToList();
        var modelBreaking = allPlans.SelectMany(p => p.ModelBreakingOperations).ToList();

        // The dry run writes reviewable SQL and applies nothing, so the acknowledgment gates do not
        // block it — the findings are surfaced as warnings inside the review loop instead.
        if (command.ScriptDirectory is not null)
        {
            if (modelBreaking.Count > 0)
                logger.LogWarning(
                    "Applying this downgrade while KEEPING the current runtime version would break it — " +
                    "{Count} operation(s) remove or reshape objects this build's runtime model maps: {Operations}",
                    modelBreaking.Count, string.Join("; ", modelBreaking));
            if (destructive.Count > 0)
                logger.LogWarning(
                    "Applying this downgrade destroys data permanently — {Count} destructive operation(s): {Operations}",
                    destructive.Count, string.Join("; ", destructive));

            await WriteScriptsAsync(command, workflowPlans, messagingPlan, cancellationToken);
            return;
        }

        // Gate 2 — this build's runtime model still needs what the revert removes. Refusing here is
        // what keeps a same-version downgrade from taking the domain down at first touch
        // (42703/42P01 on every read/write of the affected entity). The explicit intent flag exists
        // for the one legitimate case: the runtime is being rolled back to an older version right
        // after this command, so the current model's needs no longer apply.
        if (modelBreaking.Count > 0 && !command.ForRuntimeRollback)
        {
            logger.LogError(
                "The requested downgrade was refused: {Count} operation(s) remove or reshape tables/columns " +
                "that THIS build's runtime model maps, so a runtime staying on the current version would fail " +
                "on every read/write of those entities. If (and only if) you are deploying an OLDER runtime " +
                "version right after this command, re-run with --for-runtime-rollback. Otherwise revert the " +
                "change with a new forward migration instead. Offending operations: {Operations}",
                modelBreaking.Count, string.Join("; ", modelBreaking));
            return;
        }

        // Gate 3 — destructive reverts (table/column/schema drops) need the explicit acknowledgment.
        if (destructive.Count > 0 && !command.AcceptDataLoss)
        {
            logger.LogError(
                "The requested downgrade contains {Count} destructive operation(s) and was refused. " +
                "Re-run with --accept-data-loss to acknowledge the data those drops destroy is lost: {Operations}",
                destructive.Count, string.Join("; ", destructive));
            return;
        }

        var failed = await ApplyAsync(command, workflowPlans, messagingPlan, cancellationToken);
        if (failed > 0)
        {
            logger.LogError(
                "Downgrade finished with {FailedCount} failed schema(s). Exiting with failure.", failed);
            return;
        }

        Success = true;
        logger.LogInformation("Downgrade completed successfully for every targeted schema.");
    }

    private async Task<int> ApplyAsync(
        MigratorCommand command,
        List<SchemaMigrationPlan> workflowPlans,
        SchemaMigrationPlan? messagingPlan,
        CancellationToken cancellationToken)
    {
        var failedCount = 0;

        // Same parallelism shape as the forward runner; no-op schemas are skipped, everything else
        // converges under the per-schema "schema-migration:{schema}" lock.
        var schemasToApply = workflowPlans.Where(p => !p.IsNoOp).Select(p => p.Schema).ToList();
        if (schemasToApply.Count > 0)
        {
            var maxConcurrency = Math.Min(Math.Max(Environment.ProcessorCount, 1), schemasToApply.Count);
            using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
            await Task.WhenAll(schemasToApply.Select(async schema =>
            {
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var orchestrator = scope.ServiceProvider.GetRequiredService<ISchemaMigrationOrchestrator>();
                    await orchestrator.MigrateSchemaToTargetWithLockAsync(schema, command.Target!, cancellationToken);
                    logger.LogInformation("Downgrade completed for schema {Schema}", schema);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failedCount);
                    logger.LogError(
                        ex,
                        "Downgrade failed for schema {Schema}. Continuing with remaining schemas; the run will exit with failure",
                        schema);
                }
                finally
                {
                    semaphore.Release();
                }
            }));
        }

        if (messagingPlan is { IsNoOp: false })
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var ctx = CreateMessagingContext(scope.ServiceProvider);
                // The forward path (MigrateMessagingDbContext) also migrates this chain without our
                // per-schema lock; EF's own migration lock serializes concurrent migrators.
                await ctx.GetService<IMigrator>().MigrateAsync(messagingPlan.TargetMigration, cancellationToken);
                logger.LogInformation(
                    "Downgrade completed for the messaging chain at {TargetMigration}", messagingPlan.TargetMigration);
            }
            catch (Exception ex)
            {
                failedCount++;
                logger.LogError(ex, "Downgrade failed for the messaging chain");
            }
        }

        return failedCount;
    }

    private async Task WriteScriptsAsync(
        MigratorCommand command,
        List<SchemaMigrationPlan> workflowPlans,
        SchemaMigrationPlan? messagingPlan,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(command.ScriptDirectory!);

        foreach (var plan in workflowPlans.Where(p => !p.IsNoOp))
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var targeted = scope.ServiceProvider.GetRequiredService<ITargetedSchemaMigrator>();
            var sql = await targeted.GenerateScriptToTargetAsync(plan.Schema, command.Target!, cancellationToken);
            var path = Path.Combine(command.ScriptDirectory!, $"{plan.Schema}.sql");
            await File.WriteAllTextAsync(path, sql, cancellationToken);
            logger.LogInformation("Wrote downgrade script for schema {Schema}: {Path}", plan.Schema, path);
        }

        if (messagingPlan is { IsNoOp: false })
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var sql = await MigrationChainInspector.GenerateScriptAsync(
                CreateMessagingContext(scope.ServiceProvider), command.MessagingTarget!, cancellationToken);
            var path = Path.Combine(command.ScriptDirectory!, "sys_queues.messaging.sql");
            await File.WriteAllTextAsync(path, sql, cancellationToken);
            logger.LogInformation("Wrote downgrade script for the messaging chain: {Path}", path);
        }

        Success = true;
        logger.LogInformation(
            "Dry run: scripts written to {Directory}; no database changes were applied. " +
            "The supported rollback path is the direct apply (it keeps every schema's migration history consistent)",
            command.ScriptDirectory);
    }

    /// <summary>
    /// The schema set a command acts on: the explicit <c>--schema</c> list, or every system schema
    /// plus every domain schema discovered from sys_flows. Unlike the forward path, a discovery
    /// failure here is a hard failure — a rollback that silently misses schemas is worse than one
    /// that refuses to start.
    /// </summary>
    private async Task<List<string>?> ResolveWorkflowSchemasAsync(
        MigratorCommand command, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();

        if (!scope.ServiceProvider.GetRequiredService<IOptions<RuntimeOptions>>().Value.EnableSchemaMigration)
        {
            logger.LogError("Schema migration is disabled (Runtime:EnableSchemaMigration=false); refusing to run");
            return null;
        }

        if (command.Schemas.Count > 0)
            return command.Schemas.ToList();

        var (domainSchemas, discoveryError) = await SchemaDiscovery.TryDiscoverDomainSchemasAsync(
            scope.ServiceProvider, cancellationToken);
        if (domainSchemas is null)
        {
            logger.LogError(
                discoveryError,
                "Failed to enumerate domain schemas from sys_flows; refusing to run against an unknown schema set. " +
                "Name the schemas explicitly with --schema to override");
            return null;
        }

        return [.. SchemaDiscovery.GetSystemSchemas(scope.ServiceProvider), .. domainSchemas];
    }

    /// <summary>
    /// The DI-registered messaging context is migration-capable (history table
    /// <c>sys_queues.__Workflow_Migrations</c>) — the same instance the forward path migrates; only
    /// the command timeout is widened the same way <c>MigrateMessagingDbContext</c> does.
    /// </summary>
    private static MessagingDbContext CreateMessagingContext(IServiceProvider scopedServices)
    {
        var ctx = scopedServices.GetRequiredService<MessagingDbContext>();
        var migrationOptions = scopedServices.GetService<IOptions<SchemaMigrationOptions>>()?.Value
                               ?? new SchemaMigrationOptions();
        ctx.Database.SetCommandTimeout(migrationOptions.CommandTimeoutSeconds);
        return ctx;
    }

    private void LogStatus(SchemaMigrationStatus status)
    {
        logger.LogInformation(
            "Schema {Schema}: head={AppliedHead} applied={AppliedCount} pending={PendingCount} unknownToThisBuild={UnknownCount}{UnknownList}",
            status.Schema,
            status.AppliedHead ?? "(none)",
            status.AppliedCount,
            status.PendingMigrations.Count,
            status.UnknownAppliedMigrations.Count,
            status.UnknownAppliedMigrations.Count > 0
                ? $" [{string.Join(", ", status.UnknownAppliedMigrations)}]"
                : string.Empty);
    }

    private void LogPlan(SchemaMigrationPlan plan)
    {
        if (plan.IsNoOp)
        {
            logger.LogInformation(
                "Schema {Schema}: already at {TargetMigration}; nothing to do", plan.Schema, plan.TargetMigration);
            return;
        }

        logger.LogInformation(
            "Schema {Schema}: head={AppliedHead} target={TargetMigration} revert={RevertCount} [{RevertList}] apply={ApplyCount} [{ApplyList}] destructive={DestructiveCount} modelBreaking={ModelBreakingCount}",
            plan.Schema,
            plan.AppliedHead ?? "(none)",
            plan.TargetMigration,
            plan.MigrationsToRevert.Count,
            string.Join(", ", plan.MigrationsToRevert),
            plan.MigrationsToApply.Count,
            string.Join(", ", plan.MigrationsToApply),
            plan.DestructiveOperations.Count,
            plan.ModelBreakingOperations.Count);
    }
}
