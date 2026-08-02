using BBT.Aether.Results;
using BBT.Workflow.Scripting.Related;

namespace BBT.Workflow.Instances.Related;

/// <summary>
/// Local, system-identity read of another instance's latest data, for related-instance access from
/// mapping scripts. Deliberately bypasses the query-role check, x-roles field filtering, extensions
/// and the data-function response cache that <see cref="IInstanceQueryAppService"/> applies — the read
/// happens inside the engine's own correlation frame, not on behalf of a caller.
/// </summary>
public interface IRelatedInstanceQueryAppService
{
    /// <summary>Reads one instance. A successful result carrying null means the instance was not found.</summary>
    Task<Result<RelatedInstanceSnapshot?>> ReadAsync(
        RelatedInstanceRef reference,
        CancellationToken cancellationToken = default);

    /// <summary>Reads several instances in the current schema, omitting the ones that do not exist.</summary>
    Task<Result<IReadOnlyList<RelatedInstanceSnapshot>>> ReadManyAsync(
        IReadOnlyList<RelatedInstanceRef> references,
        CancellationToken cancellationToken = default);
}
