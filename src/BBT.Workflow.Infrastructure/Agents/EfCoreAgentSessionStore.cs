using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
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
/// <item>Write: one <c>INSERT … ON CONFLICT DO UPDATE … WHERE xmin = expected</c> statement; zero affected
/// rows means another writer got there first (only possible for cross-instance <c>k:*</c> rows; an
/// <c>i:*</c> row has one writer under the instance's Busy mutex).</item>
/// <item>Content: gzip, then AES-256-GCM with a fresh random key stored on the row; the scope key is the
/// associated data, so a ciphertext copied to another row does not decrypt. Deleting the row — or its
/// owning instance, which cascades — destroys the key with the content.</item>
/// </list>
/// </summary>
public sealed class EfCoreAgentSessionStore(IAetherDbContextProvider<WorkflowDbContext> dbContextProvider) : IAgentSessionStore
{
    private const byte FormatVersion = 0x01;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    /// <inheritdoc />
    public async Task<AgentSessionReadResult> FindAsync(string scopeKey, Guid instanceId, CancellationToken cancellationToken = default)
    {
        var context = await dbContextProvider.GetDbContextAsync(cancellationToken);
        var row = await context.AgentSessions
            .AsNoTracking()
            .Where(s => s.ScopeKey == scopeKey)
            .Select(s => new
            {
                s.ScopeKey, s.AgentKey, s.AgentVersion, s.InstructionsHash, s.SessionData, s.EncryptionKey,
                s.ProviderConversationId, s.MessageCount, s.RowVersion, s.ExpiresAt
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null || (row.ExpiresAt is { } expires && expires < DateTime.UtcNow))
            return new AgentSessionReadResult(null, null);

        byte[]? messages = null;
        if (row.SessionData is { Length: > 0 } sealedData)
        {
            if (row.EncryptionKey is not { Length: 32 } key)
                return new AgentSessionReadResult(null, "missing key");
            try
            {
                messages = Decompress(Open(sealedData, key, scopeKey));
            }
            catch (Exception ex) when (ex is CryptographicException or InvalidDataException or ArgumentException)
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

        byte[]? data = null;
        byte[]? key = null;
        if (write.Messages is { Length: > 0 } messages)
        {
            key = RandomNumberGenerator.GetBytes(32);
            data = Seal(Compress(messages), key, write.ScopeKey);
        }

        var affected = await context.Database.ExecuteSqlRawAsync(UpsertSql(SessionsTable(context)),
        [
            Param("scope", NpgsqlDbType.Varchar, write.ScopeKey),
            Param("mode", NpgsqlDbType.Smallint, (short)session.Mode),
            Param("storage", NpgsqlDbType.Smallint, (short)session.Storage),
            Param("agentKey", NpgsqlDbType.Varchar, session.AgentKey),
            Param("agentVersion", NpgsqlDbType.Varchar, session.AgentVersion),
            Param("instructionsHash", NpgsqlDbType.Varchar, session.InstructionsHash),
            Param("data", NpgsqlDbType.Bytea, data),
            Param("conversationId", NpgsqlDbType.Varchar, session.ProviderConversationId),
            Param("key", NpgsqlDbType.Bytea, key),
            Param("messageCount", NpgsqlDbType.Integer, session.MessageCount),
            Param("sizeBytes", NpgsqlDbType.Integer, data?.Length ?? 0),
            Param("owner", NpgsqlDbType.Uuid, session.OwnerInstanceId),
            Param("lastInstance", NpgsqlDbType.Uuid, session.LastInstanceId),
            Param("lastRun", NpgsqlDbType.Uuid, session.LastRunId),
            Param("updatedAt", NpgsqlDbType.TimestampTz, DateTime.SpecifyKind(session.UpdatedAt, DateTimeKind.Utc)),
            Param("expiresAt", NpgsqlDbType.TimestampTz, session.ExpiresAt is { } e ? DateTime.SpecifyKind(e, DateTimeKind.Utc) : null),
            Param("expected", NpgsqlDbType.Text, write.ExpectedRowVersion?.ToString() ?? string.Empty)
        ], cancellationToken);

        return affected == 1;
    }

    private static NpgsqlParameter Param(string name, NpgsqlDbType type, object? value) =>
        new(name, type) { Value = value ?? DBNull.Value };

    /// <inheritdoc />
    public async Task<int> SweepExpiredAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        var context = await dbContextProvider.GetDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;
        return await context.AgentSessions
            .Where(s => s.ExpiresAt < now)
            .OrderBy(s => s.ExpiresAt)
            .Take(Math.Max(1, batchSize))
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>The schema-qualified <c>AgentSessions</c> table, quoted; a quote in the schema name is dropped.</summary>
    private static string SessionsTable(WorkflowDbContext context) =>
        $"\"{(context.CurrentSchemaName ?? "public").Replace("\"", "", StringComparison.Ordinal)}\".\"AgentSessions\"";

    /// <summary>
    /// The upsert statement. The guard always references @expected: an empty expected version (no row was read) never
    /// matches, so a row created in the meantime is reported as a conflict instead of overwritten.
    /// </summary>
    private static string UpsertSql(string table) =>
        $"INSERT INTO {table} (\"ScopeKey\", \"Mode\", \"Storage\", \"AgentKey\", \"AgentVersion\", \"InstructionsHash\", " +
        "\"SessionData\", \"ProviderConversationId\", \"EncryptionKey\", \"MessageCount\", \"SizeBytes\", \"OwnerInstanceId\", " +
        "\"LastInstanceId\", \"LastRunId\", \"UpdatedAt\", \"ExpiresAt\") " +
        "VALUES (@scope, @mode, @storage, @agentKey, @agentVersion, @instructionsHash, @data, @conversationId, @key, " +
        "@messageCount, @sizeBytes, @owner, @lastInstance, @lastRun, @updatedAt, @expiresAt) " +
        "ON CONFLICT (\"ScopeKey\") DO UPDATE SET " +
        "\"Mode\" = EXCLUDED.\"Mode\", \"Storage\" = EXCLUDED.\"Storage\", \"AgentKey\" = EXCLUDED.\"AgentKey\", " +
        "\"AgentVersion\" = EXCLUDED.\"AgentVersion\", \"InstructionsHash\" = EXCLUDED.\"InstructionsHash\", " +
        "\"SessionData\" = EXCLUDED.\"SessionData\", \"ProviderConversationId\" = EXCLUDED.\"ProviderConversationId\", " +
        "\"EncryptionKey\" = EXCLUDED.\"EncryptionKey\", \"MessageCount\" = EXCLUDED.\"MessageCount\", " +
        "\"SizeBytes\" = EXCLUDED.\"SizeBytes\", \"LastInstanceId\" = EXCLUDED.\"LastInstanceId\", " +
        "\"LastRunId\" = EXCLUDED.\"LastRunId\", \"UpdatedAt\" = EXCLUDED.\"UpdatedAt\", \"ExpiresAt\" = EXCLUDED.\"ExpiresAt\" " +
        $"WHERE @expected <> '' AND {table}.xmin::text = @expected";

    internal static byte[] Seal(byte[] plain, byte[] key, string scopeKey)
    {
        var payload = new byte[1 + NonceSize + plain.Length + TagSize];
        payload[0] = FormatVersion;
        var nonce = payload.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, payload.AsSpan(1 + NonceSize, plain.Length), payload.AsSpan(1 + NonceSize + plain.Length, TagSize),
            Encoding.UTF8.GetBytes(scopeKey));
        return payload;
    }

    internal static byte[] Open(byte[] payload, byte[] key, string scopeKey)
    {
        if (payload.Length < 1 + NonceSize + TagSize || payload[0] != FormatVersion)
            throw new CryptographicException("unknown session format");
        var length = payload.Length - 1 - NonceSize - TagSize;
        var plain = new byte[length];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(payload.AsSpan(1, NonceSize), payload.AsSpan(1 + NonceSize, length),
            payload.AsSpan(1 + NonceSize + length, TagSize), plain, Encoding.UTF8.GetBytes(scopeKey));
        return plain;
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
