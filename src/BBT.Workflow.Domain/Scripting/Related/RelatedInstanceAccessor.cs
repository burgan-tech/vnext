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

    private readonly Instance _instance;
    private readonly IRelatedInstanceReader _reader;
    private readonly IInstanceCorrelationRepository _correlationRepository;
    private readonly RelatedAccessOptions _options;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<Guid, RelatedInstanceView> _memo;
    private readonly RelatedInstanceRef? _parentRef;

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
    public Task<IReadOnlyList<string>> SubKeysAsync(CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Implemented in Task 4.");

    /// <inheritdoc />
    public Task<RelatedInstanceView?> SubAsync(string subFlowKey, CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Implemented in Task 4.");

    /// <inheritdoc />
    public Task<IReadOnlyList<RelatedInstanceView>> SubsAsync(
        string? subFlowKey = null,
        CancellationToken cancellationToken = default) =>
        throw new NotImplementedException("Implemented in Task 4.");

    private async Task<RelatedInstanceView?> ResolveAsync(
        RelatedInstanceRef reference,
        string direction,
        InstanceCorrelation? correlation,
        CancellationToken cancellationToken)
    {
        if (_memo.TryGetValue(reference.InstanceId, out var cached))
            return cached;

        EnsureUnderLimit();

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

    private void EnsureUnderLimit()
    {
        if (_memo.Count < _options.MaxResolutionsPerContext)
            return;

        _logger.RelatedInstanceResolutionLimitExceeded(_instance.Id, _options.MaxResolutionsPerContext);
        throw new RelatedInstanceAccessException(
            $"Related instance resolution limit of {_options.MaxResolutionsPerContext} exceeded for instance {_instance.Id}. " +
            "Reduce the number of distinct related instances a single script resolves, or raise " +
            $"{RelatedAccessOptions.SectionName}:MaxResolutionsPerContext.");
    }

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
