using System.Text.Json;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Workflow.Caching;
using BBT.Workflow.Data;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Runtime;
using BBT.Workflow.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Infrastructure.Security;

/// <summary>
/// Default <see cref="IInstanceDataEncryptionMaintenanceService"/>: pages over the current schema's
/// instances, recomputes each data row's stored payload against the active key, and rewrites only
/// the rows that actually differ.
/// </summary>
/// <param name="dbContextProvider">Schema-bound context provider.</param>
/// <param name="componentCacheStore">Resolves each instance's workflow and master schema.</param>
/// <param name="runtimeInfoProvider">Supplies the host domain for workflow lookup.</param>
/// <param name="cipher">Encrypts under the active key.</param>
/// <param name="keyProvider">Identifies the active key.</param>
/// <param name="options">Encryption options.</param>
public sealed class InstanceDataEncryptionMaintenanceService(
    IAetherDbContextProvider<WorkflowDbContext> dbContextProvider,
    IComponentCacheStore componentCacheStore,
    IRuntimeInfoProvider runtimeInfoProvider,
    ISensitiveDataCipher cipher,
    IDataEncryptionKeyProvider keyProvider,
    IOptions<DataEncryptionOptions> options) : IInstanceDataEncryptionMaintenanceService
{
    /// <inheritdoc />
    public async Task<EncryptionMaintenanceReport> ReEncryptAsync(
        EncryptionMaintenanceRequest request,
        CancellationToken cancellationToken = default)
    {
        var activeKeyId = keyProvider.GetActive()?.KeyId;

        // Refuse to write when encryption is not usable. With encryption disabled, Encrypt() is a
        // pass-through, so every row would be rewritten to PLAINTEXT — a silent mass-decryption of
        // exactly the data this feature exists to protect. Dry runs are still allowed so an
        // operator can inspect the estate before provisioning keys.
        if (!request.DryRun && !cipher.IsEnabled)
        {
            throw new SensitiveDataEncryptionException(
                "Refusing to run a write pass while instance-data encryption is disabled or has no " +
                $"active key: it would rewrite every row to plaintext. Set " +
                $"'{DataEncryptionOptions.SectionName}:Enabled' and a valid 'ActiveKeyId' first, or " +
                "call again with dryRun=true to inspect only.");
        }

        var context = await dbContextProvider.GetDbContextAsync();
        var schema = SanitizeIdentifier(context.CurrentSchemaName ?? "public");

        var failures = new List<string>();
        int instancesScanned = 0, rowsScanned = 0, rowsRewritten = 0, rowsCurrent = 0, expired = 0;

        var batchSize = Math.Clamp(request.BatchSize, 1, 1000);
        Guid? cursor = null;

        while (true)
        {
            var page = await LoadInstancePageAsync(context, request.InstanceKey, cursor, batchSize, cancellationToken);
            if (page.Count == 0)
                break;

            foreach (var target in page)
            {
                cursor = target.Id;

                if (request.MaxInstances is { } max && instancesScanned >= max)
                    return Build();

                instancesScanned++;

                IReadOnlyDictionary<string, SensitiveFieldMetadata> sensitiveFields;
                try
                {
                    sensitiveFields = await ResolveSensitiveFieldsAsync(target, cancellationToken);
                }
                catch (Exception ex)
                {
                    // A single unresolvable workflow must not abort the whole pass; the row is left
                    // exactly as it was and named in the report.
                    failures.Add($"{target.Id}: schema resolution failed — {ex.Message}");
                    continue;
                }

                var rows = await LoadRowsAsync(context, schema, target.Id, cancellationToken);

                foreach (var row in rows)
                {
                    rowsScanned++;

                    try
                    {
                        // Decrypt is schema-free (marker-driven); encrypt is schema-driven. Together
                        // they normalise the row onto the active key regardless of whether it
                        // started as plaintext (backfill) or an older key (rotation).
                        var plaintext = cipher.Decrypt(new JsonData(row.Data));
                        var rewritten = cipher.Encrypt(plaintext, sensitiveFields);

                        expired += CountExpiredRetentionValues(plaintext, sensitiveFields, row.EnteredAt);

                        if (IsAlreadyCurrent(row.Data, rewritten.Json, activeKeyId))
                        {
                            rowsCurrent++;
                            continue;
                        }

                        rowsRewritten++;

                        if (!request.DryRun)
                            await UpdateRowAsync(context, schema, row.Id, rewritten.Json, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{target.Id}/{row.Id}: {ex.Message}");
                    }
                }
            }

            if (page.Count < batchSize)
                break;
        }

        return Build();

        EncryptionMaintenanceReport Build() => new(
            request.DryRun,
            activeKeyId,
            instancesScanned,
            rowsScanned,
            rowsRewritten,
            rowsCurrent,
            expired,
            failures);
    }

    /// <summary>
    /// A row needs no work when its stored payload already carries only active-key markers and the
    /// recomputed payload would add nothing new. Compared structurally rather than by string
    /// equality, because a fresh nonce makes every re-encryption differ byte-for-byte.
    /// </summary>
    private static bool IsAlreadyCurrent(string storedJson, string rewrittenJson, string? activeKeyId)
    {
        // With no active key nothing can be brought onto one, so there is no work to report. Saying
        // otherwise would advertise a "rewrite" that could only ever decrypt.
        if (activeKeyId is null)
            return true;

        var storedHasCiphertext = ISensitiveDataCipher.ContainsCiphertext(storedJson);
        var rewrittenHasCiphertext = ISensitiveDataCipher.ContainsCiphertext(rewrittenJson);

        // Nothing is encrypted and nothing should be → already correct.
        if (!storedHasCiphertext && !rewrittenHasCiphertext)
            return true;

        // Should be encrypted but is not (backfill), or is encrypted but should not be.
        if (storedHasCiphertext != rewrittenHasCiphertext)
            return false;

        // Both encrypted: the row is current only when every marker already names the active key.
        return !HasForeignKeyMarker(storedJson, activeKeyId);
    }

    private static bool HasForeignKeyMarker(string json, string activeKeyId)
    {
        var current = $"{SensitiveDataCipher.MarkerPrefix}{activeKeyId}:";
        var index = json.IndexOf(SensitiveDataCipher.MarkerPrefix, StringComparison.Ordinal);

        while (index >= 0)
        {
            if (!json.AsSpan(index).StartsWith(current, StringComparison.Ordinal))
                return true;

            index = json.IndexOf(
                SensitiveDataCipher.MarkerPrefix,
                index + SensitiveDataCipher.MarkerPrefix.Length,
                StringComparison.Ordinal);
        }

        return false;
    }

    /// <summary>
    /// Counts sensitive values whose retention window has elapsed. Reported only — see the
    /// interface remarks for why enforcement is not implemented here.
    /// </summary>
    private static int CountExpiredRetentionValues(
        JsonData plaintext,
        IReadOnlyDictionary<string, SensitiveFieldMetadata> sensitiveFields,
        DateTime enteredAt)
    {
        var expired = 0;
        var now = DateTime.UtcNow;

        foreach (var (path, metadata) in sensitiveFields)
        {
            if (metadata.RetentionDays is not { } days || days <= 0)
                continue;

            if (enteredAt.AddDays(days) > now)
                continue;

            if (PathHasValue(plaintext.JsonElement, path))
                expired++;
        }

        return expired;
    }

    private static bool PathHasValue(JsonElement root, string path)
    {
        var current = new List<JsonElement> { root };

        foreach (var rawSegment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = rawSegment;
            var arrayDepth = 0;
            while (segment.EndsWith("[]", StringComparison.Ordinal))
            {
                arrayDepth++;
                segment = segment[..^2];
            }

            var next = new List<JsonElement>();
            foreach (var node in current)
            {
                if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty(segment, out var child))
                    next.Add(child);
            }

            for (var depth = 0; depth < arrayDepth; depth++)
            {
                var unwrapped = new List<JsonElement>();
                foreach (var node in next)
                {
                    if (node.ValueKind == JsonValueKind.Array)
                        unwrapped.AddRange(node.EnumerateArray());
                }

                next = unwrapped;
            }

            current = next;
            if (current.Count == 0)
                return false;
        }

        return current.Exists(static node =>
            node.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined));
    }

    private async Task<IReadOnlyDictionary<string, SensitiveFieldMetadata>> ResolveSensitiveFieldsAsync(
        MaintenanceInstance target,
        CancellationToken cancellationToken)
    {
        var empty = new Dictionary<string, SensitiveFieldMetadata>();

        var workflowResult = await componentCacheStore.GetFlowAsync(
            runtimeInfoProvider.Domain, target.Flow, target.FlowVersion, cancellationToken);

        if (!workflowResult.IsSuccess || workflowResult.Value?.Schema is null)
            return empty;

        var schemaResult = await componentCacheStore.GetSchemaAsync(workflowResult.Value.Schema, cancellationToken);
        if (!schemaResult.IsSuccess || schemaResult.Value is null)
            return empty;

        var schema = schemaResult.Value;
        return SensitiveSchemaCache.GetOrParse(schema.Domain, schema.Key, schema.Version, schema.Schema);
    }

    private static async Task<List<MaintenanceInstance>> LoadInstancePageAsync(
        WorkflowDbContext context,
        string? instanceKey,
        Guid? cursor,
        int batchSize,
        CancellationToken cancellationToken)
    {
        // Keyset pagination on Id: stable while the pass is running and immune to rows being
        // written underneath it, unlike Skip/Take.
        var query = context.Instances.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(instanceKey))
            query = query.Where(i => i.Key == instanceKey);

        if (cursor.HasValue)
            query = query.Where(i => i.Id.CompareTo(cursor.Value) > 0);

        return await query
            .OrderBy(i => i.Id)
            .Take(batchSize)
            .Select(i => new MaintenanceInstance(i.Id, i.Flow, i.FlowVersion))
            .ToListAsync(cancellationToken);
    }

    private static async Task<List<MaintenanceRow>> LoadRowsAsync(
        WorkflowDbContext context,
        string schema,
        Guid instanceId,
        CancellationToken cancellationToken)
    {
        return await context.Database.SqlQueryRaw<MaintenanceRow>(
                $"SELECT \"Id\", \"EnteredAt\", \"Data\"::text AS \"Data\" " +
                $"FROM \"{schema}\".\"InstancesData\" WHERE \"InstanceId\" = {{0}} ORDER BY \"Id\"",
                instanceId)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Rewrites only the payload column. Version, VersionNo, IsLatest, ETag and DataHash are
    /// untouched because the plaintext did not change — that is what keeps caches and long-polling
    /// clients undisturbed.
    /// </summary>
    private static async Task UpdateRowAsync(
        WorkflowDbContext context,
        string schema,
        Guid rowId,
        string json,
        CancellationToken cancellationToken)
    {
        await context.Database.ExecuteSqlRawAsync(
            $"UPDATE \"{schema}\".\"InstancesData\" SET \"Data\" = {{0}}::jsonb WHERE \"Id\" = {{1}}",
            [json, rowId],
            cancellationToken);
    }

    private static string SanitizeIdentifier(string identifier)
        => identifier.Replace("\"", string.Empty, StringComparison.Ordinal);

    private sealed record MaintenanceInstance(Guid Id, string Flow, string FlowVersion);

    private sealed class MaintenanceRow
    {
        public Guid Id { get; set; }
        public DateTime EnteredAt { get; set; }
        public string Data { get; set; } = string.Empty;
    }
}
