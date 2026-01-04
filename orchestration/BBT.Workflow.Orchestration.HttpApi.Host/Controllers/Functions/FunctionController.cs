using BBT.Aether;
using BBT.Aether.Application.Dtos;
using BBT.Aether.AspNetCore.Controllers;
using BBT.Aether.AspNetCore.Pagination;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Domain.Shared;
using BBT.Workflow.Functions;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.DTOs;
using BBT.Workflow.Runtime;
using GetExtensionsInput = BBT.Workflow.Instances.DTOs.GetExtensionsInput;
using GetExtensionsOutput = BBT.Workflow.Instances.DTOs.GetExtensionsOutput;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace BBT.Workflow.Controllers.Instances;

/// <summary>
/// Controller for handling workflow function operations
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/v{version:apiVersion}")]
[ServiceFilter(typeof(ResponseHeaderFilter))]
public sealed class FunctionController(
    IFunctionAppService functionAppService,
    IInstanceQueryAppService queryAppService,
    IPaginationLinkGenerator linkGenerator) : AetherControllerBase
{
    [HttpPatch("{domain}/functions")]
    public async Task<IActionResult> GetDomainFunctionsAsync(
        [FromRoute] string domain,
        CancellationToken cancellationToken = default)
    {
        var response = await functionAppService.GetDomainFunctionsAsync(domain, cancellationToken);
        return FromResult(response);
    }

    [HttpGet("{domain}/functions/{function}")]
    public async Task<IActionResult> GetFunctionByKeyAsync(
        [FromRoute] string domain,
        [FromRoute] string function,
        [FromQuery] FunctionListQueryParameters parameters,
        CancellationToken cancellationToken = default)
    {
        var requestContext = HttpContext.GetRequestBindingContext();

        var response = await functionAppService.GetFunctionByFunctionKeyAsync(
            function,
            RuntimeSysSchemaInfo.Functions,
            domain,
            requestContext.Headers,
            requestContext.QueryParameters,
            cancellationToken);
        return FromResult(response);
    }

    [HttpGet("{domain}/workflows/{workflow}/functions/{function}")]
    public async Task<IActionResult> GetFunctionByKeyAsync(
        [FromRoute] string domain,
        [FromRoute] string function,
        [FromRoute] string workflow,
        [FromQuery] FunctionListQueryParameters parameters,
        CancellationToken cancellationToken = default)
    {
        var requestContext = HttpContext.GetRequestBindingContext();

        var getInstanceListInput = new GetInstanceListInput
        {
            Domain = domain,
            Page = parameters.Page,
            PageSize = parameters.PageSize,
            PageUrl = InstanceUrlTemplates.FunctionList(domain, workflow, function),
            Sort = parameters.Sort,
            Workflow = workflow,
            Filter = function.ToLowerInvariant() == Definitions.Functions.FunctionTypeConst.Data
                ? parameters.Filter
                : [],
            Headers = requestContext.Headers,
            QueryParameters = requestContext.QueryParameters
        };

        var instanceListResult = await queryAppService.GetInstanceListAsync(getInstanceListInput, cancellationToken);

        if (!instanceListResult.IsSuccess)
        {
            return FromResult(instanceListResult);
        }

        // Process based on function type
        var functionType = function.ToLowerInvariant();

        if (functionType == Definitions.Functions.FunctionTypeConst.Longpooling)
        {
            return FromResult(await ProcessLongpoolingFunctionListAsync(domain, workflow, instanceListResult.Value!,
                cancellationToken));
        }

        if (functionType == Definitions.Functions.FunctionTypeConst.View)
        {
            return FromResult(await ProcessViewFunctionListAsync(domain, workflow, instanceListResult.Value!,
                cancellationToken));
        }

        if (functionType == Definitions.Functions.FunctionTypeConst.Data)
        {
            return FromResult(await ProcessDataFunctionListAsync(domain, workflow, instanceListResult.Value!,
                requestContext.Headers, requestContext.QueryParameters, cancellationToken));
        }

        if (functionType == Definitions.Functions.FunctionTypeConst.Schema)
        {
            return FromResult(await ProcessSchemaFunctionListAsync(domain, workflow, instanceListResult.Value!,
                cancellationToken));
        }

        if (functionType == Definitions.Functions.FunctionTypeConst.Extensions)
        {
            return FromResult(await ProcessExtensionsFunctionListAsync(domain, workflow, instanceListResult.Value!,
                requestContext.Headers, requestContext.QueryParameters, cancellationToken));
        }

        return FromResult(await ProcessCustomFunctionListAsync(function, workflow, domain, instanceListResult.Value!,
            requestContext.Headers, requestContext.QueryParameters, cancellationToken));
    }

    [HttpGet("{domain}/workflows/{workflow}/instances/{instance}/functions/{function}")]
    public async Task<IActionResult> GetFunctionWithInstanceAsync(
        [FromRoute] string domain,
        [FromRoute] string function,
        [FromRoute] string workflow,
        [FromRoute] string instance,
        [FromQuery] FunctionQueryParameters parameters,
        [FromHeader(Name = "If-None-Match")] string? ifNoneMatch,
        CancellationToken cancellationToken = default)
    {
        var requestContext = HttpContext.GetRequestBindingContext();

        var functionType = function.ToLowerInvariant();

        if (functionType == Definitions.Functions.FunctionTypeConst.Longpooling)
        {
            return FromResult(await ProcessLongpoolingFunctionAsync(domain, workflow, instance, parameters.Version,
                parameters.Extensions, cancellationToken));
        }

        if (functionType == Definitions.Functions.FunctionTypeConst.View)
        {
            return FromResult(await ProcessViewFunctionAsync(domain, workflow, instance, parameters.Version,
                parameters.Platform, parameters.TransitionKey, cancellationToken));
        }

        if (functionType == Definitions.Functions.FunctionTypeConst.Data)
        {
            var dataResult = await ProcessDataFunctionAsync(
                domain, workflow, instance, ifNoneMatch,
                parameters.Extensions, requestContext.Headers, requestContext.QueryParameters, cancellationToken);

            if (dataResult.IsNotModified)
            {
                return StatusCode(304);
            }

            return FromResult(dataResult.Result);
        }
        
        if (functionType == Definitions.Functions.FunctionTypeConst.Schema)
        {
            return FromResult(await ProcessSchemaFunctionAsync(domain, workflow, instance, parameters.Version,
                parameters.TransitionKey, cancellationToken));
        }

        if (functionType == Definitions.Functions.FunctionTypeConst.Extensions)
        {
            return FromResult(await ProcessExtensionsFunctionAsync(domain, workflow, instance, parameters.Version,
                parameters.Extensions, requestContext.Headers, requestContext.QueryParameters, cancellationToken));
        }

        return FromResult(await functionAppService.GetFunctionByInstanceAsync(function, workflow, domain, instance,
            requestContext.Headers, requestContext.QueryParameters, cancellationToken));
    }

    #region Private Helper Methods

    private async Task<Result<GetInstanceStateOutput>> ProcessLongpoolingFunctionAsync(
        string domain,
        string workflow,
        string instance,
        string? version,
        string[]? extensions,
        CancellationToken cancellationToken)
    {
        var input = new GetInstanceStateInput
        {
            Domain = domain,
            Workflow = workflow,
            Instance = instance,
            Version = version,
            Extensions = extensions
        };
        return await queryAppService.GetInstanceStateAsync(input, cancellationToken);
    }

    private async Task<Result<HateoasPagedResultDto<GetInstanceStateOutput>>> ProcessLongpoolingFunctionListAsync(
        string domain,
        string workflow,
        HateoasPagedList<GetInstanceOutput> instanceListResult,
        CancellationToken cancellationToken)
    {
        var tasks = instanceListResult.Items.Select(instance =>
            ProcessLongpoolingFunctionAsync(domain, workflow, instance.Key!, instance.FlowVersion, null, cancellationToken));

        var results = await Task.WhenAll(tasks);
        var list = results.Where(r => r.IsSuccess).Select(r => r.Value!).ToList();

        var route = InstanceUrlTemplates.FunctionList(domain, workflow,
            Definitions.Functions.FunctionTypeConst.Longpooling, InstanceUrlTemplates.GetApiVersionPrefix("1"));
        var output = linkGenerator.CreateHateoasResult(instanceListResult, list, route);

        return Result<HateoasPagedResultDto<GetInstanceStateOutput>>.Ok(output);
    }

    private async Task<Result<GetViewOutput>> ProcessViewFunctionAsync(
        string domain,
        string workflow,
        string instance,
        string? version,
        string? platform,
        string? transitionKey,
        CancellationToken cancellationToken)
    {
        var input = new GetViewInput
        {
            Domain = domain,
            Workflow = workflow,
            Instance = instance,
            Version = version
        };

        return await queryAppService.GetPlatformSpecificViewAsync(
            input,
            platform ?? string.Empty,
            transitionKey ?? string.Empty,
            cancellationToken);
    }

    private async Task<Result<GetSchemaOutput>> ProcessSchemaFunctionAsync(
        string domain,
        string workflow,
        string instance,
        string? version,
        string? transitionKey,
        CancellationToken cancellationToken)
    {
        var input = new GetSchemaInput
        {
            Domain = domain,
            Workflow = workflow,
            Instance = instance,
            Version = version
        };

        return await queryAppService.GetSchemaAsync(input, transitionKey, cancellationToken);
    }

    private async Task<Result<GetExtensionsOutput>> ProcessExtensionsFunctionAsync(
        string domain,
        string workflow,
        string instance,
        string? version,
        string[]? extensions,
        Dictionary<string, string?> headers,
        Dictionary<string, string?> queryParams,
        CancellationToken cancellationToken)
    {
        var input = new GetExtensionsInput
        {
            Domain = domain,
            Workflow = workflow,
            Instance = instance,
            Version = version,
            Extensions = extensions,
            Headers = headers,
            QueryParameters = queryParams
        };

        return await queryAppService.GetExtensionsAsync(input, cancellationToken);
    }

    private async Task<Result<HateoasPagedResultDto<GetExtensionsOutput>>> ProcessExtensionsFunctionListAsync(
        string domain,
        string workflow,
        HateoasPagedList<GetInstanceOutput> instanceListResult,
        Dictionary<string, string?> headers,
        Dictionary<string, string?> queryParams,
        CancellationToken cancellationToken)
    {
        var tasks = instanceListResult.Items.Select(instance =>
            ProcessExtensionsFunctionAsync(domain, workflow, instance.Key!, instance.FlowVersion, null, headers, queryParams, cancellationToken));

        var results = await Task.WhenAll(tasks);
        var list = results.Where(r => r.IsSuccess).Select(r => r.Value!).ToList();

        var route = InstanceUrlTemplates.FunctionList(domain, workflow,
            Definitions.Functions.FunctionTypeConst.Extensions, InstanceUrlTemplates.GetApiVersionPrefix("1"));
        var output = linkGenerator.CreateHateoasResult(instanceListResult, list, route);

        return Result<HateoasPagedResultDto<GetExtensionsOutput>>.Ok(output);
    }

    private async Task<Result<HateoasPagedResultDto<GetSchemaOutput>>> ProcessSchemaFunctionListAsync(
        string domain,
        string workflow,
        HateoasPagedList<GetInstanceOutput> instanceListResult,
        CancellationToken cancellationToken)
    {
        var tasks = instanceListResult.Items.Select(instance =>
            ProcessSchemaFunctionAsync(domain, workflow, instance.Key!, instance.FlowVersion, string.Empty, cancellationToken));

        var results = await Task.WhenAll(tasks);
        var list = results.Where(r => r.IsSuccess).Select(r => r.Value!).ToList();

        var route = InstanceUrlTemplates.FunctionList(domain, workflow,
            Definitions.Functions.FunctionTypeConst.Schema, InstanceUrlTemplates.GetApiVersionPrefix("1"));
        var output = linkGenerator.CreateHateoasResult(instanceListResult, list, route);

        return Result<HateoasPagedResultDto<GetSchemaOutput>>.Ok(output);
    }

    private async Task<Result<HateoasPagedResultDto<GetViewOutput>>> ProcessViewFunctionListAsync(
        string domain,
        string workflow,
        HateoasPagedList<GetInstanceOutput> instanceListResult,
        CancellationToken cancellationToken)
    {
        var tasks = instanceListResult.Items.Select(instance =>
            ProcessViewFunctionAsync(domain, workflow, instance.Key!, instance.FlowVersion, string.Empty, string.Empty, cancellationToken));

        var results = await Task.WhenAll(tasks);
        var list = results.Where(r => r.IsSuccess).Select(r => r.Value!).ToList();

        var route = InstanceUrlTemplates.FunctionList(domain, workflow,
            Definitions.Functions.FunctionTypeConst.View, InstanceUrlTemplates.GetApiVersionPrefix("1"));
        var output = linkGenerator.CreateHateoasResult(instanceListResult, list, route);

        return Result<HateoasPagedResultDto<GetViewOutput>>.Ok(output);
    }

    private async Task<ConditionalResult<GetInstanceDataOutput>> ProcessDataFunctionAsync(
        string domain,
        string workflow,
        string instance,
        string? ifNoneMatch,
        string[]? extensions,
        Dictionary<string, string?> headers,
        Dictionary<string, string?> queryParams,
        CancellationToken cancellationToken)
    {
        var input = new GetInstanceDataInput
        {
            Domain = domain,
            Workflow = workflow,
            Instance = instance,
            IfNoneMatch = ifNoneMatch,
            Extensions = extensions,
            Headers = headers,
            QueryParameters = queryParams
        };

        var response = await queryAppService.GetInstanceDataAsync(input, cancellationToken);

        if (response.Result.IsSuccess && !string.IsNullOrEmpty(response.Result.Value?.Etag))
        {
            HttpContext.Response.Headers[HeadersConstants.ETag] = response.Result.Value.Etag;
        }

        return response;
    }

    private async Task<Result<HateoasPagedResultDto<GetInstanceDataOutput>>> ProcessDataFunctionListAsync(
        string domain,
        string workflow,
        HateoasPagedList<GetInstanceOutput> instanceListResult,
        Dictionary<string, string?> headers,
        Dictionary<string, string?> queryParams,
        CancellationToken cancellationToken)
    {
        var tasks = instanceListResult.Items.Select(instance =>
        {
            var input = new GetInstanceDataInput
            {
                Domain = domain,
                Workflow = workflow,
                Instance = instance.Key!,
                Headers = headers,
                QueryParameters = queryParams
            };
            return queryAppService.GetInstanceDataAsync(input, cancellationToken);
        });

        var results = await Task.WhenAll(tasks);
        var list = results.Where(r => r.Result.IsSuccess).Select(r => r.Result.Value!).ToList();

        var route = InstanceUrlTemplates.FunctionList(domain, workflow,
            Definitions.Functions.FunctionTypeConst.Data, InstanceUrlTemplates.GetApiVersionPrefix("1"));
        var output = linkGenerator.CreateHateoasResult(instanceListResult, list, route);
        return Result<HateoasPagedResultDto<GetInstanceDataOutput>>.Ok(output);
    }

    private async Task<Result<HateoasPagedResultDto<Dictionary<string, dynamic?>>>> ProcessCustomFunctionListAsync(
        string function,
        string workflow,
        string domain,
        HateoasPagedList<GetInstanceOutput> instanceListResult,
        Dictionary<string, string?> headers,
        Dictionary<string, string?> queryParams,
        CancellationToken cancellationToken)
    {
        var tasks = instanceListResult.Items.Select(instance =>
            functionAppService.GetFunctionByInstanceAsync(function, workflow, domain, instance.Key!, headers, queryParams, cancellationToken));

        var results = await Task.WhenAll(tasks);
        var list = results.Where(r => r.IsSuccess).Select(r => r.Value!).ToList();

        var route = InstanceUrlTemplates.FunctionList(domain, workflow, function,
            InstanceUrlTemplates.GetApiVersionPrefix("1"));
        var output = linkGenerator.CreateHateoasResult(instanceListResult, list, route);
        return Result<HateoasPagedResultDto<Dictionary<string, dynamic?>>>.Ok(output);
    }

    #endregion
}