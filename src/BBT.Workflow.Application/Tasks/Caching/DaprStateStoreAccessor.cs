using System.Text.Json;
using Dapr.Client;
using Microsoft.Extensions.Configuration;

namespace BBT.Workflow.Tasks.Caching;

/// <summary>
/// Dapr-backed implementation of <see cref="IStateStoreAccessor"/>.
/// Mirrors the get/set semantics of the State Store task invoker (shared <c>custom:</c> prefix,
/// TTL metadata, consistency/concurrency, optimistic ETag writes) so a cache-aside task can read and
/// write the same physical entries a State Store task would, without an extra service-invocation hop.
/// </summary>
public sealed class DaprStateStoreAccessor : IStateStoreAccessor
{
    private const string StateStoreNameConfigKey = "DAPR_STATE_STORE_NAME";

    /// <summary>
    /// Fixed namespace prefix applied to every caller-supplied key.
    /// </summary>
    private const string KeyPrefix = "custom:";

    private readonly DaprClient _daprClient;
    private readonly string? _defaultStoreName;

    public DaprStateStoreAccessor(DaprClient daprClient, IConfiguration configuration)
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
    public async Task<StateGetResult> GetAsync(
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
            metadata is { Count: > 0 } ? new Dictionary<string, string>(metadata) : null,
            cancellationToken);

        var found = value.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);
        return new StateGetResult(found, value, string.IsNullOrEmpty(etag) ? null : etag);
    }

    /// <inheritdoc />
    public async Task<StateSetResult> SetAsync(
        string storeName,
        string key,
        JsonElement value,
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
            var saved = await _daprClient.TrySaveStateAsync(
                storeName,
                PrefixKey(key),
                value,
                etag,
                stateOptions,
                operationMetadata,
                cancellationToken);
            return new StateSetResult(saved);
        }

        await _daprClient.SaveStateAsync(
            storeName,
            PrefixKey(key),
            value,
            stateOptions,
            operationMetadata,
            cancellationToken);
        return new StateSetResult(true);
    }

    /// <summary>
    /// Merges caller metadata with the Dapr <c>ttlInSeconds</c> entry. A null or non-positive TTL means
    /// no expiry (the metadata entry is omitted).
    /// </summary>
    private static Dictionary<string, string>? BuildMetadata(
        IReadOnlyDictionary<string, string>? metadata,
        int? ttlInSeconds)
    {
        Dictionary<string, string>? result = metadata is { Count: > 0 }
            ? new Dictionary<string, string>(metadata)
            : null;

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

        return new StateOptions
        {
            Consistency = consistencyMode,
            Concurrency = concurrencyMode
        };
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
