using System.Net;
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

namespace BBT.Workflow.Authorization.Remote;

/// <summary>
/// Remote implementation of authorize operations using HTTP client calls to the instance authorize function endpoint.
/// GET {baseUrl}/api/v{version}/{domain}/workflows/{workflow}/instances/{instance}/functions/authorize
/// Uses IDomainDiscoveryResolver to resolve endpoint by target domain.
/// </summary>
public sealed class RemoteAuthorizeAppService(
    HttpClient httpClient,
    IOptions<RemoteOptions> options,
    IDomainDiscoveryResolver endpointResolver,
    ICurrentUser currentUser)
    : IRemoteAuthorizeAppService
{
    private readonly RemoteOptions _options = options.Value;

    private string ApiVersionPrefix => InstanceUrlTemplates.GetApiVersionPrefix(_options.ApiVersion);

    /// <inheritdoc />
    public async Task<Result<AuthorizeOutput>> GetAuthorizeResultForInstanceAsync(
        string domain,
        string workflow,
        string instanceId,
        string role,
        string? transitionKey,
        string? functionKey,
        string? version,
        bool checkQueryRoles,
        AuthorizationRequestContext? requestContext = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var endpointResult = await endpointResolver.GetEndpointAsync(domain, EndpointKind.Url, cancellationToken);
            if (!endpointResult.IsSuccess)
                return Result<AuthorizeOutput>.Fail(endpointResult.Error);

            var endpoint = endpointResult.Value!;
            var relativePath = InstanceUrlTemplates.Authorize(domain, workflow, instanceId, ApiVersionPrefix);

            var queryParams = new List<string>();
            if (!string.IsNullOrEmpty(role))
                queryParams.Add($"role={Uri.EscapeDataString(role)}");
            if (!string.IsNullOrEmpty(transitionKey))
                queryParams.Add($"transitionKey={Uri.EscapeDataString(transitionKey)}");
            if (!string.IsNullOrEmpty(functionKey))
                queryParams.Add($"functionKey={Uri.EscapeDataString(functionKey)}");
            if (!string.IsNullOrEmpty(version))
                queryParams.Add($"version={Uri.EscapeDataString(version)}");
            if (checkQueryRoles)
                queryParams.Add("queryRoles=true");

            if (queryParams.Count > 0)
                relativePath += "?" + string.Join("&", queryParams);

            var requestUri = new Uri(endpoint.BaseUrl, relativePath.TrimStart('/'));
            using var requestMessage = new HttpRequestMessage(HttpMethod.Get, requestUri);
            var forwardHeaders = currentUser.ToForwardHeaders();
            CurrentUserForwardHeadersHelper.MergeIntoRequest(requestMessage, forwardHeaders, requestContext?.Headers);
            var response = await httpClient.SendAsync(requestMessage, cancellationToken);

            // 200 and 403 both return AuthorizeOutput body (allowed true/false)
            if (response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.Forbidden)
            {
                var responseContent = await response.ReadDecompressedContentAsync(cancellationToken);
                var output = JsonSerializer.Deserialize<AuthorizeOutput>(responseContent, JsonSerializerConstants.JsonOptions);
                return Result<AuthorizeOutput>.Ok(output ?? new AuthorizeOutput { Allowed = false });
            }

            var error = await RemoteHttpResponseHelper.MapToErrorAsync(response, cancellationToken, JsonSerializerConstants.JsonOptions);
            return Result<AuthorizeOutput>.Fail(error);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return Result<AuthorizeOutput>.Fail(Error.Transient("remote_network_error", ex.Message));
        }
    }

    /// <inheritdoc />
    public async Task<Result<AuthorizationMatrixOutput>> GetAuthorizationMatrixForInstanceAsync(
        string domain,
        string workflow,
        string instanceId,
        string? version,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var endpointResult = await endpointResolver.GetEndpointAsync(domain, EndpointKind.Url, cancellationToken);
            if (!endpointResult.IsSuccess)
                return Result<AuthorizationMatrixOutput>.Fail(endpointResult.Error);

            var endpoint = endpointResult.Value!;
            var relativePath = InstanceUrlTemplates.Permissions(domain, workflow, instanceId, ApiVersionPrefix);

            if (!string.IsNullOrEmpty(version))
                relativePath += "?version=" + Uri.EscapeDataString(version);

            var requestUri = new Uri(endpoint.BaseUrl, relativePath.TrimStart('/'));
            using var requestMessage = new HttpRequestMessage(HttpMethod.Get, requestUri);
            var forwardHeaders = currentUser.ToForwardHeaders();
            CurrentUserForwardHeadersHelper.MergeIntoRequest(requestMessage, forwardHeaders, null);
            var response = await httpClient.SendAsync(requestMessage, cancellationToken);

            if (response.StatusCode == HttpStatusCode.OK)
            {
                var responseContent = await response.ReadDecompressedContentAsync(cancellationToken);
                var output = JsonSerializer.Deserialize<AuthorizationMatrixOutput>(responseContent, JsonSerializerConstants.JsonOptions);
                return Result<AuthorizationMatrixOutput>.Ok(output!);
            }

            var error = await RemoteHttpResponseHelper.MapToErrorAsync(response, cancellationToken, JsonSerializerConstants.JsonOptions);
            return Result<AuthorizationMatrixOutput>.Fail(error);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return Result<AuthorizationMatrixOutput>.Fail(Error.Transient("remote_network_error", ex.Message));
        }
    }

}
