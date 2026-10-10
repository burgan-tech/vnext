using BBT.Aether.MultiSchema;

namespace BBT.Workflow.Runtime;

/// <summary>
/// Schema-name formatter for multi-domain hosting: a <b>co-hosted</b> domain's schemas carry a
/// <c>&lt;domain&gt;_</c> prefix, so several domains share one database without their flow schemas or
/// definition schemas (<c>sys_flows</c>, <c>sys_tasks</c>, …) colliding.
/// </summary>
/// <remarks>
/// <para>
/// <b>Backward compatible by construction.</b> The prefix applies only when the process hosts more than
/// one domain AND the current domain is not the primary (<c>APP_DOMAIN</c>). A single-domain process and
/// the primary domain of a pool produce exactly the names <see cref="DefaultSchemaNameFormatter"/>
/// produces, so existing databases are read and written unchanged.
/// </para>
/// <para>
/// <c>sys_queues</c> and <c>sys_metrics</c> are never prefixed: the outbox, inbox, background jobs,
/// distributed locks and function metrics are shared by every domain of a pool, and their rows carry the
/// domain themselves.
/// </para>
/// <para>
/// Aether's <c>CurrentSchema.Change</c> formats the name at the moment the scope opens, so the domain
/// scope (<see cref="DomainScope"/>) must already be open — which every entry point guarantees. The
/// format is idempotent: an already-prefixed name is returned unchanged, because some callers format a
/// name and then pass the result to <c>Change</c> again.
/// </para>
/// </remarks>
public sealed class DomainSchemaNameFormatter(IRuntimeInfoProvider runtimeInfoProvider) : ISchemaNameFormatter
{
    private const int MaxLength = 63; // PostgreSQL identifier limit

    private static readonly DefaultSchemaNameFormatter Inner = new();

    private static readonly HashSet<string> SharedSchemas = new(StringComparer.Ordinal)
    {
        "sys_queues",
        "sys_metrics"
    };

    /// <inheritdoc />
    public string Format(string schemaName)
    {
        var formatted = Inner.Format(schemaName);

        var prefix = PrefixForCurrentDomain();
        if (prefix is null || SharedSchemas.Contains(formatted) || formatted.StartsWith(prefix, StringComparison.Ordinal))
        {
            return formatted;
        }

        var prefixed = prefix + formatted;
        if (prefixed.Length > MaxLength)
        {
            // Truncation (what the default formatter does) could make two co-hosted schemas collide.
            throw new InvalidOperationException(
                $"Schema name '{prefixed}' for '{schemaName}' exceeds the PostgreSQL limit of {MaxLength} characters " +
                "under multi-domain hosting; shorten the component key or the domain name.");
        }

        return prefixed;
    }

    /// <summary>
    /// <c>&lt;domain&gt;_</c> for a co-hosted (non-primary) current domain; <c>null</c> when no prefix applies.
    /// </summary>
    private string? PrefixForCurrentDomain()
    {
        var hosted = runtimeInfoProvider.HostedDomains;
        if (hosted is not { Count: > 1 })
        {
            return null;
        }

        var current = runtimeInfoProvider.Domain;
        if (string.Equals(current, hosted[0], StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Inner.Format(current) + "_";
    }
}
