using System.Text;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Discovery;
using BBT.Workflow.Instances.Related;
using BBT.Workflow.Remote;
using BBT.Workflow.Remote.Configuration;
using BBT.Workflow.Scripting.Related;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Gateway;

/// <summary>
/// Reads related instances that live in another domain, over the internal related-data endpoints
/// (<c>InstanceController.GetRelatedDataAsync</c> / <c>GetRelatedDataBatchAsync</c>). Modeled on
/// <see cref="BBT.Workflow.Instances.Remote.RemoteInstanceQueryAppService"/> for endpoint resolution,
/// <see cref="HttpClient"/> usage and error mapping. Unlike that service, this reader sends no caller
/// identity and forwards no headers — related-instance access is system-identity by design.
/// </summary>
public sealed class RemoteRelatedInstanceReader(
    HttpClient httpClient,
    IOptions<RemoteOptions> options,
    IDomainDiscoveryResolver endpointResolver) : IRelatedInstanceReader
{
    private readonly RemoteOptions _options = options.Value;

    private string ApiVersionPrefix => InstanceUrlTemplates.GetApiVersionPrefix(_options.ApiVersion);

    /// <inheritdoc />
    public async Task<Result<RelatedInstanceSnapshot?>> ReadAsync(
        RelatedInstanceRef reference,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);

        var endpointResult = await endpointResolver.GetEndpointAsync(
            reference.Domain, EndpointKind.Url, cancellationToken);

        if (!endpointResult.IsSuccess)
            return Result<RelatedInstanceSnapshot?>.Fail(endpointResult.Error);

        var endpoint = endpointResult.Value!;

        var relativePath = InstanceUrlTemplates.RelatedData(
            reference.Domain, reference.Flow, reference.InstanceId.ToString(), ApiVersionPrefix);

        if (!string.IsNullOrWhiteSpace(reference.FlowVersion))
            relativePath += $"?version={Uri.EscapeDataString(reference.FlowVersion)}";

        var requestUri = new Uri(endpoint.BaseUrl, relativePath.TrimStart('/'));

        try
        {
            var response = await httpClient.GetAsync(requestUri, cancellationToken);

            // AetherControllerBase.FromResult maps a successful read of a nonexistent instance to 204
            // No Content — absence, not an error. A 404 here means the route or the target app id is
            // wrong (an infrastructure fault), so it must NOT be treated as absence: it falls through to
            // the failure branch below like any other non-success status.
            if (response.StatusCode == System.Net.HttpStatusCode.NoContent)
                return Result<RelatedInstanceSnapshot?>.Ok(null);

            if (!response.IsSuccessStatusCode)
            {
                var error = await RemoteHttpResponseHelper.MapToErrorAsync(
                    response, cancellationToken, JsonSerializerConstants.JsonOptions);
                return Result<RelatedInstanceSnapshot?>.Fail(error);
            }

            var responseContent = await response.ReadDecompressedContentAsync(cancellationToken);
            var snapshot = JsonSerializer.Deserialize<RelatedInstanceSnapshot>(
                responseContent, JsonSerializerConstants.JsonOptions);

            return Result<RelatedInstanceSnapshot?>.Ok(Normalize(snapshot, reference));
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // Network errors → Transient error (per Railway Pattern), matching RemoteInstanceQueryAppService.
            return Result<RelatedInstanceSnapshot?>.Fail(Error.Transient("remote_network_error", exception.Message));
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

        var snapshots = new List<RelatedInstanceSnapshot>(references.Count);

        // One HTTP call per (Domain, Flow, FlowVersion) group — the batch endpoint is routed by domain
        // and workflow, so ids belonging to different flows cannot share a request.
        foreach (var group in references.GroupBy(reference => (reference.Domain, reference.Flow, reference.FlowVersion)))
        {
            var groupResult = await ReadGroupAsync(group.Key, [.. group], cancellationToken);

            // All-or-nothing: a failing group fails the whole call. Returning only the groups that
            // succeeded would let a script see e.g. four of five children and read that as "there are
            // four" — silently treating an infrastructure fault as absence, which must never happen.
            if (!groupResult.IsSuccess)
                return Result<IReadOnlyList<RelatedInstanceSnapshot>>.Fail(groupResult.Error);

            snapshots.AddRange(groupResult.Value!);
        }

        return Result<IReadOnlyList<RelatedInstanceSnapshot>>.Ok(snapshots);
    }

    private async Task<Result<IReadOnlyList<RelatedInstanceSnapshot>>> ReadGroupAsync(
        (string Domain, string Flow, string? FlowVersion) key,
        IReadOnlyList<RelatedInstanceRef> group,
        CancellationToken cancellationToken)
    {
        var endpointResult = await endpointResolver.GetEndpointAsync(key.Domain, EndpointKind.Url, cancellationToken);
        if (!endpointResult.IsSuccess)
            return Result<IReadOnlyList<RelatedInstanceSnapshot>>.Fail(endpointResult.Error);

        var endpoint = endpointResult.Value!;

        var relativePath = InstanceUrlTemplates.RelatedDataBatch(key.Domain, key.Flow, ApiVersionPrefix);
        if (!string.IsNullOrWhiteSpace(key.FlowVersion))
            relativePath += $"?version={Uri.EscapeDataString(key.FlowVersion)}";

        var requestUri = new Uri(endpoint.BaseUrl, relativePath.TrimStart('/'));

        var requestBody = new RelatedDataBatchInput
        {
            InstanceIds = group.Select(reference => reference.InstanceId).ToList()
        };

        var jsonContent = JsonSerializer.Serialize(requestBody, JsonSerializerConstants.JsonOptions);
        var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        var requestMessage = new HttpRequestMessage(HttpMethod.Post, requestUri)
        {
            Content = content
        };

        try
        {
            var response = await httpClient.SendAsync(requestMessage, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await RemoteHttpResponseHelper.MapToErrorAsync(
                    response, cancellationToken, JsonSerializerConstants.JsonOptions);
                return Result<IReadOnlyList<RelatedInstanceSnapshot>>.Fail(error);
            }

            var responseContent = await response.ReadDecompressedContentAsync(cancellationToken);
            var wireSnapshots = JsonSerializer.Deserialize<List<RelatedInstanceSnapshot>>(
                responseContent, JsonSerializerConstants.JsonOptions) ?? [];

            var byId = group.ToDictionary(reference => reference.InstanceId);
            var normalized = wireSnapshots
                .Where(snapshot => byId.ContainsKey(snapshot.InstanceId))
                .Select(snapshot => Normalize(snapshot, byId[snapshot.InstanceId])!)
                .ToList();

            return Result<IReadOnlyList<RelatedInstanceSnapshot>>.Ok(normalized);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return Result<IReadOnlyList<RelatedInstanceSnapshot>>.Fail(
                Error.Transient("remote_network_error", exception.Message));
        }
    }

    /// <summary>
    /// Projects the wire snapshot into the shape the accessor expects. The reference — not the wire
    /// payload — is authoritative for <see cref="RelatedInstanceSnapshot.Domain"/>,
    /// <see cref="RelatedInstanceSnapshot.Flow"/> and <see cref="RelatedInstanceSnapshot.FlowVersion"/>:
    /// the remote instance aggregate does not carry a domain at all (the schema/runtime does), and the
    /// reference is what the caller resolved the instance by, so it is the trustworthy source.
    /// </summary>
    /// <remarks>
    /// <see cref="RelatedInstanceSnapshot.Data"/> is declared <c>dynamic?</c>, which compiles to a plain
    /// <c>object</c> field. System.Text.Json has no converter that intercepts a plain <c>object</c>
    /// target, so deserializing the wire payload leaves <see cref="JsonElement"/> boxed in that slot —
    /// registering <c>ExpandoObjectJsonConverter</c> in <see cref="JsonSerializerConstants"/> does not
    /// change this, because converter selection is based on the declared CLR type, not the runtime
    /// value. The local reader never hits this: <c>Instance.Data</c> already returns an
    /// <c>ExpandoObject</c> (via <c>InstanceData.Attributes</c> → <c>JsonElement.ToDynamic()</c>).
    /// Left unconverted, a script's <c>context.Related.ParentAsync().Data.SomeField</c> would work for a
    /// same-domain parent and throw a <see cref="Microsoft.CSharp.RuntimeBinder.RuntimeBinderException"/>
    /// (JsonElement has no such member) for a cross-domain one — identical mapping code behaving
    /// differently depending on where the related instance happens to live. Converting here keeps the
    /// two paths identical from the script's point of view.
    /// </remarks>
    private static RelatedInstanceSnapshot? Normalize(RelatedInstanceSnapshot? snapshot, RelatedInstanceRef reference)
    {
        if (snapshot == null)
            return null;

        object? data = snapshot.Data is JsonElement element ? element.ToDynamic() : snapshot.Data;

        return new RelatedInstanceSnapshot
        {
            InstanceId = snapshot.InstanceId == Guid.Empty ? reference.InstanceId : snapshot.InstanceId,
            Key = snapshot.Key,
            Domain = reference.Domain,
            Flow = reference.Flow,
            FlowVersion = reference.FlowVersion,
            Status = snapshot.Status,
            CurrentState = snapshot.CurrentState,
            IsCompleted = snapshot.IsCompleted,
            Data = data
        };
    }
}
