using BBT.Aether.Results;
using BBT.Workflow.Instances.Related;
using BBT.Workflow.Scripting.Related;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Workflow.Gateway;

/// <summary>
/// Reads related instances that live in this runtime's domain. Establishes the schema scope with
/// <c>ExecuteReadWithWorkflowAsync</c> — the same pattern <see cref="LocalInstanceQueryGateway"/> uses.
/// </summary>
/// <remarks>
/// Each read gets its own unit of work, not only its own DI scope: Aether keys a DbContext by
/// (unit of work, type, schema), and the scripts of same-order tasks run as parallel branches that would
/// otherwise share one context per schema and fail with "A second operation was started on this context
/// instance". Visibility is unchanged: the transition's unit of work is non-transactional and these reads
/// are no-tracking, so they read the same saved rows on their own context as they did on the shared one.
/// </remarks>
public sealed class LocalRelatedInstanceReader(IServiceScopeFactory serviceScopeFactory) : IRelatedInstanceReader
{
    /// <inheritdoc />
    public Task<Result<RelatedInstanceSnapshot?>> ReadAsync(
        RelatedInstanceRef reference,
        CancellationToken cancellationToken = default) =>
        serviceScopeFactory.ExecuteReadWithWorkflowAsync(
            reference.Domain,
            reference.Flow,
            reference.FlowVersion,
            async (serviceProvider, ct) =>
            {
                var service = serviceProvider.GetRequiredService<IRelatedInstanceQueryAppService>();
                return await service.ReadAsync(reference, ct);
            },
            cancellationToken);

    /// <inheritdoc />
    public Task<Result<IReadOnlyList<RelatedInstanceSnapshot>>> ReadManyAsync(
        IReadOnlyList<RelatedInstanceRef> references,
        CancellationToken cancellationToken = default)
    {
        if (references.Count == 0)
            return Task.FromResult(Result<IReadOnlyList<RelatedInstanceSnapshot>>.Ok(
                (IReadOnlyList<RelatedInstanceSnapshot>)[]));

        return ReadManyCoreAsync(references, cancellationToken);
    }

    private async Task<Result<IReadOnlyList<RelatedInstanceSnapshot>>> ReadManyCoreAsync(
        IReadOnlyList<RelatedInstanceRef> references,
        CancellationToken cancellationToken)
    {
        var snapshots = new List<RelatedInstanceSnapshot>(references.Count);

        // One schema scope per flow: different flows resolve to different schemas.
        foreach (var group in references.GroupBy(reference => (reference.Flow, reference.FlowVersion)))
        {
            var groupRefs = group.ToList();
            var groupResult = await serviceScopeFactory.ExecuteReadWithWorkflowAsync(
                groupRefs[0].Domain,
                group.Key.Flow,
                group.Key.FlowVersion,
                async (serviceProvider, ct) =>
                {
                    var service = serviceProvider.GetRequiredService<IRelatedInstanceQueryAppService>();
                    return await service.ReadManyAsync(groupRefs, ct);
                },
                cancellationToken);

            if (!groupResult.IsSuccess)
                return Result<IReadOnlyList<RelatedInstanceSnapshot>>.Fail(groupResult.Error);

            snapshots.AddRange(groupResult.Value!);
        }

        return Result<IReadOnlyList<RelatedInstanceSnapshot>>.Ok(snapshots);
    }
}
