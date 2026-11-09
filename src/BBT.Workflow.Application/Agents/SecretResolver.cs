using BBT.Workflow.Scripting.Functions;
using Microsoft.Extensions.Configuration;

namespace BBT.Workflow.Agents;

/// <summary>
/// Resolves a <c>secretRef</c> authored on an agent provider profile or an MCP task (issue #1105).
/// </summary>
public interface ISecretResolver
{
    /// <summary>
    /// Resolves <paramref name="secretRef"/>: <c>store/bundle/key</c> reads the Dapr secret store through the
    /// script secret cache; any other form reads configuration (Vault is loaded into configuration at
    /// startup), with <c>/</c> mapped to the configuration separator. Returns null when nothing is found.
    /// </summary>
    Task<string?> ResolveAsync(string? secretRef, CancellationToken cancellationToken = default);
}

/// <summary>Default <see cref="ISecretResolver"/>.</summary>
public sealed class SecretResolver(IConfiguration configuration, IScriptSecretCache? secretCache = null) : ISecretResolver
{
    /// <inheritdoc />
    public async Task<string?> ResolveAsync(string? secretRef, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(secretRef))
            return null;

        var parts = secretRef.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 3 && secretCache is not null)
        {
            var value = await secretCache.GetSecretAsync(parts[0], parts[1], parts[2]);
            if (!string.IsNullOrEmpty(value))
                return value;
        }

        var direct = configuration[secretRef];
        if (!string.IsNullOrEmpty(direct))
            return direct;

        var mapped = configuration[string.Join(':', parts)];
        if (!string.IsNullOrEmpty(mapped))
            return mapped;

        // A bundle/key reference whose bundle was flattened at load time: the key alone.
        return parts.Length > 1 ? configuration[parts[^1]] : null;
    }
}
