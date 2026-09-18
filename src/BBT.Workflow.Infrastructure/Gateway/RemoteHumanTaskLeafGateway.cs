using System.Text;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Discovery;
using BBT.Workflow.Instances.HumanTask;
using BBT.Workflow.Remote;
using BBT.Workflow.Remote.Configuration;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Gateway;

/// <summary>
/// Hands one level of a human-task descent to the domain that owns it, over the internal
/// human-task-leaf endpoint. The far side recurses locally and answers with a finished result, so
/// a chain that crosses a domain boundary costs ONE call for that whole branch rather than one per
/// level per instance.
/// </summary>
/// <remarks>
/// Modeled on <see cref="RemoteRelatedInstanceReader"/> for endpoint resolution, transport usage
/// and error mapping. Unlike that reader it does carry caller roles and the headers backing
/// <c>$.context.Headers.*</c>, because authorization has to happen where the leaf's workflow
/// definition can be resolved — which is only in the owning domain. That is the same posture the
/// endpoint itself has: internal-only, no authorization of its own, protected by network isolation.
/// It does not make the public surface weaker (that route already accepts a self-asserted caller
/// identity) but it is not an improvement to it either.
/// </remarks>
public sealed class RemoteHumanTaskLeafGateway(
    IRemoteTransport<RemoteHumanTaskLeafGateway> transport,
    IOptions<RemoteOptions> options,
    IDomainDiscoveryResolver endpointResolver) : IHumanTaskLeafGateway
{
    private readonly RemoteOptions _options = options.Value;

    private string ApiVersionPrefix => InstanceUrlTemplates.GetApiVersionPrefix(_options.ApiVersion);

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<HumanTaskLeafResult>>> ResolveAsync(
        string domain,
        string flow,
        HumanTaskLeafRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.InstanceIds.Count == 0)
            return Result<IReadOnlyList<HumanTaskLeafResult>>.Ok([]);

        var endpointResult = await endpointResolver.GetEndpointAsync(
            domain, EndpointKind.Url, cancellationToken);

        if (!endpointResult.IsSuccess)
            return Result<IReadOnlyList<HumanTaskLeafResult>>.Fail(endpointResult.Error);

        var relativePath = InstanceUrlTemplates.HumanTaskLeafBatch(domain, flow, ApiVersionPrefix);
        var jsonContent = JsonSerializer.Serialize(request, JsonSerializerConstants.JsonOptions);

        try
        {
            var response = await transport.SendAsync(
                endpointResult.Value!,
                HttpMethod.Post,
                relativePath,
                requestMessage =>
                {
                    requestMessage.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
                },
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var error = await RemoteHttpResponseHelper.MapToErrorAsync(
                    response, cancellationToken, JsonSerializerConstants.JsonOptions);
                return Result<IReadOnlyList<HumanTaskLeafResult>>.Fail(error);
            }

            var responseContent = await response.ReadDecompressedContentAsync(cancellationToken);
            var results = JsonSerializer.Deserialize<List<HumanTaskLeafResult>>(
                responseContent, JsonSerializerConstants.JsonOptions) ?? [];

            return Result<IReadOnlyList<HumanTaskLeafResult>>.Ok(results);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller itself went away — propagate rather than reporting a hop failure.
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return Result<IReadOnlyList<HumanTaskLeafResult>>.Fail(
                Error.Transient("remote_network_error", exception.Message));
        }
    }
}
