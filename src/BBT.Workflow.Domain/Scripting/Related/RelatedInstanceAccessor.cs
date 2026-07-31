using System.Collections.Concurrent;
using System.Text.Json;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Scripting.Related;

/// <summary>
/// Default <see cref="IRelatedInstanceAccessor"/>. Derives the parent reference from the instance's
/// <c>parent.*</c> metadata and child references from the correlation repository, delegates reads to
/// <see cref="IRelatedInstanceReader"/>, memoizes results for the lifetime of the owning ScriptContext,
/// and caps how many distinct related instances one context may resolve.
/// </summary>
public sealed class RelatedInstanceAccessor : IRelatedInstanceAccessor
{
    private const string DirectionParent = "parent";
    private const string DirectionSub = "sub";

    private readonly Instance _instance;
    private readonly IRelatedInstanceReader _reader;
    private readonly IInstanceCorrelationRepository _correlationRepository;
    private readonly RelatedAccessOptions _options;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<Guid, RelatedInstanceView> _memo;
    private readonly RelatedInstanceRef? _parentRef;

    private readonly SemaphoreSlim _correlationGate = new(1, 1);
    private List<InstanceCorrelation>? _correlations;

    /// <summary>Creates an accessor bound to one instance snapshot.</summary>
    public RelatedInstanceAccessor(
        Instance instance,
        IRelatedInstanceReader reader,
        IInstanceCorrelationRepository correlationRepository,
        RelatedAccessOptions options,
        ILogger logger)
        : this(instance, reader, correlationRepository, options, logger, new ConcurrentDictionary<Guid, RelatedInstanceView>())
    {
    }

    private RelatedInstanceAccessor(
        Instance instance,
        IRelatedInstanceReader reader,
        IInstanceCorrelationRepository correlationRepository,
        RelatedAccessOptions options,
        ILogger logger,
        ConcurrentDictionary<Guid, RelatedInstanceView> memo)
    {
        _instance = instance ?? throw new ArgumentNullException(nameof(instance));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _correlationRepository = correlationRepository ?? throw new ArgumentNullException(nameof(correlationRepository));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _memo = memo;
        _parentRef = BuildParentRef(instance);
    }

    /// <inheritdoc />
    public bool HasParent => _parentRef != null;

    /// <inheritdoc />
    public async Task<RelatedInstanceView?> ParentAsync(CancellationToken cancellationToken = default)
    {
        if (_parentRef == null)
        {
            _logger.RelatedInstanceNotFound(_instance.Id, DirectionParent, null);
            return null;
        }

        return await ResolveAsync(_parentRef, DirectionParent, correlation: null, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> SubKeysAsync(CancellationToken cancellationToken = default)
    {
        var correlations = await GetCorrelationsAsync(cancellationToken);
        return correlations
            .Select(correlation => correlation.SubFlowName)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<RelatedInstanceView?> SubAsync(
        string subFlowKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subFlowKey);

        var correlations = await GetCorrelationsAsync(cancellationToken);
        var correlation = correlations
            .Where(candidate => string.Equals(candidate.SubFlowName, subFlowKey, StringComparison.Ordinal))
            .OrderByDescending(candidate => candidate.CreatedAt)
            .FirstOrDefault();

        if (correlation == null)
        {
            _logger.RelatedInstanceNotFound(_instance.Id, DirectionSub, subFlowKey);
            return null;
        }

        return await ResolveAsync(ToRef(correlation), DirectionSub, correlation, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RelatedInstanceView>> SubsAsync(
        string? subFlowKey = null,
        CancellationToken cancellationToken = default)
    {
        var correlations = await GetCorrelationsAsync(cancellationToken);
        var matches = correlations
            .Where(candidate => subFlowKey == null ||
                                string.Equals(candidate.SubFlowName, subFlowKey, StringComparison.Ordinal))
            .OrderBy(candidate => candidate.CreatedAt)
            .ToList();

        if (matches.Count == 0)
        {
            _logger.RelatedInstanceNotFound(_instance.Id, DirectionSub, subFlowKey);
            return [];
        }

        return await ResolveManyAsync(matches, cancellationToken);
    }

    /// <summary>
    /// Creates an accessor for a parallel task branch. The branch is bound to its own instance
    /// snapshot but shares this accessor's memo and reader, so a related instance already resolved by
    /// the coordinator is not read again. Safe because branches only read.
    /// </summary>
    /// <param name="branchInstance">The branch's instance snapshot.</param>
    public RelatedInstanceAccessor ForBranch(Instance branchInstance) =>
        new(branchInstance, _reader, _correlationRepository, _options, _logger, _memo);

    /// <summary>
    /// Drops every memoized related instance. Called when the owning ScriptContext is disposed so the
    /// resolved data does not outlive the transition.
    /// </summary>
    public void ClearMemo()
    {
        _memo.Clear();
        _correlations = null;
    }

    private async Task<RelatedInstanceView?> ResolveAsync(
        RelatedInstanceRef reference,
        string direction,
        InstanceCorrelation? correlation,
        CancellationToken cancellationToken)
    {
        if (_memo.TryGetValue(reference.InstanceId, out var cached))
            return cached;

        EnsureUnderLimit(1);

        var result = await _reader.ReadAsync(reference, cancellationToken);
        if (!result.IsSuccess)
        {
            var reason = result.Error.Message ?? "unknown";
            _logger.RelatedInstanceResolutionFailed(
                _instance.Id, direction, reference.InstanceId, reference.Domain, reference.Flow, reason);
            throw new RelatedInstanceAccessException(
                $"Failed to read related instance {reference.InstanceId} ({direction}): {reason}");
        }

        var snapshot = result.Value;
        if (snapshot == null)
        {
            _logger.RelatedInstanceNotFound(_instance.Id, direction, reference.Flow);
            return null;
        }

        var view = ToView(snapshot, correlation);
        _memo[reference.InstanceId] = view;
        _logger.RelatedInstanceResolved(
            _instance.Id, direction, snapshot.InstanceId, snapshot.Domain, snapshot.Flow);
        return view;
    }

    private void EnsureUnderLimit(int additional)
    {
        if (_memo.Count + additional <= _options.MaxResolutionsPerContext)
            return;

        _logger.RelatedInstanceResolutionLimitExceeded(_instance.Id, _options.MaxResolutionsPerContext);
        throw new RelatedInstanceAccessException(
            $"Related instance resolution limit of {_options.MaxResolutionsPerContext} exceeded for instance {_instance.Id}. " +
            $"Attempted to add {additional} more. " +
            "Reduce the number of distinct related instances a single script resolves, or raise " +
            $"{RelatedAccessOptions.SectionName}:MaxResolutionsPerContext.");
    }

    private async Task<IReadOnlyList<RelatedInstanceView>> ResolveManyAsync(
        List<InstanceCorrelation> correlations,
        CancellationToken cancellationToken)
    {
        var pending = correlations
            .Where(correlation => !_memo.ContainsKey(correlation.SubFlowInstanceId))
            .ToList();

        if (pending.Count > 0)
        {
            EnsureUnderLimit(pending.Count);

            var references = pending.Select(ToRef).ToList();
            var result = await _reader.ReadManyAsync(references, cancellationToken);
            if (!result.IsSuccess)
            {
                var reason = result.Error.Message ?? "unknown";
                // Batch-shaped log: a batch can span several domains, so naming any single
                // correlation's domain would point an operator at an innocent target.
                var domains = string.Join(
                    ", ",
                    pending.Select(correlation => correlation.SubFlowDomain).Distinct(StringComparer.Ordinal));
                _logger.RelatedInstanceBatchResolutionFailed(_instance.Id, pending.Count, domains, reason);
                throw new RelatedInstanceAccessException(
                    $"Failed to read {pending.Count} related instance(s) of {_instance.Id} " +
                    $"in domain(s) {domains}: {reason}");
            }

            var byId = result.Value!.ToDictionary(snapshot => snapshot.InstanceId);
            foreach (var correlation in pending)
            {
                if (!byId.TryGetValue(correlation.SubFlowInstanceId, out var snapshot))
                {
                    _logger.RelatedInstanceNotFound(_instance.Id, DirectionSub, correlation.SubFlowName);
                    continue;
                }

                _memo[correlation.SubFlowInstanceId] = ToView(snapshot, correlation);
                _logger.RelatedInstanceResolved(
                    _instance.Id, DirectionSub, snapshot.InstanceId, snapshot.Domain, snapshot.Flow);
            }
        }

        return correlations
            .Where(correlation => _memo.ContainsKey(correlation.SubFlowInstanceId))
            .Select(correlation => _memo[correlation.SubFlowInstanceId])
            .ToList();
    }

    private async Task<List<InstanceCorrelation>> GetCorrelationsAsync(CancellationToken cancellationToken)
    {
        if (_correlations != null)
            return _correlations;

        await _correlationGate.WaitAsync(cancellationToken);
        try
        {
            // Deliberately not Instance.ChildCorrelations: the repository's default include filters
            // out completed correlations, and completed subflow output must stay readable.
            _correlations ??= await _correlationRepository.GetByParentAsync(_instance.Id, cancellationToken);
            return _correlations;
        }
        finally
        {
            _correlationGate.Release();
        }
    }

    private static RelatedInstanceRef ToRef(InstanceCorrelation correlation) =>
        new(
            correlation.SubFlowInstanceId,
            correlation.SubFlowDomain,
            correlation.SubFlowName,
            correlation.SubFlowVersion);

    private static RelatedInstanceView ToView(RelatedInstanceSnapshot snapshot, InstanceCorrelation? correlation) =>
        new()
        {
            InstanceId = snapshot.InstanceId,
            Key = snapshot.Key,
            Domain = snapshot.Domain,
            Flow = snapshot.Flow,
            FlowVersion = snapshot.FlowVersion,
            Status = snapshot.Status,
            CurrentState = snapshot.CurrentState,
            IsCompleted = snapshot.IsCompleted,
            CorrelationCompleted = correlation?.IsCompleted,
            TerminalOutcome = correlation?.TerminalOutcome?.ToString(),
            SubFlowType = correlation?.SubFlowType.Code,
            Data = snapshot.Data
        };

    private static RelatedInstanceRef? BuildParentRef(Instance instance)
    {
        var id = ReadGuid(instance, DomainConsts.MetaDataKeys.Id);
        if (id == null)
            return null;

        var domain = ReadString(instance, DomainConsts.MetaDataKeys.Domain);
        var flow = ReadString(instance, DomainConsts.MetaDataKeys.Flow);
        if (string.IsNullOrWhiteSpace(domain) || string.IsNullOrWhiteSpace(flow))
            return null;

        return new RelatedInstanceRef(
            id.Value,
            domain!,
            flow!,
            ReadString(instance, DomainConsts.MetaDataKeys.Version));
    }

    /// <summary>
    /// Reads a Guid from instance metadata. The same slot can hold a Guid (freshly written by
    /// SubflowStarter), a string, or a JsonElement (after a database round-trip).
    /// </summary>
    private static Guid? ReadGuid(Instance instance, string key)
    {
        if (!instance.ExtraProperties.TryGetValue(key, out var raw) || raw == null)
            return null;

        return raw switch
        {
            Guid guid => guid == Guid.Empty ? null : guid,
            string text => Guid.TryParse(text, out var parsed) ? parsed : null,
            JsonElement element when element.ValueKind == JsonValueKind.String =>
                element.TryGetGuid(out var fromJson) ? fromJson : null,
            _ => Guid.TryParse(raw.ToString(), out var fallback) ? fallback : null
        };
    }

    /// <summary>
    /// Reads a string from instance metadata. Fails closed like <see cref="ReadGuid"/>: an unexpected
    /// stored type yields null rather than a fabricated <c>ToString()</c> value, which would otherwise
    /// pass the non-empty check in <see cref="BuildParentRef"/> and produce a reference to a domain or
    /// flow that never existed.
    /// </summary>
    private static string? ReadString(Instance instance, string key)
    {
        if (!instance.ExtraProperties.TryGetValue(key, out var raw) || raw == null)
            return null;

        return raw switch
        {
            string text => string.IsNullOrWhiteSpace(text) ? null : text,
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
            _ => null
        };
    }
}
