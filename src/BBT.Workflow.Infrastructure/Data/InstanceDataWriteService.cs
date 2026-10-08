using System.Diagnostics;
using System.Text;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Workflow.Aspects;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Encryption;
using BBT.Workflow.ExceptionHandling;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Files;
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
    IServiceProvider serviceProvider,
    IJsonSchemaValidator jsonSchemaValidator,
    IOptions<WorkflowExecutionOptions> executionOptions,
    ILogger<InstanceDataWriteService> logger,
    InstanceDataProtector? protector = null,
    IOptions<BBT.Workflow.Authorization.SchemaEncryptionOptions>? encryptionOptions = null) : IInstanceDataWriteService
{
    /// <summary>x-encryption paths per resolved schema object (the component cache hands out the same instance while cached).</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<SchemaDefinition, ProtectedPaths>
        ProtectedPathMemo = new();

    private sealed record ProtectedPaths(IReadOnlySet<string> Encrypt, IReadOnlyDictionary<string, string> Hash)
    {
        public static readonly ProtectedPaths None = new(
            new HashSet<string>(StringComparer.Ordinal), new Dictionary<string, string>(StringComparer.Ordinal));

        public bool Any => Encrypt.Count > 0 || Hash.Count > 0;
    }

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
        CancellationToken cancellationToken = default,
        Definitions.Workflow? workflow = null)
    {
        // Before the gate: the master schema is loaded once (validation, x-encryption and x-storage all read it), and
        // the x-storage offload runs here — no binding I/O under the instance gate or the row lock, and the buffered
        // (history: none) path below never sees the bytes either.
        var schema = await LoadSchemaAsync(workflow, cancellationToken);
        delta = await OffloadFilesAsync(instance, workflow, schema.Schema, delta, cancellationToken);

        using var gate = await InstanceWriteGate.AcquireAsync(instance.Id, cancellationToken);
        if (instance.DataBuffer is { } buffer)
            return await AppendBufferedAsync(instance, buffer, delta, versionStrategy, schema, cancellationToken);

        return await AppendCoreAsync(instance, delta, versionStrategy, schema, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<InstanceData?> FlushAsync(
        Instance instance,
        Definitions.Workflow? workflow,
        Func<CancellationToken, Task>? inSameTransaction = null,
        CancellationToken cancellationToken = default)
    {
        var buffer = instance.DataBuffer;
        if (buffer is null || !buffer.HasPendingChanges)
        {
            if (inSameTransaction is not null)
                await inSameTransaction(cancellationToken);
            return null;
        }

        var schema = await LoadSchemaAsync(workflow, cancellationToken);
        using var gate = await InstanceWriteGate.AcquireAsync(instance.Id, cancellationToken);
        var baseVersion = buffer.BaseRow?.Version;

        // The accumulated delta is merged onto the PERSISTED head, so encryption, hashing, sanitizing
        // and validation run exactly as for a direct append; only the version is folded from the
        // strategies the buffer accepted.
        var row = await AppendCoreAsync(
            instance,
            buffer.AccumulatedDelta!,
            VersionStrategy.None,
            schema,
            cancellationToken,
            versionResolver: head =>
            {
                if (!string.Equals(head?.Version, baseVersion, StringComparison.Ordinal))
                    logger.InstanceDataBufferDrift(instance.Id, baseVersion, head?.Version);
                return buffer.ResolveVersion(head?.Version);
            },
            afterPersist: inSameTransaction);

        buffer.MarkFlushed(row);
        logger.InstanceDataBufferFlushed(instance.Id, row?.Version, row is not null);
        return row;
    }

    /// <summary>
    /// Buffered append of a <c>history: none</c> instance (vnext#1006): merges the delta onto the
    /// buffer's in-memory head with the same <see cref="PlanAppend"/> rules and validates the merged
    /// content, without touching the database. The buffer changes only after validation passes.
    /// </summary>
    private async Task<InstanceData?> AppendBufferedAsync(
        Instance instance,
        InstanceDataBuffer buffer,
        JsonData delta,
        VersionStrategy? versionStrategy,
        SchemaLoad schemaLoad,
        CancellationToken cancellationToken)
    {
        var headRow = buffer.Head;
        var head = headRow is null
            ? null
            : new InstanceDataHeadRow { Version = headRow.Version, DataHash = headRow.DataHash, Data = headRow.Data.Json };

        var schema = ResolveSchema(schemaLoad, head);
        var writeOptions = executionOptions.Value.InstanceDataWrite;
        var plan = PlanAppend(
            head, delta, versionStrategy, writeOptions.LegacyAppendPipeline, writeOptions.PreserveNumericPrecision);
        if (plan.IsDuplicate)
            return null;

        // A head carrying x-encryption values holds tokens, not the plaintext the schema describes:
        // the flush validates the opened document instead.
        if (!EncryptedValueFormat.MayContainReserved(plan.Content.Json))
            await ValidateAgainstSchemaAsync(schema, plan.Content);

        var accumulated = buffer.AccumulatedDelta is { } previous
            ? JsonData.FromNormalized(JsonCanonicalizer.MergeAndCanonicalize(
                previous.JsonElement,
                delta.JsonElement,
                writeOptions.PreserveNumericPrecision ? JsonNumberPolicy.PreservePrecision : JsonNumberPolicy.Legacy).NormalizedJson)
            : delta;

        var row = buffer.Accept(
            instance.Id, plan.Content, InstanceData.ComputeDataHash(plan.Content), plan.Version, accumulated, versionStrategy);
        logger.InstanceDataBuffered(instance.Id, row.Version);
        return row;
    }

    private async Task<InstanceData?> AppendCoreAsync(
        Instance instance,
        JsonData delta,
        VersionStrategy? versionStrategy,
        SchemaLoad schemaLoad,
        CancellationToken cancellationToken,
        Func<InstanceDataHeadRow?, string>? versionResolver = null,
        Func<CancellationToken, Task>? afterPersist = null)
    {
        var context = await dbContextProvider.GetDbContextAsync();

        return await RunLockedAsync(context, instance.Id, cancellationToken, async () =>
        {
            var row = await AppendLockedAsync(context, instance, delta, versionStrategy, schemaLoad, versionResolver, cancellationToken);

            // Runs inside the row-lock transaction, so a flush and the caller's own save commit together.
            if (afterPersist is not null)
                await afterPersist(cancellationToken);

            return row;
        });
    }

    private async Task<InstanceData?> AppendLockedAsync(
        WorkflowDbContext context,
        Instance instance,
        JsonData delta,
        VersionStrategy? versionStrategy,
        SchemaLoad schemaLoad,
        Func<InstanceDataHeadRow?, string>? versionResolver,
        CancellationToken cancellationToken)
    {
        var head = await ReadHeadAsync(context, instance.Id, cancellationToken);
        var schema = ResolveSchema(schemaLoad, head);
        var (encryption, plainHead, sanitizedDelta) =
            await PrepareEncryptionAsync(context, instance.Id, schema, head, delta, cancellationToken);

        var writeOptions = executionOptions.Value.InstanceDataWrite;
        var plan = PlanAppend(
            plainHead, sanitizedDelta, versionStrategy, writeOptions.LegacyAppendPipeline, writeOptions.PreserveNumericPrecision);
        if (versionResolver is not null)
            plan = plan with { Version = versionResolver(head) };

        // x-encryption "hash" is applied to the merged document BEFORE dedup: the digest is deterministic
        // within the instance, so an unchanged value hashes to what the head already stores.
        var content = encryption.Secret is { } secret
            ? protector!.ApplyHashes(plan.Content, encryption.Paths.Hash, secret)
            : plan.Content;

        // Version and size are only known now (PlanAppend needs the head read under the row
        // lock) — the span starts here rather than at method entry, per Task 9.
        using var activity = StartAppendActivity(
            plan.Version, Encoding.UTF8.GetByteCount(content.NormalizedJson));

        // A row that will carry tokens is hashed with a key (see AesGcmFieldCipher.KeyedHash); the
        // dedup compares in that scheme. A head hashed in the other scheme misses once — one extra row.
        var dataHash = encryption.KeyedHash
            ? InstanceDataProtector.KeyedDataHash(content, encryption.Secret!)
            : InstanceData.ComputeDataHash(content);
        var isDuplicate = encryption.Secret is not null
            ? head is not null && string.Equals(dataHash, head.DataHash, StringComparison.OrdinalIgnoreCase)
            : plan.IsDuplicate;

        if (isDuplicate)
        {
            return null;
        }

        await ValidateAgainstSchemaAsync(schema, content);

        var stored = Seal(instance.Id, content, encryption);

        // A strategy append always sits at or above the head → it takes the latest flag.
        // VersionNo is line-scoped: the next ordinal WITHIN the target Version string.
        var row = new InstanceData(Guid.NewGuid(), instance.Id, plan.Version, stored, dataHash, isLatest: true)
        {
            // A new semantic-version line always starts at one. Only same-version appends
            // need MAX(VersionNo), which removes one query from every version increment.
            VersionNo = head is null || !string.Equals(plan.Version, head.Version, StringComparison.Ordinal)
                ? 1
                : await ReadLineMaxAsync(context, instance.Id, plan.Version, cancellationToken) + 1
        };

        await PersistAsync(context, instance, row, demoteStaleLatest: head is not null, cancellationToken);
        return row;
    }

    /// <inheritdoc />
    public async Task<InstanceData> AppendExplicitAsync(
        Instance instance,
        Guid id,
        string version,
        JsonData data,
        CancellationToken cancellationToken = default,
        Definitions.Workflow? workflow = null)
    {
        var schema = await LoadSchemaAsync(workflow, cancellationToken);
        data = await OffloadFilesAsync(instance, workflow, schema.Schema, data, cancellationToken);

        using var gate = await InstanceWriteGate.AcquireAsync(instance.Id, cancellationToken);
        return await AppendExplicitCoreAsync(instance, id, version, data, schema, cancellationToken);
    }

    private async Task<InstanceData> AppendExplicitCoreAsync(
        Instance instance,
        Guid id,
        string version,
        JsonData data,
        SchemaLoad schemaLoad,
        CancellationToken cancellationToken)
    {
        // Version and data are already known at entry — unlike AppendCoreAsync, no head read is
        // needed to know what is being written.
        using var activity = StartAppendActivity(version, Encoding.UTF8.GetByteCount(data.NormalizedJson));

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
            var schema = ResolveSchema(schemaLoad, head);
            var (encryption, _, sanitized) =
                await PrepareEncryptionAsync(context, instance.Id, schema, head, data, cancellationToken);
            var content = encryption.Secret is { } secret
                ? protector!.ApplyHashes(sanitized, encryption.Paths.Hash, secret)
                : sanitized;

            // An explicit (possibly older-line) version only takes the latest flag when it
            // compares at or above the head — an older line never steals the global latest.
            var takesLatest = head is null
                || InstanceDataVersionComparer.CompareVersionStrings(version, head.Version) >= 0;

            await ValidateAgainstSchemaAsync(schema, content);

            var stored = Seal(instance.Id, content, encryption);
            var dataHash = encryption.KeyedHash
                ? InstanceDataProtector.KeyedDataHash(content, encryption.Secret!)
                : InstanceData.ComputeDataHash(content);

            var row = new InstanceData(id, instance.Id, version, stored, dataHash, takesLatest)
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
    /// <para>
    /// <paramref name="preserveNumericPrecision"/> is the <c>InstanceDataWrite:PreserveNumericPrecision</c>
    /// opt-in (see <see cref="BBT.Workflow.BackgroundJobs.Options.InstanceDataWriteOptions"/>):
    /// it only selects the <see cref="JsonNumberPolicy"/> passed into the canonicalizer on this
    /// (non-legacy) path, and is ignored whenever <paramref name="legacyPipeline"/> is true.
    /// </para>
    /// </summary>
    internal static AppendPlan PlanAppend(
        InstanceDataHeadRow? head,
        JsonData delta,
        VersionStrategy? versionStrategy,
        bool legacyPipeline,
        bool preserveNumericPrecision = false)
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

        var numberPolicy = preserveNumericPrecision
            ? JsonNumberPolicy.PreservePrecision
            : JsonNumberPolicy.Legacy;
        var baseElement = new JsonData(head.Data).JsonElement;
        var result = JsonCanonicalizer.MergeAndCanonicalize(baseElement, delta.JsonElement, numberPolicy);

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
    /// instance-global sequence. Strategy appends whose planned version differs from the head
    /// start directly at 1; only same-version appends reach this query.
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
    /// x-storage defence in depth (spec §3): <c>content</c> produced by task outputs or subflow output mappings is
    /// offloaded (Trusted) before the row is written. Runs outside the instance write gate and the row lock.
    /// </summary>
    /// <remarks>
    /// Runs on every append, so the no-op path does no extra I/O: the fields come from the schema the append already
    /// loaded (<see cref="LoadSchemaAsync"/>; no second component-cache read) through a memo whose hit allocates
    /// nothing. No workflow or no resolved schema (same rule as validation — the caller holds the definition), a
    /// host without the Application module (workers, DbMigrator), a master schema without <c>x-storage</c> fields, or
    /// a delta with no <c>content</c> member at any <c>x-storage</c> path (read-only probe, no mutable DOM) ⇒ no span
    /// and no offload call. A failure throws (<see cref="FileOffloadFailure"/>: 503 store unavailable / 400 invalid
    /// node) and follows the caller's normal error path; nothing has been written.
    /// </remarks>
    private async Task<JsonData> OffloadFilesAsync(
        Instance instance,
        Definitions.Workflow? workflow,
        SchemaDefinition? schema,
        JsonData delta,
        CancellationToken cancellationToken)
    {
        if (workflow is null || schema is null)
            return delta;

        // Resolved lazily for the same reason as IComponentCacheStore: the service lives in the Application module.
        var offloadService = serviceProvider.GetService<IFileOffloadService>();
        if (offloadService is null)
            return delta;

        var fields = offloadService.GetFields(schema);
        if (fields.Count == 0)
            return delta;

        var element = delta.JsonElement;
        if (element.ValueKind != System.Text.Json.JsonValueKind.Object || !FileNodeWalker.AnyContent(element, fields))
            return delta;

        using var activity = PipelineStepActivityHelper.StartOperationActivity("Files.Offload");
        var result = await offloadService.OffloadAsync(new FileOffloadRequest(
            workflow, instance.Id, element, LatestData: null, FileOffloadMode.Trusted, fields), cancellationToken);
        if (!result.IsSuccess)
        {
            activity?.SetStatus(ActivityStatusCode.Error, result.Error.Code);
            throw FileOffloadFailure.ToException(result.Error);
        }

        return result.Value!.Changed && result.Value.Payload is { } payload
            ? JsonData.FromElement(payload)
            : delta;
    }

    /// <summary>
    /// The master schema as loaded before the write gate: <see cref="FailedKey"/> is set when the workflow names a
    /// schema that could not be loaded (the head-dependent decision is taken under the lock, <see cref="ResolveSchema"/>).
    /// </summary>
    private readonly record struct SchemaLoad(SchemaDefinition? Schema, string? FailedKey);

    /// <summary>
    /// Loads the workflow's master schema once per append, before the write gate — validation, <c>x-encryption</c>
    /// and <c>x-storage</c> all read it.
    /// </summary>
    /// <remarks>
    /// The definition arrives as an argument from the caller, which already holds it (the pipeline
    /// context, the script context, the start path's own load). It used to be read from an ambient
    /// scope, and a caller running outside such a scope silently skipped validation; passing it
    /// explicitly keeps that outcome visible at each call site instead of hiding it here.
    /// </remarks>
    private async Task<SchemaLoad> LoadSchemaAsync(
        Definitions.Workflow? workflow,
        CancellationToken cancellationToken)
    {
        if (workflow?.Schema is null)
            return default;

        // Resolved lazily: IComponentCacheStore lives in the Application module, which non-HTTP
        // hosts (workers, DbMigrator) do not load. Those hosts never pass a workflow, so this line
        // is not reached there; the null-check is a belt-and-braces skip.
        var componentCacheStore = serviceProvider.GetService<IComponentCacheStore>();
        if (componentCacheStore is null)
        {
            logger.InstanceDataSchemaLoadFailed(workflow.Schema.Key, "IComponentCacheStore is not registered in this host");
            return new SchemaLoad(null, workflow.Schema.Key);
        }

        var schemaResult = await componentCacheStore.GetSchemaAsync(workflow.Schema, cancellationToken);
        if (!schemaResult.IsSuccess)
        {
            logger.InstanceDataSchemaLoadFailed(workflow.Schema.Key, schemaResult.Error.Message);
            return new SchemaLoad(null, workflow.Schema.Key);
        }

        return new SchemaLoad(schemaResult.Value, null);
    }

    /// <summary>
    /// The loaded schema, or — when it could not be loaded — skipped (logged at load) as before, unless the
    /// instance's head already carries <c>x-encryption</c> values: then the write cannot know which paths to protect
    /// and is refused (<see cref="EncryptionSchemaUnavailableException"/>, 503) instead of storing them in plaintext.
    /// </summary>
    private SchemaDefinition? ResolveSchema(SchemaLoad load, InstanceDataHeadRow? head)
        => load.FailedKey is { } key ? SchemaUnavailable(key, head) : load.Schema;

    // An instance whose head already carries x-encryption values is known to use them: without the schema the
    // write cannot know which paths to protect, so it is refused rather than stored in plaintext.
    private SchemaDefinition? SchemaUnavailable(string schemaKey, InstanceDataHeadRow? head) =>
        protector is not null && head is not null && EncryptedValueFormat.MayContainReserved(head.Data)
            ? throw new EncryptionSchemaUnavailableException(schemaKey)
            : null;

    /// <summary>
    /// Validates the (plaintext) content against the resolved master schema — the same contract the old
    /// aggregate mutation methods enforced.
    /// </summary>
    private Task ValidateAgainstSchemaAsync(SchemaDefinition? schema, JsonData content)
    {
        if (schema is null)
            return Task.CompletedTask;

        var validationResult = jsonSchemaValidator.Validate(schema.Schema, content.JsonElement);
        if (!validationResult.IsSuccess)
        {
            throw new SchemaValidationException(
                validationResult.Error.Message ?? "Schema Validation Error",
                validationResult.Error.ValidationErrors?.ToList().AsReadOnly());
        }

        return Task.CompletedTask;
    }

    // ── x-encryption (hash, encrypt) ─────────────────────────────────────────

    /// <summary>What an append has to do about <c>x-encryption</c>.</summary>
    private sealed record EncryptionPlan(ProtectedPaths Paths, InstanceDataView? HeadView, InstanceSecretMaterial? Secret)
    {
        public static readonly EncryptionPlan None = new(ProtectedPaths.None, null, null);

        /// <summary>The new row's DataHash is keyed when the row can carry encrypt tokens.</summary>
        public bool KeyedHash => Secret is not null && Paths.Encrypt.Count > 0;
    }

    /// <summary>
    /// Resolves the instance's secret (creating it on the first protected write, under the row lock), opens the head so
    /// the merge, dedup and validation work on the engine's view, and rejects any reserved-prefix value the delta
    /// introduces other than the value already stored at the same path. Returns the plan, the head in its opened form and
    /// the sanitized delta.
    /// </summary>
    private async Task<(EncryptionPlan Plan, InstanceDataHeadRow? Head, JsonData Delta)> PrepareEncryptionAsync(
        WorkflowDbContext context,
        Guid instanceId,
        SchemaDefinition? schema,
        InstanceDataHeadRow? head,
        JsonData delta,
        CancellationToken cancellationToken)
    {
        if (protector is null)
            return (EncryptionPlan.None, head, delta);

        var paths = schema is null
            ? ProtectedPaths.None
            : ProtectedPathMemo.GetValue(schema, static s =>
            {
                var exposure = Definitions.Schemas.SchemaRolesParser.ParseExposure(s.Schema);
                return new ProtectedPaths(exposure.EncryptPaths, exposure.HashPaths);
            });
        var writes = paths.Any && (encryptionOptions?.Value.EncryptWrites ?? true);
        var headHasTokens = head is not null && EncryptedValueFormat.MayContainToken(head.Data);

        InstanceSecretMaterial? secret = null;
        if (writes)
            secret = await protector.Secrets.GetOrCreateAsync(context, instanceId, cancellationToken);
        else if (headHasTokens)
            await protector.Secrets.PreloadAsync(context, [instanceId], cancellationToken);

        InstanceDataView? headView = null;
        JsonData? headStored = null;
        if (head is not null)
        {
            headStored = new JsonData(head.Data);
            headView = await protector.UnprotectAsync(context.CurrentSchemaName, instanceId, headStored, cancellationToken);
            if (headView.HasTokens)
                head = new InstanceDataHeadRow { Version = head.Version, DataHash = head.DataHash, Data = headView.Plain.Json };
        }

        delta = protector.SanitizeDelta(instanceId, delta, headStored, headView, paths.Hash);

        // Writes switched off (operator rollback): plaintext on purpose; existing tokens were still opened above.
        return (writes ? new EncryptionPlan(paths, headView, secret) : new EncryptionPlan(ProtectedPaths.None, headView, null),
                head, delta);
    }

    /// <summary>The row as it is stored: the merged content with its <c>encrypt</c> paths sealed.</summary>
    private JsonData Seal(Guid instanceId, JsonData content, EncryptionPlan encryption) =>
        encryption.Secret is { } secret
            ? protector!.Protect(instanceId, content, encryption.Paths.Encrypt, encryption.HeadView, secret).Stored
            : content;

    private static string SanitizeIdentifier(string identifier)
        => identifier.Replace("\"", "", StringComparison.Ordinal);

    /// <summary>
    /// Starts the <c>Instance.AppendData</c> span around an instance-data append, tagged with
    /// the semantic version and the serialized (UTF-8) byte size of the row being written —
    /// never the payload content itself. Extracted from <see cref="AppendCoreAsync"/> /
    /// <see cref="AppendExplicitCoreAsync"/> so the wiring is unit-testable without constructing
    /// this Npgsql-backed service (DbContext, transactions, row locks).
    /// </summary>
    internal static Activity? StartAppendActivity(string version, long sizeBytes)
    {
        var activity = PipelineStepActivityHelper.StartOperationActivity("Instance.AppendData");
        activity?.SetTag(TelemetryConstants.TagNames.DataVersion, version);
        activity?.SetTag(TelemetryConstants.TagNames.DataSizeBytes, sizeBytes);
        return activity;
    }
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
