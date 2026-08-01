using BBT.Aether.Results;
using BBT.Workflow.Instances.Related;
using BBT.Workflow.Scripting.Related;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Workflow.Gateway;

/// <summary>
/// Reads related instances that live in this runtime's domain. Establishes the schema scope with
/// <c>ExecuteWithWorkflowAsync</c> — the same pattern <see cref="LocalInstanceQueryGateway"/> uses.
/// </summary>
/// <remarks>
/// The fresh scope isolates the <see cref="IServiceProvider"/>, not the database transaction: Aether's
/// ambient unit of work keys DbContexts by (type, schema) and enlists a newly opened one in the
/// surrounding transition's connection and transaction. A related read therefore observes the current
/// transition's uncommitted writes. That is intended — within one transition a script should see the
/// engine's own in-flight state rather than a stale snapshot — but it is not the isolation the scope
/// creation might suggest.
/// </remarks>
public sealed class LocalRelatedInstanceReader(IServiceScopeFactory serviceScopeFactory) : IRelatedInstanceReader
{
    /// <inheritdoc />
    public Task<Result<RelatedInstanceSnapshot?>> ReadAsync(
        RelatedInstanceRef reference,
        CancellationToken cancellationToken = default) =>
        serviceScopeFactory.ExecuteWithWorkflowAsync(
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
            var groupResult = await serviceScopeFactory.ExecuteWithWorkflowAsync(
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
