using System.Text.Json;
using BBT.Aether.BackgroundJob;
using BBT.Workflow.Runtime;
using Npgsql;

namespace BBT.Workflow.Data;

/// <summary>
/// Opens the job's <see cref="DomainScope"/> before Aether claims it, under multi-domain hosting with
/// dedicated databases.
/// </summary>
/// <remarks>
/// <para>
/// Aether's dispatcher claims the job row in <c>sys_queues.BackgroundJobs</c> in its own unit of work
/// <b>before</b> the vNext handler runs and opens the scope from its arguments — so without this the
/// claim would look in the default database and skip the job ("not found in an active state").
/// </para>
/// <para>
/// The Dapr job carries the payload <b>by reference</b>: the envelope has <c>schema</c> and
/// <c>extensions.jobid</c> but no <c>data</c>, and a flow schema does not identify a domain (dedicated
/// databases use unprefixed names). The domain is therefore taken from the payload when it is inline,
/// else by finding the database that holds the job id — one primary-key lookup per database, only in a
/// pool with more than one database. With one database the inner bridge runs exactly as before.
/// </para>
/// </remarks>
public sealed class DomainScopedJobExecutionBridge(
    IJobExecutionBridge inner,
    IDomainDatabaseResolver resolver,
    string queueSchema = "sys_queues") : IJobExecutionBridge
{
    /// <inheritdoc />
    public async Task ExecuteAsync(string jobName, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        string? domain = null;
        if (resolver.DatabaseRepresentatives.Count > 1)
        {
            domain = TryReadDomain(payload);
            if (domain is null && TryReadJobId(payload) is { } jobId)
            {
                domain = await FindDomainOfJobAsync(jobId, cancellationToken);
            }
        }

        using (DomainScope.Begin(domain))
        {
            await inner.ExecuteAsync(jobName, payload, cancellationToken);
        }
    }

    /// <summary>The database (as one of its domains) whose <c>BackgroundJobs</c> holds <paramref name="jobId"/>.</summary>
    private async Task<string?> FindDomainOfJobAsync(Guid jobId, CancellationToken cancellationToken)
    {
        foreach (var domain in resolver.DatabaseRepresentatives)
        {
            if (resolver.ConnectionStringFor(domain) is not { } connectionString)
            {
                continue;
            }

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                $"SELECT 1 FROM \"{queueSchema}\".\"BackgroundJobs\" WHERE \"Id\" = @id LIMIT 1", connection);
            command.Parameters.AddWithValue("id", jobId);
            if (await command.ExecuteScalarAsync(cancellationToken) is not null)
            {
                return domain;
            }
        }

        return null;
    }

    /// <summary>The envelope's <c>extensions.jobid</c> (payload-by-reference jobs).</summary>
    internal static Guid? TryReadJobId(ReadOnlyMemory<byte> payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                   && TryGetProperty(root, "extensions", out var extensions)
                   && extensions.ValueKind == JsonValueKind.Object
                   && TryGetProperty(extensions, "jobid", out var jobId)
                   && jobId.ValueKind == JsonValueKind.String
                   && Guid.TryParse(jobId.GetString(), out var id)
                ? id
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The payload's <c>domain</c>, from the envelope's <c>data</c> or the bare payload.</summary>
    internal static string? TryReadDomain(ReadOnlyMemory<byte> payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (TryGetProperty(root, "data", out var data))
            {
                if (data.ValueKind == JsonValueKind.String && data.GetString() is { } inner)
                {
                    return ReadDomainFromText(inner);
                }

                return ReadDomain(data);
            }

            return ReadDomain(root);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A string <c>data</c> is either JSON text or base64 of the serialized payload.</summary>
    private static string? ReadDomainFromText(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            using var document = JsonDocument.Parse(trimmed);
            return ReadDomain(document.RootElement);
        }

        var buffer = new byte[(text.Length * 3 / 4) + 3];
        if (!Convert.TryFromBase64String(text, buffer, out var written))
        {
            return null;
        }

        using var decoded = JsonDocument.Parse(buffer.AsMemory(0, written));
        return ReadDomain(decoded.RootElement);
    }

    private static string? ReadDomain(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object
        && TryGetProperty(element, "domain", out var domain)
        && domain.ValueKind == JsonValueKind.String
            ? domain.GetString()
            : null;

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
