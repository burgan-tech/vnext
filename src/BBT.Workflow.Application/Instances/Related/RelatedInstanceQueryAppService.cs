using BBT.Aether.Results;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting.Related;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Instances.Related;

/// <summary>
/// Reads related instances from the current schema with no authorization filtering.
/// Registered per-scope and always invoked inside an established schema scope by
/// <c>LocalRelatedInstanceReader</c>.
/// </summary>
public sealed class RelatedInstanceQueryAppService(
    IInstanceRepository instanceRepository,
    ILogger<RelatedInstanceQueryAppService> logger) : IRelatedInstanceQueryAppService
{
    /// <inheritdoc />
    public async Task<Result<RelatedInstanceSnapshot?>> ReadAsync(
        RelatedInstanceRef reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);

        try
        {
            var instance = await instanceRepository.FindByIdentifierAsReadOnlyAsync(
                reference.InstanceId.ToString(), cancellationToken);

            return Result<RelatedInstanceSnapshot?>.Ok(
                instance == null ? null : ToSnapshot(instance, reference));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller itself went away — propagate rather than reporting a read failure. Matches
            // WorkflowOutputMappingService and the background job handlers.
            throw;
        }
        catch (Exception exception)
        {
            // WorkflowLogs extension, not raw logger.LogError — the coding standard forbids the latter.
            logger.RelatedInstanceReadFailed(exception, reference.InstanceId, reference.Flow);

            return Result<RelatedInstanceSnapshot?>.Fail(Error.Failure(
                WorkflowErrorCodes.RelatedInstanceReadFailed,
                $"Related instance read failed for {reference.InstanceId}: {exception.Message}",
                detail: exception.GetType().Name));
        }
    }

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<RelatedInstanceSnapshot>>> ReadManyAsync(
        IReadOnlyList<RelatedInstanceRef> references,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(references);

        if (references.Count == 0)
            return Result<IReadOnlyList<RelatedInstanceSnapshot>>.Ok([]);

        try
        {
            var instances = await instanceRepository.FindByIdsAsReadOnlyAsync(
                references.Select(reference => reference.InstanceId).ToList(),
                cancellationToken);

            var byId = instances.ToDictionary(instance => instance.Id);

            // Reference order preserved; references with no matching row are omitted — absence is
            // data. Only a thrown exception fails the batch.
            var snapshots = references
                .Where(reference => byId.ContainsKey(reference.InstanceId))
                .Select(reference => ToSnapshot(byId[reference.InstanceId], reference))
                .ToList();

            return Result<IReadOnlyList<RelatedInstanceSnapshot>>.Ok(snapshots);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.RelatedInstanceReadFailed(exception, references[0].InstanceId, references[0].Flow);

            return Result<IReadOnlyList<RelatedInstanceSnapshot>>.Fail(Error.Failure(
                WorkflowErrorCodes.RelatedInstanceReadFailed,
                $"Related instance batch read of {references.Count} instance(s) failed: {exception.Message}",
                detail: exception.GetType().Name));
        }
    }

    /// <summary>
    /// Projects the aggregate into the wire/read shape. <paramref name="reference"/> supplies the
    /// domain: the <see cref="Instance"/> aggregate does not carry one (the schema and runtime do), and
    /// the reference is what the caller resolved the instance by, so it is authoritative.
    /// </summary>
    private static RelatedInstanceSnapshot ToSnapshot(Instance instance, RelatedInstanceRef reference) => new()
    {
        InstanceId = instance.Id,
        Key = instance.Key,
        Domain = reference.Domain,
        Flow = instance.Flow,
        FlowVersion = instance.FlowVersion,
        Status = instance.Status.Code,
        CurrentState = instance.CurrentState,
        IsCompleted = instance.IsCompleted,
        Data = instance.Data
    };
}
