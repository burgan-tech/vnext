using System;
using System.Text;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow;
using BBT.Workflow.Definitions;
using BBT.Workflow.Discovery;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.Remote;
using BBT.Aether.Users;
using BBT.Workflow.CurrentUser;
using BBT.Workflow.Remote;
using BBT.Workflow.Remote.Configuration;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Infrastructure.Instances.Remote;

/// <summary>
/// Remote implementation of instance retry operations using HTTP client calls to InstanceController.
/// Uses IDomainDiscoveryResolver to dynamically resolve endpoint URLs based on target domain.
/// </summary>
public sealed class RemoteInstanceRetryAppService(
    HttpClient httpClient,
    IOptions<RemoteOptions> options,
    IDomainDiscoveryResolver endpointResolver,
    ICurrentUser currentUser)
    : IRemoteInstanceRetryAppService
{
    private readonly RemoteOptions _options = options.Value;

    private string ApiVersionPrefix => InstanceUrlTemplates.GetApiVersionPrefix(_options.ApiVersion);

    /// <summary>
    /// Retries a faulted workflow instance by calling the remote API
    /// POST {baseUrl}/api/v{version}/{domain}/workflows/{workflow}/instances/{instance}/retry
    /// </summary>
    public async Task<Result<RetryInstanceOutput>> RetryAsync(
        RetryInstanceInput input,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Resolve endpoint dynamically based on target domain
            var endpointResult = await endpointResolver.GetEndpointAsync(input.Domain, EndpointKind.Url, cancellationToken);

            if (!endpointResult.IsSuccess)
            {
                return Result<RetryInstanceOutput>.Fail(endpointResult.Error);
            }

            var endpoint = endpointResult.Value!;

            var relativePath = InstanceUrlTemplates.Retry(input.Domain, input.Workflow, input.Instance, ApiVersionPrefix);

            var queryParams = new List<string>();
            if (input.Sync)
                queryParams.Add("sync=true");

            if (queryParams.Count > 0)
                relativePath += "?" + string.Join("&", queryParams);

            var requestUri = new Uri(endpoint.BaseUrl, relativePath.TrimStart('/'));

            // Build request body from data if provided
            var content = input.Data != null
                ? new StringContent(JsonSerializer.Serialize(input.Data, JsonSerializerConstants.JsonOptions), Encoding.UTF8, "application/json")
                : new StringContent("{}", Encoding.UTF8, "application/json");

            var requestMessage = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = content
            };

            var forwardHeaders = currentUser.ToForwardHeaders();
            CurrentUserForwardHeadersHelper.MergeIntoRequest(requestMessage, forwardHeaders, input.Headers, RemoteHttpResponseHelper.IsRestrictedHeader);

            var response = await httpClient.SendAsync(requestMessage, cancellationToken);

            // Status code → Result.Fail (per Railway Pattern)
            return await HandleResponseAsync<RetryInstanceOutput>(response, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            // Network errors → Transient error (per Railway Pattern)
            return Result<RetryInstanceOutput>.Fail(Error.Transient("remote_network_error", ex.Message));
        }
    }

    /// <summary>
    /// Handles HTTP response by mapping status codes to appropriate Result types.
    /// Follows Railway Pattern: Status code → Result.Fail (not exceptions).
    /// </summary>
    private static async Task<Result<T>> HandleResponseAsync<T>(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            var responseContent = await response.ReadDecompressedContentAsync(cancellationToken);
            var result = JsonSerializer.Deserialize<T>(responseContent, JsonSerializerConstants.JsonOptions);
            return Result<T>.Ok(result!);
        }

        var error = await RemoteHttpResponseHelper.MapToErrorAsync(response, cancellationToken, JsonSerializerConstants.JsonOptions);
        return Result<T>.Fail(error);
    }
}
