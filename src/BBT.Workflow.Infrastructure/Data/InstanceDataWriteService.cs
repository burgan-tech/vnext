using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Workflow.Aspects;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Caching;
using BBT.Workflow.DefinitionContext;
using BBT.Workflow.Definitions;
using BBT.Workflow.ExceptionHandling;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Shared.Merging;
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
/// authoritative head: <c>Version</c> = head version + strategy, <c>VersionNo</c> = the next
/// ordinal WITHIN that Version line (1-based; each new semantic version restarts at 1), and
/// the no-change dedup from the merged content's hash. The row is inserted directly (never
/// through the aggregate navigation) and the caller's aggregate — live or snapshot — has its
/// in-memory latest refreshed via <c>Instance.AcceptPersistedData</c>.
/// <para>
/// A brand-new instance has no row to lock yet — harmless: it cannot have a concurrent writer,
/// the head read returns empty and numbering starts at 1. The unique indexes on
/// <c>InstancesData</c> are the database-level backstop; writing instance data through anything
/// other than this service is a convention violation caught in review, not at runtime.
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
    /// <summary>
    /// Serializes appends that arrive on the SAME DbContext concurrently. Parallel task
    /// branches each get their own DI scope, but the ambient (AsyncLocal) UnitOfWork flows
    /// into every branch and hands them all the same schema-bound context — and a Npgsql
    /// connection cannot run two commands at once (NpgsqlOperationInProgressException).
    /// Writers on different contexts are untouched; those serialize on the FOR UPDATE
    /// row lock as designed.
    /// </summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WorkflowDbContext, SemaphoreSlim>
        ContextGates = new();

    // The striped per-instance gate (InstanceWriteGate) is taken BEFORE the context is even
    // resolved: GetDbContextAsync itself can touch the shared connection (context/schema
    // materialization in the ambient UnitOfWork), so two branches of the same instance must not
    // enter it concurrently either ("A second operation was started on this context instance").
    //
    // It lives in InstanceWriteGate rather than here because this service is NOT the only writer
    // of an instance's state: SubProcessTaskExecutor.CreateCorrelationAsync read-modify-writes the
    // same parent aggregate from a parallel fan-out branch, and a per-item correlation write and
    // this data append genuinely overlap. Both MUST take the same semaphore — a private gate array
    // here would serialize this service against itself and against nothing else, and the collision
    // would come straight back. Splitting it back into two arrays reintroduces the bug.
    /// <inheritdoc />
    public async Task<InstanceData?> AppendAsync(
        Instance instance,
        JsonData delta,
        VersionStrategy? versionStrategy,
        CancellationToken cancellationToken = default)
    {
        using var gate = await InstanceWriteGate.AcquireAsync(instance.Id, cancellationToken);
        return await AppendCoreAsync(instance, delta, versionStrategy, cancellationToken);
    }

    private async Task<InstanceData?> AppendCoreAsync(
        Instance instance,
        JsonData delta,
        VersionStrategy? versionStrategy,
        CancellationToken cancellationToken)
    {
        var context = await dbContextProvider.GetDbContextAsync();

        return await RunLockedAsync(context, instance.Id, cancellationToken, async () =>
        {
            var head = await ReadHeadAsync(context, instance.Id, cancellationToken);
            var plan = PlanAppend(
                head, delta, versionStrategy, executionOptions.Value.InstanceDataWrite.LegacyAppendPipeline);

            if (plan.IsDuplicate)
            {
                return null;
            }

            await ValidateAgainstSchemaAsync(plan.Content, cancellationToken);

            // A strategy append always sits at or above the head → it takes the latest flag.
            // VersionNo is line-scoped: the next ordinal WITHIN the target Version string.
            var row = new InstanceData(Guid.NewGuid(), instance.Id, plan.Version, plan.Content, isLatest: true)
            {
                VersionNo = await ReadLineMaxAsync(context, instance.Id, plan.Version, cancellationToken) + 1
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
        using var gate = await InstanceWriteGate.AcquireAsync(instance.Id, cancellationToken);
        return await AppendExplicitCoreAsync(instance, id, version, data, cancellationToken);
    }

    private async Task<InstanceData> AppendExplicitCoreAsync(
        Instance instance,
        Guid id,
        string version,
        JsonData data,
        CancellationToken cancellationToken)
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
                VersionNo = await ReadLineMaxAsync(context, instance.Id, version, cancellationToken) + 1
            };

            await PersistAsync(context, instance, row, demoteStaleLatest: takesLatest && head is not null, cancellationToken);
            return row;
        });

        return result!;
    }

    /// <summary>
    /// Computes a strategy append's identity from the authoritative head: the full merged
    /// content, the no-change dedup verdict (hash of the MERGED result against the head's hash —
    /// a delta-only duplicate never matches raw), and the semantic version. Pure so the contract
    /// is unit-testable; <see cref="AppendAsync"/> calls it under the row lock and then assigns
    /// the line-scoped VersionNo from the target version line's current maximum.
    /// <para>
    /// <paramref name="legacyPipeline"/> is the <c>InstanceDataWrite:LegacyAppendPipeline</c>
    /// kill-switch (B9): true routes to the original multi-pass <see cref="PlanAppendLegacy"/>;
    /// false (default) takes the single-pass <see cref="JsonCanonicalizer.MergeAndCanonicalize"/>
    /// path, which is byte-parity proven against it (see
    /// <c>JsonCanonicalizerParityTests</c> and <c>InstanceDataWriteServicePipelineTests</c>).
    /// </para>
    /// </summary>
    internal static AppendPlan PlanAppend(
        InstanceDataHeadRow? head,
        JsonData delta,
        VersionStrategy? versionStrategy,
        bool legacyPipeline)
    {
        if (legacyPipeline)
        {
            return PlanAppendLegacy(head, delta, versionStrategy);
        }

        if (head is null)
        {
            // Nothing to merge yet — this IS the legacy head-null result already (delta passed
            // through untouched). Deliberately NOT routed through JsonCanonicalizer: that path
            // replicates JsonData.Merge's Expando round-trip (camelCase + number reformat), which
            // the legacy head-null branch never invokes (it skips Merge entirely). Canonicalizing
            // against an empty base would therefore silently reformat the very first row of every
            // instance — a real byte-parity break, not a hypothetical one; pinned by
            // InstanceDataWriteServicePipelineTests.Append_NewPipeline_ProducesSameRow_AsLegacy_WhenHeadIsNull.
            return new AppendPlan(delta, WorkflowConstants.DefaultVersion, IsDuplicate: false);
        }

        var baseElement = new JsonData(head.Data).JsonElement;
        var result = JsonCanonicalizer.MergeAndCanonicalize(baseElement, delta.JsonElement);

        // No-change dedup on the MERGED result, same rule as legacy — the canonicalizer's hash
        // is byte-parity proven equal to ComputeDataHash(legacy merged content), so this compares
        // correctly even across a duplicate written by the OTHER pipeline.
        var isDuplicate = string.Equals(result.DataHash, head.DataHash, StringComparison.OrdinalIgnoreCase);

        var content = JsonData.FromNormalized(result.NormalizedJson);
        var version = InstanceData.IncrementVersion(head.Version, versionStrategy ?? VersionStrategy.None);
        return new AppendPlan(content, version, isDuplicate);
    }

    /// <summary>
    /// Original multi-pass append plan (<c>JsonData.Merge</c> → <c>NormalizedJson</c> →
    /// <c>ComputeDataHash</c>), preserved verbatim as the kill-switch fallback and as the
    /// byte-parity oracle's production twin.
    /// </summary>
    internal static AppendPlan PlanAppendLegacy(
        InstanceDataHeadRow? head,
        JsonData delta,
        VersionStrategy? versionStrategy)
    {
        if (head is null)
        {
            return new AppendPlan(delta, WorkflowConstants.DefaultVersion, IsDuplicate: false);
        }

        var content = new JsonData(head.Data).Merge(delta);

        // No-change dedup on the MERGED result — an idempotent duplicate (e.g. a repeated
        // callback stamping a key that is already set) writes nothing.
        var isDuplicate = string.Equals(
            InstanceData.ComputeDataHash(content), head.DataHash, StringComparison.OrdinalIgnoreCase);

        var version = InstanceData.IncrementVersion(head.Version, versionStrategy ?? VersionStrategy.None);
        return new AppendPlan(content, version, isDuplicate);
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
        var gate = ContextGates.GetValue(context, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await RunLockedCoreAsync(context, instanceId, cancellationToken, body);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<T?> RunLockedCoreAsync<T>(
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
    /// Reads the authoritative head under the lock — the semantic version plus the content and
    /// its hash, which the merge, the no-change dedup and the version increment all need.
    /// </summary>
    private async Task<InstanceDataHeadRow?> ReadHeadAsync(
        WorkflowDbContext context,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        var schema = SanitizeIdentifier(context.CurrentSchemaName ?? "public");
        return await context.Database.SqlQueryRaw<InstanceDataHeadRow>(
                $"SELECT \"Version\", \"DataHash\", \"Data\"::text AS \"Data\" " +
                $"FROM \"{schema}\".\"InstancesData\" WHERE \"InstanceId\" = {{0}} AND \"IsLatest\"",
                instanceId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Reads the target version line's current maximum VersionNo under the lock. VersionNo is
    /// line-scoped: an ordinal WITHIN one semantic Version string (1-based), not an
    /// instance-global sequence — each new Version line restarts at 1 and same-version appends
    /// (<c>VersionStrategy.None</c>) continue their own line.
    /// </summary>
    private async Task<long> ReadLineMaxAsync(
        WorkflowDbContext context,
        Guid instanceId,
        string version,
        CancellationToken cancellationToken)
    {
        var schema = SanitizeIdentifier(context.CurrentSchemaName ?? "public");
        return await context.Database.SqlQueryRaw<long>(
                $"SELECT COALESCE(MAX(\"VersionNo\"), 0) AS \"Value\" " +
                $"FROM \"{schema}\".\"InstancesData\" WHERE \"InstanceId\" = {{0}} AND \"Version\" = {{1}}",
                instanceId, version)
            .FirstAsync(cancellationToken);
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
    public string Version { get; set; } = string.Empty;
    public string DataHash { get; set; } = string.Empty;
    public string Data { get; set; } = string.Empty;
}

/// <summary>
/// The identity of a strategy append computed from the authoritative head: merged content,
/// dedup verdict, and the semantic version the new row will carry. The line-scoped VersionNo
/// is assigned separately, from the target version line's current maximum.
/// </summary>
internal readonly record struct AppendPlan(
    JsonData Content,
    string Version,
    bool IsDuplicate);
