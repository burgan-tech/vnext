using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;

namespace BBT.Workflow.Scripting.Related;

/// <summary>
/// Reads related instances with the engine's own system identity: no query-role check, no x-roles
/// field filtering, no extensions, no data-function response cache. Implemented by a routed gateway
/// that reads locally when the target domain matches the runtime and calls an internal endpoint
/// otherwise.
/// </summary>
/// <remarks>
/// A successful <see cref="Result{T}"/> carrying null means "not found" and is normal. A failed Result
/// means an infrastructure problem and is converted into
/// <see cref="RelatedInstanceAccessException"/> by the accessor.
/// </remarks>
public interface IRelatedInstanceReader
{
    /// <summary>Reads a single related instance.</summary>
    Task<Result<RelatedInstanceSnapshot?>> ReadAsync(
        RelatedInstanceRef reference,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads several related instances, grouping by domain so each domain is contacted once.
    /// References that resolve to nothing are omitted from the result rather than reported as errors.
    /// </summary>
    Task<Result<IReadOnlyList<RelatedInstanceSnapshot>>> ReadManyAsync(
        IReadOnlyList<RelatedInstanceRef> references,
        CancellationToken cancellationToken = default);
}
