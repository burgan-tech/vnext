using BBT.Aether.Results;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting.Related;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Workflow.Gateway;

/// <summary>
/// Routes related-instance reads: local when the target domain matches this runtime, remote otherwise.
/// Mirrors <see cref="RoutedInstanceQueryGateway"/>, but injects both sides as interfaces (via keyed
/// DI) so the routing decision itself is unit-testable.
/// </summary>
public sealed class RoutedRelatedInstanceReader(
    IRuntimeInfoProvider runtimeInfoProvider,
    [FromKeyedServices(RelatedReaderKeys.Local)] IRelatedInstanceReader local,
    [FromKeyedServices(RelatedReaderKeys.Remote)] IRelatedInstanceReader remote) : IRelatedInstanceReader
{
    private readonly IRuntimeInfoProvider _runtimeInfoProvider = runtimeInfoProvider;
    private readonly IRelatedInstanceReader _local = local;
    private readonly IRelatedInstanceReader _remote = remote;

    /// <inheritdoc />
    public Task<Result<RelatedInstanceSnapshot?>> ReadAsync(
        RelatedInstanceRef reference,
        CancellationToken cancellationToken = default) =>
        _runtimeInfoProvider.IsDomainMatch(reference.Domain)
            ? _local.ReadAsync(reference, cancellationToken)
            : _remote.ReadAsync(reference, cancellationToken);

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
            var remoteResult = await _remote.ReadManyAsync(remoteRefs, cancellationToken);
            if (!remoteResult.IsSuccess)
                return Result<IReadOnlyList<RelatedInstanceSnapshot>>.Fail(remoteResult.Error);

            snapshots.AddRange(remoteResult.Value!);
        }

        return Result<IReadOnlyList<RelatedInstanceSnapshot>>.Ok(snapshots);
    }
}
