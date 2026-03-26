using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Gateway;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Instances;

/// <summary>
/// Resolves view content by domain (local cache or remote GetInstanceAsync) and maps to GetViewOutput.
/// Centralizes view-by-reference resolution and model-based mapping for maintainability and extensibility.
/// </summary>
public sealed class ViewContentResolutionService(
    IComponentCacheStore componentCacheStore,
    IInstanceQueryGateway instanceQueryGateway,
    ILogger<ViewContentResolutionService> logger) : IViewContentResolutionService
{
    private static readonly JsonSerializerOptions AttributesJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <inheritdoc />
    public async Task<Result<GetViewOutput>> ResolveViewContentAsync(
        IReference viewRef,
        string requestDomain,
        Dictionary<string, string?>? headers,
        Dictionary<string, string?>? queryParameters,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(viewRef.Domain, requestDomain, StringComparison.OrdinalIgnoreCase))
        {
            return await ResolveLocalAsync(viewRef, cancellationToken);
        }

        return await ResolveRemoteAsync(viewRef, requestDomain, headers, queryParameters, cancellationToken);
    }

    private async Task<Result<GetViewOutput>> ResolveLocalAsync(
        IReference viewRef,
        CancellationToken cancellationToken)
    {
        var viewResult = await componentCacheStore.GetViewAsync(
            viewRef.Domain,
            viewRef.Key,
            viewRef.Version,
            cancellationToken);

        return viewResult.Map(view => MapViewToOutput(view));
    }

    private async Task<Result<GetViewOutput>> ResolveRemoteAsync(
        IReference viewRef,
        string requestDomain,
        Dictionary<string, string?>? headers,
        Dictionary<string, string?>? queryParameters,
        CancellationToken cancellationToken)
    {
        logger.ResolvingViewFromRemoteDomain(
            viewRef.Domain,
            viewRef.Flow,
            viewRef.Key,
            requestDomain);

        var instanceInput = new GetInstanceInput
        {
            Domain = viewRef.Domain,
            Workflow = viewRef.Flow,
            Instance = viewRef.Key,
            Version = viewRef.Version,
            Headers = headers,
            QueryParameters = queryParameters
        };

        var conditionalResult = await instanceQueryGateway.GetInstanceAsync(instanceInput, cancellationToken);

        if (conditionalResult.IsNotModified)
        {
            return Result<GetViewOutput>.Fail(
                Error.NotFound("notmodified", "Remote view instance returned not modified."));
        }

        if (!conditionalResult.Result.IsSuccess)
        {
            return Result<GetViewOutput>.Fail(conditionalResult.Result.Error);
        }

        var instanceOutput = conditionalResult.Result.Value!;
        var viewOutput = MapInstanceOutputToViewOutput(instanceOutput, viewRef.Key);
        return Result<GetViewOutput>.Ok(viewOutput);
    }

    /// <summary>
    /// Maps domain View entity to GetViewOutput (local resolution).
    /// Content is typed by view type (JSON object/array for Json, DeepLink, Http, URN when parseable; otherwise string).
    /// </summary>
    private static GetViewOutput MapViewToOutput(View view)
    {
        var content = view.GetContentAsTyped();
        return new GetViewOutput
        {
            Key = view.Key,
            Content = content,
            Type = view.Type.ToString(),
            Display = view.Display,
            Label = string.Empty
        };
    }

    /// <summary>
    /// Maps remote GetInstanceOutput to GetViewOutput using ViewInstanceAttributesDto (model-based).
    /// View content is in Attributes; deserialized to DTO then mapped.
    /// Content is typed by view type (JSON object/array for Json, DeepLink, Http, URN when parseable; otherwise string), same as local GetContentAsTyped().
    /// </summary>
    private static GetViewOutput MapInstanceOutputToViewOutput(GetInstanceOutput instanceOutput, string viewKey)
    {
        var attrs = DeserializeAttributes(instanceOutput.Attributes);
        var contentRaw = attrs?.Content ?? string.Empty;
        var type = attrs?.Type;
        var viewType = type ?? ViewType.Json;
        var contentTyped = View.GetContentAsTypedFromObject(contentRaw, viewType);
        return new GetViewOutput
        {
            Key = instanceOutput.Key ?? viewKey,
            Content = contentTyped,
            Type = viewType.ToString(),
            Display = attrs?.Display ?? string.Empty,
            Label = attrs?.Label ?? string.Empty
        };
    }

    private static ViewInstanceAttributesDto? DeserializeAttributes(JsonElement? attributes)
    {
        if (!attributes.HasValue || attributes.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        try
        {
            var raw = attributes.Value.GetRawText();
            return JsonSerializer.Deserialize<ViewInstanceAttributesDto>(raw, AttributesJsonOptions);
        }
        catch
        {
            return null;
        }
    }
}