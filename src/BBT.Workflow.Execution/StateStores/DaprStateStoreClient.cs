using System.Text.Json;
using Dapr.Client;
using Microsoft.Extensions.Configuration;

namespace BBT.Workflow.Execution.StateStores;

/// <summary>
/// Dapr-backed implementation of <see cref="IStateStoreClient"/>. This is the single place that talks to
/// <see cref="DaprClient"/> for state-store access; the State Store and Cache-Aside invokers compose it.
/// </summary>
public sealed class DaprStateStoreClient : IStateStoreClient
{
    private const string StateStoreNameConfigKey = "DAPR_STATE_STORE_NAME";

    /// <summary>
    /// Fixed namespace prefix applied to every caller-supplied key, keeping task-written entries away
    /// from engine-owned cache keys in the shared store.
    /// </summary>
    public const string KeyPrefix = "custom:";

    private readonly DaprClient _daprClient;
    private readonly string? _defaultStoreName;

    public DaprStateStoreClient(DaprClient daprClient, IConfiguration configuration)
    {
        _daprClient = daprClient;
        _defaultStoreName = configuration[StateStoreNameConfigKey];
    }

    /// <inheritdoc />
    public string? ResolveStoreName(string? storeName) =>
        !string.IsNullOrWhiteSpace(storeName) ? storeName : _defaultStoreName;

    /// <inheritdoc />
    public string PrefixKey(string key) => KeyPrefix + key;

    /// <inheritdoc />
    public async Task<StateEntry> GetAsync(
        string storeName,
        string key,
        string? consistency,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken = default)
    {
        var (value, etag) = await _daprClient.GetStateAndETagAsync<JsonElement>(
            storeName,
            PrefixKey(key),
            ParseConsistency(consistency),
            ToMetadata(metadata),
            cancellationToken);

        var found = value.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);
        return new StateEntry(found, value, string.IsNullOrEmpty(etag) ? null : etag);
    }

    /// <inheritdoc />
    public async Task<bool> SetAsync<TValue>(
        string storeName,
        string key,
        TValue value,
        int? ttlInSeconds,
        string? consistency,
        string? concurrency,
        string? etag,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken = default)
    {
        var operationMetadata = BuildMetadata(metadata, ttlInSeconds);
        var stateOptions = BuildStateOptions(consistency, concurrency);

        if (!string.IsNullOrEmpty(etag))
        {
            return await _daprClient.TrySaveStateAsync(
                storeName, PrefixKey(key), value, etag, stateOptions, operationMetadata, cancellationToken);
        }

        await _daprClient.SaveStateAsync(
            storeName, PrefixKey(key), value, stateOptions, operationMetadata, cancellationToken);
        return true;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(
        string storeName,
        string key,
        string? consistency,
        string? concurrency,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken = default)
    {
        await _daprClient.DeleteStateAsync(
            storeName,
            PrefixKey(key),
            BuildStateOptions(consistency, concurrency),
            ToMetadata(metadata),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> DeleteBulkAsync(
        string storeName,
        IReadOnlyList<string> prefixedKeys,
        CancellationToken cancellationToken = default)
    {
        var items = prefixedKeys
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => new BulkDeleteStateItem(k, etag: string.Empty))
            .ToList();

        if (items.Count == 0)
        {
            return 0;
        }

        await _daprClient.DeleteBulkStateAsync(storeName, items, cancellationToken);
        return items.Count;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> QueryPrefixedKeysAsync(
        string storeName,
        string query,
        IReadOnlyDictionary<string, string>? metadata,
        CancellationToken cancellationToken = default)
    {
        var queryResponse = await _daprClient.QueryStateAsync<JsonElement>(
            storeName, query, ToMetadata(metadata), cancellationToken);

        return queryResponse?.Results?
            .Select(r => r.Key)
            .Where(k => k != null && k.StartsWith(KeyPrefix, StringComparison.Ordinal))
            .ToList() ?? new List<string>();
    }

    private static Dictionary<string, string>? ToMetadata(IReadOnlyDictionary<string, string>? metadata) =>
        metadata is { Count: > 0 } ? new Dictionary<string, string>(metadata) : null;

    private static Dictionary<string, string>? BuildMetadata(
        IReadOnlyDictionary<string, string>? metadata,
        int? ttlInSeconds)
    {
        var result = ToMetadata(metadata);

        if (ttlInSeconds is > 0)
        {
            result ??= new Dictionary<string, string>();
            result["ttlInSeconds"] = ttlInSeconds.Value.ToString();
        }

        return result;
    }

    private static StateOptions? BuildStateOptions(string? consistency, string? concurrency)
    {
        var consistencyMode = ParseConsistency(consistency);
        var concurrencyMode = ParseConcurrency(concurrency);

        if (consistencyMode is null && concurrencyMode is null)
        {
            return null;
        }

        return new StateOptions { Consistency = consistencyMode, Concurrency = concurrencyMode };
    }

    private static ConsistencyMode? ParseConsistency(string? consistency) =>
        consistency?.ToLowerInvariant() switch
        {
            "strong" => ConsistencyMode.Strong,
            "eventual" => ConsistencyMode.Eventual,
            _ => null
        };

    private static ConcurrencyMode? ParseConcurrency(string? concurrency) =>
        concurrency?.ToLowerInvariant() switch
        {
            "firstwrite" => ConcurrencyMode.FirstWrite,
            "lastwrite" => ConcurrencyMode.LastWrite,
            _ => null
        };
}
