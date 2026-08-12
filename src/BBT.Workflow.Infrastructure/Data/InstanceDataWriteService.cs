using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Workflow.Aspects;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Caching;
using BBT.Workflow.DefinitionContext;
using BBT.Workflow.Definitions;
using BBT.Workflow.ExceptionHandling;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BBT.Workflow.Data;

/// <summary>
/// The single InstanceData writer (architecture decision: immediate per-record persistence, no
/// aggregate-side data mutation). Serializes concurrent writers with the per-instance PostgreSQL
/// <c>FOR UPDATE</c> row lock and computes the ROW'S WHOLE IDENTITY under that lock from the
/// authoritative head: <c>VersionNo</c> = head + 1, <c>Version</c> = head version + strategy, and
/// the no-change dedup from the merged content's hash. The row is inserted directly (never
/// through the aggregate navigation) and the caller's aggregate — live or snapshot — has its
/// in-memory latest refreshed via <c>Instance.AcceptPersistedData</c>.
/// <para>
/// A brand-new instance has no row to lock yet — harmless: it cannot have a concurrent writer,
/// the head read returns empty and numbering starts at 1. The partial unique indexes on
/// <c>InstancesData</c> remain the database-level backstop, and the SaveChanges guard in
/// <see cref="WorkflowDbContext"/> rejects any stray writer that bypasses this service.
/// </para>
/// </summary>
public sealed class InstanceDataWriteService(
    IAetherDbContextProvider<WorkflowDbContext> dbContextProvider,
    IWorkflowContext workflowContext,
    IServiceProvider serviceProvider,
    IJsonSchemaValidator jsonSchemaValidator,
    IOptions<WorkflowExecutionOptions> executionOptions,
    ILogger<InstanceDataWriteService> logger) : IInstanceDataWriteService
{
    /// <inheritdoc />
    public async Task<InstanceData?> AppendAsync(
        Instance instance,
        JsonData delta,
        VersionStrategy? versionStrategy,
        CancellationToken cancellationToken = default)
    {
        var context = await dbContextProvider.GetDbContextAsync();

        return await RunLockedAsync(context, instance.Id, cancellationToken, async () =>
        {
            var head = await ReadHeadAsync(context, instance.Id, cancellationToken);
            var plan = PlanAppend(head, delta, versionStrategy);

            if (plan.IsDuplicate)
            {
                return null;
            }

            await ValidateAgainstSchemaAsync(plan.Content, cancellationToken);

            // A strategy append always sits at or above the head → it takes the latest flag.
            var row = new InstanceData(Guid.NewGuid(), instance.Id, plan.Version, plan.Content, isLatest: true)
            {
                VersionNo = plan.VersionNo
            };

            await PersistAsync(context, instance, row, demoteStaleLatest: head is not null, cancellationToken);
            return row;
        });
    }

    /// <inheritdoc />
    public async Task<InstanceData> AppendExplicitAsync(
        Instance instance,
        Guid id,
        string version,
        JsonData data,
        CancellationToken cancellationToken = default)
    {
        var context = await dbContextProvider.GetDbContextAsync();

        var result = await RunLockedAsync<InstanceData>(context, instance.Id, cancellationToken, async () =>
        {
            // Publish-path dedup: the same explicit version is written once, ever.
            var existing = await context.InstancesData
                .Where(d => d.InstanceId == instance.Id && d.Version == version)
                .OrderByDescending(d => d.VersionNo)
                .FirstOrDefaultAsync(cancellationToken);
            if (existing is not null)
            {
                instance.AcceptPersistedData(existing);
                return existing;
            }

            var head = await ReadHeadAsync(context, instance.Id, cancellationToken);

            // An explicit (possibly older-line) version only takes the latest flag when it
            // compares at or above the head — an older line never steals the global latest.
            var takesLatest = head is null
                || InstanceDataVersionComparer.CompareVersionStrings(version, head.Version) >= 0;

            await ValidateAgainstSchemaAsync(data, cancellationToken);

            var row = new InstanceData(id, instance.Id, version, data, takesLatest)
            {
                VersionNo = (head?.VersionNo ?? 0L) + 1
            };

            await PersistAsync(context, instance, row, demoteStaleLatest: takesLatest && head is not null, cancellationToken);
            return row;
        });

        return result!;
    }

    /// <summary>
    /// Computes a strategy append's whole identity from the authoritative head: the full merged
    /// content, the no-change dedup verdict (hash of the MERGED result against the head's hash —
    /// a delta-only duplicate never matches raw), and the version pair. Pure so the contract is
    /// unit-testable; <see cref="AppendAsync"/> calls it under the row lock.
    /// </summary>
    internal static AppendPlan PlanAppend(
        InstanceDataHeadRow? head,
        JsonData delta,
        VersionStrategy? versionStrategy)
    {
        if (head is null)
        {
            return new AppendPlan(delta, WorkflowConstants.DefaultVersion, 1L, IsDuplicate: false);
        }

        var content = new JsonData(head.Data).Merge(delta);

        // No-change dedup on the MERGED result — an idempotent duplicate (e.g. a repeated
        // callback stamping a key that is already set) writes nothing.
        var isDuplicate = string.Equals(
            InstanceData.ComputeDataHash(content), head.DataHash, StringComparison.OrdinalIgnoreCase);

        var version = InstanceData.IncrementVersion(head.Version, versionStrategy ?? VersionStrategy.None);
        return new AppendPlan(content, version, head.VersionNo + 1, isDuplicate);
    }

    /// <summary>
    /// Runs <paramref name="body"/> inside the row-lock scope: within the ambient transaction
    /// when one is open, otherwise inside a local transaction committed on success. SET LOCAL
    /// timeouts and the Postgres error mapping (lock wait → 409, statement timeout → 503) wrap
    /// the whole scope.
    /// </summary>
    private async Task<T?> RunLockedAsync<T>(
        WorkflowDbContext context,
        Guid instanceId,
        CancellationToken cancellationToken,
        Func<Task<T?>> body) where T : class
    {
        var options = executionOptions.Value.InstanceDataWrite;
        var schema = SanitizeIdentifier(context.CurrentSchemaName ?? "public");

        var ownsTransaction = context.Database.CurrentTransaction is null;
        var transaction = ownsTransaction
            ? await context.Database.BeginTransactionAsync(cancellationToken)
            : null;

        await using (transaction)
        {
            try
            {
                // Transaction-scoped timeouts (SET LOCAL — PgBouncer transaction-mode safe).
                await context.Database.ExecuteSqlRawAsync(
                    $"SET LOCAL lock_timeout = '{options.LockTimeoutMs}ms'; " +
                    $"SET LOCAL statement_timeout = '{options.StatementTimeoutMs}ms';",
                    cancellationToken);

                // Lock the parent Instances row — every InstanceData writer for this instance
                // serializes here until the enclosing transaction commits. A brand-new instance
                // matches no row (no competitor can see it yet).
                await context.Database.ExecuteSqlRawAsync(
                    $"SELECT 1 FROM \"{schema}\".\"Instances\" WHERE \"Id\" = {{0}} FOR UPDATE",
                    [instanceId],
                    cancellationToken);

                var result = await body();

                if (transaction is not null)
                    await transaction.CommitAsync(cancellationToken);

                return result;
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
            {
                logger.InstanceDataLockWaitTimeout(instanceId, options.LockTimeoutMs);
                throw new InstanceDataLockTimeoutException(instanceId);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.QueryCanceled)
            {
                logger.InstanceDataWriteStatementTimeout(instanceId, options.StatementTimeoutMs);
                throw new InstanceDataWriteTimeoutException(instanceId);
            }
        }
    }

    /// <summary>
    /// Reads the authoritative head under the lock — version identity plus the content and its
    /// hash, which the merge and the no-change dedup both need.
    /// </summary>
    private async Task<InstanceDataHeadRow?> ReadHeadAsync(
        WorkflowDbContext context,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        var schema = SanitizeIdentifier(context.CurrentSchemaName ?? "public");
        return await context.Database.SqlQueryRaw<InstanceDataHeadRow>(
                $"SELECT \"VersionNo\", \"Version\", \"DataHash\", \"Data\"::text AS \"Data\" " +
                $"FROM \"{schema}\".\"InstancesData\" WHERE \"InstanceId\" = {{0}} AND \"IsLatest\"",
                instanceId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Demotes the stale latest row when needed, inserts the new row DIRECTLY (never via the
    /// aggregate navigation), saves, and refreshes the caller's aggregate in-memory state.
    /// </summary>
    private async Task PersistAsync(
        WorkflowDbContext context,
        Instance instance,
        InstanceData row,
        bool demoteStaleLatest,
        CancellationToken cancellationToken)
    {
        var schema = SanitizeIdentifier(context.CurrentSchemaName ?? "public");

        if (demoteStaleLatest)
        {
            var demoted = await context.Database.ExecuteSqlRawAsync(
                $"UPDATE \"{schema}\".\"InstancesData\" SET \"IsLatest\" = FALSE " +
                $"WHERE \"InstanceId\" = {{0}} AND \"IsLatest\"",
                [row.InstanceId],
                cancellationToken);

            if (demoted > 0)
                logger.InstanceDataStaleLatestDemoted(row.InstanceId, row.VersionNo);
        }

        context.InstancesData.Add(row);
        await context.SaveChangesAsync(cancellationToken);

        // Refresh the caller's aggregate. When the aggregate is tracked in THIS context, EF
        // relationship fixup already attached the row — AcceptPersistedData is Id-idempotent.
        instance.AcceptPersistedData(row);
    }

    /// <summary>
    /// Validates the content against the current workflow's master schema when one is
    /// configured — the same contract the old aggregate mutation methods enforced. No workflow
    /// in context (publish, system paths) → skip.
    /// </summary>
    private async Task ValidateAgainstSchemaAsync(JsonData content, CancellationToken cancellationToken)
    {
        var workflow = workflowContext.Workflow;
        if (workflow?.Schema is null)
            return;

        // Resolved lazily: IComponentCacheStore lives in the Application module, which non-HTTP
        // hosts (workers, DbMigrator) do not load — and in those hosts the workflow context is
        // always empty, so this line is never reached. The null-check is a belt-and-braces skip.
        var componentCacheStore = serviceProvider.GetService<IComponentCacheStore>();
        if (componentCacheStore is null)
        {
            logger.InstanceDataSchemaLoadFailed(workflow.Schema.Key, "IComponentCacheStore is not registered in this host");
            return;
        }

        var schemaResult = await componentCacheStore.GetSchemaAsync(workflow.Schema, cancellationToken);
        if (!schemaResult.IsSuccess)
        {
            logger.InstanceDataSchemaLoadFailed(workflow.Schema.Key, schemaResult.Error.Message);
            return;
        }

        var validationResult = jsonSchemaValidator.Validate(schemaResult.Value!.Schema, content.JsonElement);
        if (!validationResult.IsSuccess)
        {
            throw new SchemaValidationException(
                validationResult.Error.Message ?? "Schema Validation Error",
                validationResult.Error.ValidationErrors?.ToList().AsReadOnly());
        }
    }

    private static string SanitizeIdentifier(string identifier)
        => identifier.Replace("\"", "", StringComparison.Ordinal);
}

/// <summary>
/// Projection of the current latest InstanceData row, read under the FOR UPDATE lock.
/// Property names match the quoted column names/aliases in the raw query.
/// </summary>
internal sealed class InstanceDataHeadRow
{
    public long VersionNo { get; set; }
    public string Version { get; set; } = string.Empty;
    public string DataHash { get; set; } = string.Empty;
    public string Data { get; set; } = string.Empty;
}

/// <summary>
/// The identity of a strategy append computed from the authoritative head:
/// merged content, dedup verdict, and the version pair the new row will carry.
/// </summary>
internal readonly record struct AppendPlan(
    JsonData Content,
    string Version,
    long VersionNo,
    bool IsDuplicate);
