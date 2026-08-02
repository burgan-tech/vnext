using BBT.Aether.Results;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting.Related;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Gateway;

/// <summary>
/// Routes related-instance reads: local when the target domain matches this runtime, remote otherwise.
/// Mirrors <see cref="RoutedInstanceQueryGateway"/>, but injects both sides as interfaces (via keyed
/// DI) so the routing decision itself is unit-testable. The only component that knows a dispatch
/// crossed a domain boundary, so it is also the one that logs <c>RelatedInstanceCrossDomainRead</c>.
/// </summary>
public sealed class RoutedRelatedInstanceReader(
    IRuntimeInfoProvider runtimeInfoProvider,
    [FromKeyedServices(RelatedReaderKeys.Local)] IRelatedInstanceReader local,
    [FromKeyedServices(RelatedReaderKeys.Remote)] IRelatedInstanceReader remote,
    ILogger<RoutedRelatedInstanceReader> logger) : IRelatedInstanceReader
{
    private readonly IRuntimeInfoProvider _runtimeInfoProvider = runtimeInfoProvider;
    private readonly IRelatedInstanceReader _local = local;
    private readonly IRelatedInstanceReader _remote = remote;
    private readonly ILogger<RoutedRelatedInstanceReader> _logger = logger;

    /// <inheritdoc />
    public Task<Result<RelatedInstanceSnapshot?>> ReadAsync(
        RelatedInstanceRef reference,
        CancellationToken cancellationToken = default)
    {
        if (_runtimeInfoProvider.IsDomainMatch(reference.Domain))
            return _local.ReadAsync(reference, cancellationToken);

        // The reader only ever sees the target's reference, not the instance whose script triggered
        // this read, so the log identifies the target being read rather than the caller.
        _logger.RelatedInstanceCrossDomainRead(reference.InstanceId, reference.Domain, reference.Flow, 1);
        return _remote.ReadAsync(reference, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<RelatedInstanceSnapshot>>> ReadManyAsync(
        IReadOnlyList<RelatedInstanceRef> references,
        CancellationToken cancellationToken = default)
    {
        var localRefs = references.Where(r => _runtimeInfoProvider.IsDomainMatch(r.Domain)).ToList();
        var remoteRefs = references.Where(r => !_runtimeInfoProvider.IsDomainMatch(r.Domain)).ToList();

        var snapshots = new List<RelatedInstanceSnapshot>(references.Count);

        if (localRefs.Count > 0)
        {
            var localResult = await _local.ReadManyAsync(localRefs, cancellationToken);
            if (!localResult.IsSuccess)
                return Result<IReadOnlyList<RelatedInstanceSnapshot>>.Fail(localResult.Error);

            snapshots.AddRange(localResult.Value!);
        }

        if (remoteRefs.Count > 0)
        {
            // A remote batch can span more than one target domain/flow — e.g. SubsAsync(null) pulls
            // every correlation regardless of subflow key — so log once per (domain, flow) group rather
            // than attributing the whole batch to whichever ref happened to be first. That would point
            // an operator at an innocent target, same concern as RelatedInstanceAccessor's own
            // batch-failure log.
            foreach (var group in remoteRefs.GroupBy(r => (r.Domain, r.Flow)))
            {
                var groupCount = group.Count();
                _logger.RelatedInstanceCrossDomainRead(
                    group.First().InstanceId, group.Key.Domain, group.Key.Flow, groupCount);
            }

            var remoteResult = await _remote.ReadManyAsync(remoteRefs, cancellationToken);
            if (!remoteResult.IsSuccess)
                return Result<IReadOnlyList<RelatedInstanceSnapshot>>.Fail(remoteResult.Error);

            snapshots.AddRange(remoteResult.Value!);
        }

        return Result<IReadOnlyList<RelatedInstanceSnapshot>>.Ok(snapshots);
    }
}
