using System.IO.Compression;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Workflow.Data;
using BBT.Workflow.Instances.Agents;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace BBT.Workflow.Agents;

/// <summary>
/// Agent conversation store (table <c>AgentSessions</c>, issue #1105).
/// <list type="bullet">
/// <item>Read: one primary-key lookup, projected (no tracking).</item>
/// <item>Write: one <c>INSERT … ON CONFLICT DO UPDATE</c> statement. A cross-instance <c>k:*</c> row is
/// guarded by <c>xmin = expected</c> (zero affected rows: another writer got there first); an <c>i:*</c>
/// row has one writer under the instance's Busy mutex and is written unguarded.</item>
/// <item>Content: the serialized message list, gzip-compressed. Not encrypted and not masked (a product
/// decision for this phase: no guard until real use asks for one).</item>
/// </list>
/// </summary>
public sealed class EfCoreAgentSessionStore(IAetherDbContextProvider<WorkflowDbContext> dbContextProvider) : IAgentSessionStore
{
    /// <inheritdoc />
    public async Task<AgentSessionReadResult> FindAsync(string scopeKey, Guid instanceId, CancellationToken cancellationToken = default)
    {
        var context = await dbContextProvider.GetDbContextAsync(cancellationToken);
        var row = await context.AgentSessions
            .AsNoTracking()
            .Where(s => s.ScopeKey == scopeKey)
            .Select(s => new
            {
                s.ScopeKey, s.AgentKey, s.AgentVersion, s.InstructionsHash, s.SessionData,
                s.ProviderConversationId, s.MessageCount, s.RowVersion, s.ExpiresAt
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null || (row.ExpiresAt is { } expires && expires < DateTime.UtcNow))
            return new AgentSessionReadResult(null, null);

        byte[]? messages = null;
        if (row.SessionData is { Length: > 0 } stored)
        {
            try
            {
                messages = Decompress(stored);
            }
            catch (Exception ex) when (ex is InvalidDataException or ArgumentException)
            {
                return new AgentSessionReadResult(null, ex.GetType().Name);
            }
        }

        return new AgentSessionReadResult(new AgentSessionSnapshot(
            row.ScopeKey, row.AgentKey, row.AgentVersion, row.InstructionsHash, messages,
            row.ProviderConversationId, row.MessageCount, row.RowVersion), null);
    }

    /// <inheritdoc />
    public async Task<bool> SaveAsync(AgentSessionWrite write, Guid instanceId, CancellationToken cancellationToken = default)
    {
        var context = await dbContextProvider.GetDbContextAsync(cancellationToken);
        var session = write.Session;

        var data = write.Messages is { Length: > 0 } messages ? Compress(messages) : null;

        var table = $"\"{(context.CurrentSchemaName ?? "public").Replace("\"", "", StringComparison.Ordinal)}\".\"AgentSessions\"";

        // Instance rows (i:*) have one writer, under the instance's Busy mutex: last write wins. Shared rows
        // (k:*) are guarded: an empty expected version (no row was read) never matches, so a row created in
        // the meantime is reported as a conflict instead of overwritten.
        var sql =
            $"INSERT INTO {table} (\"ScopeKey\", \"Mode\", \"Storage\", \"AgentKey\", \"AgentVersion\", \"InstructionsHash\", " +
            "\"SessionData\", \"ProviderConversationId\", \"MessageCount\", \"SizeBytes\", \"OwnerInstanceId\", " +
            "\"LastInstanceId\", \"LastRunId\", \"UpdatedAt\", \"ExpiresAt\") " +
            "VALUES (@scope, @mode, @storage, @agentKey, @agentVersion, @instructionsHash, @data, @conversationId, " +
            "@messageCount, @sizeBytes, @owner, @lastInstance, @lastRun, @updatedAt, @expiresAt) " +
            "ON CONFLICT (\"ScopeKey\") DO UPDATE SET " +
            "\"Mode\" = EXCLUDED.\"Mode\", \"Storage\" = EXCLUDED.\"Storage\", \"AgentKey\" = EXCLUDED.\"AgentKey\", " +
            "\"AgentVersion\" = EXCLUDED.\"AgentVersion\", \"InstructionsHash\" = EXCLUDED.\"InstructionsHash\", " +
            "\"SessionData\" = EXCLUDED.\"SessionData\", \"ProviderConversationId\" = EXCLUDED.\"ProviderConversationId\", " +
            "\"MessageCount\" = EXCLUDED.\"MessageCount\", " +
            "\"SizeBytes\" = EXCLUDED.\"SizeBytes\", \"LastInstanceId\" = EXCLUDED.\"LastInstanceId\", " +
            "\"LastRunId\" = EXCLUDED.\"LastRunId\", \"UpdatedAt\" = EXCLUDED.\"UpdatedAt\", \"ExpiresAt\" = EXCLUDED.\"ExpiresAt\" " +
            $"WHERE @shared = false OR (@expected <> '' AND {table}.xmin::text = @expected)";

        var affected = await context.Database.ExecuteSqlRawAsync(sql,
        [
            Param("scope", NpgsqlDbType.Varchar, write.ScopeKey),
            Param("mode", NpgsqlDbType.Smallint, (short)session.Mode),
            Param("storage", NpgsqlDbType.Smallint, (short)session.Storage),
            Param("agentKey", NpgsqlDbType.Varchar, session.AgentKey),
            Param("agentVersion", NpgsqlDbType.Varchar, session.AgentVersion),
            Param("instructionsHash", NpgsqlDbType.Varchar, session.InstructionsHash),
            Param("data", NpgsqlDbType.Bytea, data),
            Param("conversationId", NpgsqlDbType.Varchar, session.ProviderConversationId),
            Param("messageCount", NpgsqlDbType.Integer, session.MessageCount),
            Param("sizeBytes", NpgsqlDbType.Integer, data?.Length ?? 0),
            Param("owner", NpgsqlDbType.Uuid, session.OwnerInstanceId),
            Param("lastInstance", NpgsqlDbType.Uuid, session.LastInstanceId),
            Param("lastRun", NpgsqlDbType.Uuid, session.LastRunId),
            Param("updatedAt", NpgsqlDbType.TimestampTz, DateTime.SpecifyKind(session.UpdatedAt, DateTimeKind.Utc)),
            Param("expiresAt", NpgsqlDbType.TimestampTz, session.ExpiresAt is { } e ? DateTime.SpecifyKind(e, DateTimeKind.Utc) : null),
            Param("expected", NpgsqlDbType.Text, write.ExpectedRowVersion?.ToString() ?? string.Empty),
            Param("shared", NpgsqlDbType.Boolean, !write.ScopeKey.StartsWith("i:", StringComparison.Ordinal))
        ], cancellationToken);

        return affected == 1;
    }

    private static NpgsqlParameter Param(string name, NpgsqlDbType type, object? value) =>
        new(name, type) { Value = value ?? DBNull.Value };

    /// <inheritdoc />
    public async Task<int> SweepExpiredAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        var context = await dbContextProvider.GetDbContextAsync(cancellationToken);
        var table = $"\"{(context.CurrentSchemaName ?? "public").Replace("\"", "", StringComparison.Ordinal)}\".\"AgentSessions\"";
        return await context.Database.ExecuteSqlRawAsync(
            $"DELETE FROM {table} WHERE ctid IN (SELECT ctid FROM {table} WHERE \"ExpiresAt\" < now() LIMIT {{0}})",
            [Math.Max(1, batchSize)], cancellationToken);
    }

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
            gzip.Write(data);
        return output.ToArray();
    }

    private static byte[] Decompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }
}
