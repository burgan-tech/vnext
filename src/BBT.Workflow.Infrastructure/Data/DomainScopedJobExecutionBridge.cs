using System.Text.Json;
using BBT.Aether.BackgroundJob;
using BBT.Workflow.Runtime;

namespace BBT.Workflow.Data;

/// <summary>
/// Opens the job's <see cref="DomainScope"/> before Aether claims it, under multi-domain hosting with
/// dedicated databases.
/// </summary>
/// <remarks>
/// Aether's dispatcher claims the job row in <c>sys_queues.BackgroundJobs</c> in its own unit of work
/// <b>before</b> the vNext handler runs and opens the scope from its payload — so without this the claim
/// would look in the default database. Every vNext job payload carries <c>Domain</c>; it is read from the
/// CloudEvent envelope's <c>data</c> (or the bare payload). With one database, or a payload without a
/// domain, the inner bridge runs exactly as before.
/// </remarks>
public sealed class DomainScopedJobExecutionBridge(IJobExecutionBridge inner, IDomainDatabaseResolver resolver)
    : IJobExecutionBridge
{
    /// <inheritdoc />
    public async Task ExecuteAsync(string jobName, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var domain = resolver.DatabaseRepresentatives.Count > 1 ? TryReadDomain(payload) : null;
        using (DomainScope.Begin(domain))
        {
            await inner.ExecuteAsync(jobName, payload, cancellationToken);
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
                    using var innerDocument = JsonDocument.Parse(inner);
                    return ReadDomain(innerDocument.RootElement);
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
