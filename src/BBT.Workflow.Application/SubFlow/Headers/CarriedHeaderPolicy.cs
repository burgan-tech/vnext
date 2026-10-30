using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.SubFlow.Headers;

/// <summary>
/// Default <see cref="ICarriedHeaderPolicy"/>: a code-enforced deny floor and framework exclusions,
/// plus the configured additions and caps.
/// </summary>
public sealed class CarriedHeaderPolicy(
    IOptions<SubFlowCarriedHeadersOptions> options,
    ILogger<CarriedHeaderPolicy> logger) : ICarriedHeaderPolicy
{
    private static readonly string[] DeniedSubstrings = ["authorization", "cookie", "token", "secret", "session"];

    private static readonly HashSet<string> ExcludedNames = new(StringComparer.Ordinal)
    {
        "x-subflow-depth", "x-parent-instance-id", "x-root-instance-id", "x-workflow-instance-id",
        "x-correlation-id", "traceparent", "tracestate", "baggage", "host",
        "transfer-encoding", "connection", "keep-alive"
    };

    private static readonly string[] ExcludedPrefixes = ["content-", "dapr-", "grpc-"];

    private readonly SubFlowCarriedHeadersOptions _options = options.Value;

    /// <inheritdoc />
    public bool ExposeToOutputMapping => _options.ExposeToOutputMapping;

    /// <inheritdoc />
    public bool ApplyToResume => _options.ApplyToResume;

    /// <inheritdoc />
    public Dictionary<string, string?>? Capture(IReadOnlyDictionary<string, string?>? headers)
    {
        if (!_options.Emit)
        {
            return null;
        }

        var result = Filter(headers);
        return result.Count == 0 ? null : result;
    }

    /// <inheritdoc />
    public Dictionary<string, string?> Apply(IReadOnlyDictionary<string, string?>? carried) => Filter(carried);

    private Dictionary<string, string?> Filter(IReadOnlyDictionary<string, string?>? source)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (source is null || source.Count == 0)
        {
            return result;
        }

        var additional = _options.AdditionalDenied;
        int denied = 0, overflow = 0;
        foreach (var (key, value) in source)
        {
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            var lower = key.ToLowerInvariant();
            if (IsDenied(lower, additional) || IsExcluded(lower) || (value?.Length ?? 0) > _options.MaxValueLength)
            {
                denied++;
                continue;
            }

            if (result.Count >= _options.MaxEntries)
            {
                overflow++;
                continue;
            }

            result[key] = value;
        }

        if (denied > 0 || overflow > 0)
        {
            logger.SubFlowCarriedHeadersFiltered(result.Count, denied, overflow);
        }

        return result;
    }

    private static bool IsDenied(string lower, string[] additional)
    {
        foreach (var s in DeniedSubstrings)
        {
            if (lower.Contains(s, StringComparison.Ordinal))
            {
                return true;
            }
        }

        foreach (var name in additional)
        {
            if (string.Equals(name, lower, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsExcluded(string lower)
    {
        if (ExcludedNames.Contains(lower))
        {
            return true;
        }

        foreach (var p in ExcludedPrefixes)
        {
            if (lower.StartsWith(p, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
