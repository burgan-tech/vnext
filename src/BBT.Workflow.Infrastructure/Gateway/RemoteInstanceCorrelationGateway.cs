using System.Text;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Discovery;
using BBT.Workflow.Instances;
using BBT.Workflow.Remote;
using BBT.Workflow.Remote.Configuration;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Gateway;

/// <summary>
/// Hands one correlation hop to the domain that owns it, over the internal correlation batch
/// endpoint. The far side recurses locally and answers with a finished subtree, so a branch that
/// crosses a domain boundary costs ONE call for that whole branch rather than one per level.
/// </summary>
/// <remarks>
/// Modelled on <see cref="RemoteHumanTaskLeafGateway"/> for endpoint resolution, transport usage
/// and error mapping. Unlike that gateway it carries no caller roles or headers, because this walk
/// authorizes nothing anywhere along it — the function it serves has no gate, and adding one here
/// only would make the local and remote halves of one tree disagree.
/// <para>
/// A failure is returned as a <see cref="Result"/>, never thrown: the caller turns it into an
/// unresolved node so one unreachable partner truncates its own branch instead of failing the
/// whole tree.
/// </para>
/// </remarks>
public sealed class RemoteInstanceCorrelationGateway(
    IRemoteTransport<RemoteInstanceCorrelationGateway> transport,
    IOptions<RemoteOptions> options,
    IDomainDiscoveryResolver endpointResolver) : IInstanceCorrelationGateway
{
    private readonly RemoteOptions _options = options.Value;

    private string ApiVersionPrefix => InstanceUrlTemplates.GetApiVersionPrefix(_options.ApiVersion);

    /// <inheritdoc />
    public async Task<Result<IReadOnlyList<CorrelationBatchResult>>> ResolveAsync(
        string domain,
        string flow,
        CorrelationBatchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.InstanceIds.Count == 0)
        {
            return Result<IReadOnlyList<CorrelationBatchResult>>.Ok([]);
        }

        var endpointResult = await endpointResolver.GetEndpointAsync(
            domain, EndpointKind.Url, cancellationToken);

        if (!endpointResult.IsSuccess)
        {
            return Result<IReadOnlyList<CorrelationBatchResult>>.Fail(endpointResult.Error);
        }

        var relativePath = InstanceUrlTemplates.CorrelationBatch(domain, flow, ApiVersionPrefix);
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
                return Result<IReadOnlyList<CorrelationBatchResult>>.Fail(error);
            }

            var responseContent = await response.ReadDecompressedContentAsync(cancellationToken);
            var results = JsonSerializer.Deserialize<List<CorrelationBatchResult>>(
                responseContent, JsonSerializerConstants.JsonOptions) ?? [];

            return Result<IReadOnlyList<CorrelationBatchResult>>.Ok(results);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller itself went away — propagate rather than reporting a hop failure.
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return Result<IReadOnlyList<CorrelationBatchResult>>.Fail(
                Error.Transient("remote_network_error", exception.Message));
        }
    }
}
