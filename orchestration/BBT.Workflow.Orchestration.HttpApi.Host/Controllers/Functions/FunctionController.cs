using BBT.Aether;
using BBT.Aether.Application.Dtos;
using BBT.Aether.AspNetCore.Controllers;
using BBT.Aether.AspNetCore.Pagination;
using BBT.Aether.Results;
using BBT.Aether.Users;
using BBT.Workflow.Authorization;
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
    IAuthorizeAppService authorizeAppService,
    IInstanceQueryAppService queryAppService,
    ICurrentUser currentUser,
    IPaginationLinkGenerator linkGenerator,
    IServiceScopeFactory serviceScopeFactory,
    IUrlTemplateBuilder urlTemplateBuilder) : AetherControllerBase
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

    /// <summary>
    /// Gets a paged list of workflow instances for the given function (View, Data, Schema, etc.).
    /// For function type Data: filter, sort and orderBy are applied. orderBy wins over sort when both provided.
    /// </summary>
    /// <param name="orderBy">OrderBy JSON (Data function only): {"field":"...","direction":"asc|desc"} or {"fields":[...]}. If provided with sort, orderBy wins.</param>
    [HttpGet("{domain}/workflows/{workflow}/functions/{function}")]
    public async Task<IActionResult> GetFunctionByKeyAsync(
        [FromRoute] string domain,
        [FromRoute] string function,
        [FromRoute] string workflow,
        [FromQuery] FunctionListQueryParameters parameters,
        CancellationToken cancellationToken = default)
    {
        var requestContext = HttpContext.GetRequestBindingContext();
        var functionType = function.ToLowerInvariant();

        if (functionType == Definitions.Functions.FunctionTypeConst.Authorize)
        {
            var role = currentUser.Roles?.Length > 0 ? string.Join(",", currentUser.Roles) : requestContext.QueryParameters.GetValueOrDefault("role", null);
            var transitionKey = requestContext.QueryParameters.GetValueOrDefault("transitionKey", null);
            var functionKey = requestContext.QueryParameters.GetValueOrDefault("functionKey", null);
            var version = requestContext.QueryParameters.GetValueOrDefault("version", null);
            var checkQueryRoles = string.Equals(requestContext.QueryParameters.GetValueOrDefault("queryRoles", null), "true", StringComparison.OrdinalIgnoreCase);
            var result = await authorizeAppService.GetAuthorizeResultAsync(
                domain, workflow, role ?? string.Empty, transitionKey, functionKey, version, checkQueryRoles, cancellationToken);
            return AuthorizeResultToActionResult(result);
        }

        if (functionType == Definitions.Functions.FunctionTypeConst.AuthorizationMatrix)
        {
            var version = requestContext.QueryParameters.GetValueOrDefault("version", null);
            var result =
                await authorizeAppService.GetAuthorizationMatrixAsync(domain, workflow, version, cancellationToken);
            return FromResult(result);
        }


        var getInstanceListInput = new GetInstanceListInput
        {
            Domain = domain,
            Page = parameters.Page,
            PageSize = parameters.PageSize,
            PageUrl = urlTemplateBuilder.BuildFunctionListUrl(domain, workflow, function),
            Sort = parameters.OrderBy ?? parameters.Sort,
            Workflow = workflow,
            Filter =parameters.Filter ,
            Headers = requestContext.Headers,
            QueryParameters = requestContext.QueryParameters
        };

        var instanceListResult = await queryAppService.GetInstanceListAsync(getInstanceListInput, cancellationToken);

        if (!instanceListResult.IsSuccess)
        {
            return FromResult(instanceListResult);
        }

        var pagedList = instanceListResult.Value!.ToPagedList(parameters.PageSize);

        if (functionType == Definitions.Functions.FunctionTypeConst.Longpooling)
        {
            return FromResult(await ProcessLongpoolingFunctionListAsync(domain, workflow, pagedList,
                cancellationToken));
        }

        if (functionType == Definitions.Functions.FunctionTypeConst.View)
        {
            return FromResult(await ProcessViewFunctionListAsync(domain, workflow, pagedList,
                cancellationToken));
        }

        if (functionType == Definitions.Functions.FunctionTypeConst.Data)
        {
            return FromResult(await ProcessDataFunctionListAsync(domain, workflow, pagedList,
                requestContext.Headers, requestContext.QueryParameters, cancellationToken));
        }

        if (functionType == Definitions.Functions.FunctionTypeConst.Schema)
        {
            return FromResult(await ProcessSchemaFunctionListAsync(domain, workflow, pagedList,
                cancellationToken));
        }

        if (functionType == Definitions.Functions.FunctionTypeConst.Extensions)
        {
            return FromResult(await ProcessExtensionsFunctionListAsync(domain, workflow, pagedList,
                requestContext.Headers, requestContext.QueryParameters, cancellationToken));
        }
        if (functionType == Definitions.Functions.FunctionTypeConst.Hierarchy)
        {
            return FromResult(await ProcessHierarchyFunctionListAsync(domain, workflow, pagedList,
                cancellationToken));
        }
        return FromResult(await ProcessCustomFunctionListAsync(function, workflow, domain, pagedList,
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
            var stateResult = await ProcessLongpoolingFunctionAsync(domain, workflow, instance, ifNoneMatch,
                parameters.Version, parameters.Extensions, requestContext.Headers, requestContext.QueryParameters, cancellationToken);

            if (stateResult.IsNotModified)
                return StatusCode(304);

            if (stateResult.Result.IsSuccess && stateResult.Result.Value is { } stateValue)
            {
                if (!string.IsNullOrEmpty(stateValue.ETag))
                    HttpContext.Response.Headers[HeadersConstants.ETag] = stateValue.ETag;
                if (!string.IsNullOrEmpty(stateValue.EntityEtag))
                    HttpContext.Response.Headers[HeadersConstants.XEntityETag] = stateValue.EntityEtag;
            }

            return FromResult(stateResult.Result);
        }

        if (functionType == Definitions.Functions.FunctionTypeConst.View)
        {
            return FromResult(await ProcessViewFunctionAsync(domain, workflow, instance, parameters.Version,
                parameters.TransitionKey, requestContext.Headers, requestContext.QueryParameters,
                cancellationToken));
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

        if (functionType == Definitions.Functions.FunctionTypeConst.Authorize)
        {
            var role = currentUser.Roles?.Length > 0 ? string.Join(",", currentUser.Roles) : (parameters.Role ?? requestContext.QueryParameters.GetValueOrDefault("role", null) ?? string.Empty);
            var version = parameters.Version ?? requestContext.QueryParameters.GetValueOrDefault("version", null);
            var checkQueryRoles = parameters.QueryRoles == true
                || string.Equals(requestContext.QueryParameters.GetValueOrDefault("queryRoles", null), "true", StringComparison.OrdinalIgnoreCase);
            var result = await authorizeAppService.GetAuthorizeResultForInstanceAsync(
                domain, workflow, instance, role, parameters.TransitionKey, parameters.FunctionKey, version, checkQueryRoles,
                cancellationToken);
            return AuthorizeResultToActionResult(result);
        }

        if (functionType == Definitions.Functions.FunctionTypeConst.AuthorizationMatrix)
        {
            var version = parameters.Version ?? requestContext.QueryParameters.GetValueOrDefault("version", null);
            var result = await authorizeAppService.GetAuthorizationMatrixForInstanceAsync(
                domain, workflow, instance, version, cancellationToken);
            return FromResult(result);
        }
        if (functionType == Definitions.Functions.FunctionTypeConst.Hierarchy)
        {
            var input = new GetInstanceHierarchyInput
            {
                Domain = domain,
                Workflow = workflow,
                Instance = instance
            };
            var result = await queryAppService.GetInstanceHierarchyAsync(input, cancellationToken);
            return FromResult(result);
        }
        return FromResult(await functionAppService.GetFunctionByInstanceAsync(function, workflow, domain, instance,
            requestContext.Headers, requestContext.QueryParameters, cancellationToken));
    }

    #region Private Helper Methods

    /// <summary>
    /// Maps authorize result to action result: success and allowed → 200 with body; success and not allowed → 403 with same body; failure → error response.
    /// </summary>
    private IActionResult AuthorizeResultToActionResult(Result<AuthorizeOutput> result)
    {
        if (!result.IsSuccess)
            return FromResult(result);
        if (result.Value!.Allowed)
            return Ok(result.Value);
        return StatusCode(403, result.Value);
    }

    private async Task<ConditionalResult<GetInstanceStateOutput>> ProcessLongpoolingFunctionAsync(
        string domain,
        string workflow,
        string instance,
        string? ifNoneMatch,
        string? version,
        string[]? extensions,
        Dictionary<string, string?> headers,
        Dictionary<string, string?> queryParams,
        CancellationToken cancellationToken)
    {
        var role = currentUser.Roles?.FirstOrDefault();
        var input = new GetInstanceStateInput
        {
            Domain = domain,
            Workflow = workflow,
            Instance = instance,
            IfNoneMatch = ifNoneMatch,
            Version = version,
            Extensions = extensions,
            Headers = headers,
            QueryParams = queryParams,
            Role = role
        };
        return await queryAppService.GetInstanceStateAsync(input, cancellationToken);
    }

    private async Task<Result<HateoasPagedResultDto<GetInstanceStateOutput>>> ProcessLongpoolingFunctionListAsync(
        string domain,
        string workflow,
        HateoasPagedList<GetInstanceOutput> instanceListResult,
        CancellationToken cancellationToken)
    {
        var tasks = instanceListResult.Items.Select(async instance =>
        {
            await using var scope = serviceScopeFactory.CreateAsyncScope();
            var scopedQueryService = scope.ServiceProvider.GetRequiredService<IInstanceQueryAppService>();
            var input = new GetInstanceStateInput
            {
                Domain = domain,
                Workflow = workflow,
                Instance = instance.Key!,
                Version = instance.FlowVersion,
                Extensions = null
            };
            return await scopedQueryService.GetInstanceStateAsync(input, cancellationToken);
        });

        var results = await Task.WhenAll(tasks);
        var list = results.Where(r => r.Result.IsSuccess).Select(r => r.Result.Value!).ToList();

        var route = urlTemplateBuilder.BuildFunctionListUrl(domain, workflow,
            Definitions.Functions.FunctionTypeConst.Longpooling, InstanceUrlTemplates.GetApiVersionPrefix("1"));
        var output = linkGenerator.CreateHateoasResult(instanceListResult, list, route);

        return Result<HateoasPagedResultDto<GetInstanceStateOutput>>.Ok(output);
    }

    private async Task<Result<GetViewOutput>> ProcessViewFunctionAsync(
        string domain,
        string workflow,
        string instance,
        string? version,
        string? transitionKey,
        Dictionary<string, string?> headers,
        Dictionary<string, string?> queryParams,
        CancellationToken cancellationToken)
    {
        var input = new GetViewInput
        {
            Domain = domain,
            Workflow = workflow,
            Instance = instance,
            Version = version,
            Headers = headers,
            QueryParameters = queryParams
        };

        return await queryAppService.GetViewAsync(
            input,
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
        var tasks = instanceListResult.Items.Select(async instance =>
        {
            await using var scope = serviceScopeFactory.CreateAsyncScope();
            var scopedQueryService = scope.ServiceProvider.GetRequiredService<IInstanceQueryAppService>();
            var input = new GetExtensionsInput
            {
                Domain = domain,
                Workflow = workflow,
                Instance = instance.Key!,
                Version = instance.FlowVersion,
                Extensions = null,
                Headers = headers,
                QueryParameters = queryParams
            };
            return await scopedQueryService.GetExtensionsAsync(input, cancellationToken);
        });

        var results = await Task.WhenAll(tasks);
        var list = results.Where(r => r.IsSuccess).Select(r => r.Value!).ToList();

        var route = urlTemplateBuilder.BuildFunctionListUrl(domain, workflow,
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
        var tasks = instanceListResult.Items.Select(async instance =>
        {
            await using var scope = serviceScopeFactory.CreateAsyncScope();
            var scopedQueryService = scope.ServiceProvider.GetRequiredService<IInstanceQueryAppService>();
            var input = new GetSchemaInput
            {
                Domain = domain,
                Workflow = workflow,
                Instance = instance.Key!,
                Version = instance.FlowVersion
            };
            return await scopedQueryService.GetSchemaAsync(input, string.Empty, cancellationToken);
        });

        var results = await Task.WhenAll(tasks);
        var list = results.Where(r => r.IsSuccess).Select(r => r.Value!).ToList();

        var route = urlTemplateBuilder.BuildFunctionListUrl(domain, workflow,
            Definitions.Functions.FunctionTypeConst.Schema, InstanceUrlTemplates.GetApiVersionPrefix("1"));
        var output = linkGenerator.CreateHateoasResult(instanceListResult, list, route);

        return Result<HateoasPagedResultDto<GetSchemaOutput>>.Ok(output);
    }
    private async Task<Result<HateoasPagedResultDto<GetInstanceHierarchyOutput>>> ProcessHierarchyFunctionListAsync(
        string domain,
        string workflow,
        HateoasPagedList<GetInstanceOutput> instanceListResult,
        CancellationToken cancellationToken)
    {
        var tasks = instanceListResult.Items.Select(async instance =>
        {
            await using var scope = serviceScopeFactory.CreateAsyncScope();
            var scopedQueryService = scope.ServiceProvider.GetRequiredService<IInstanceQueryAppService>();
            var input = new GetInstanceHierarchyInput
            {
                Domain = domain,
                Workflow = workflow,
                Instance = instance.Key!
            };
            return await scopedQueryService.GetInstanceHierarchyAsync(input, cancellationToken);
        });
 
        var results = await Task.WhenAll(tasks);
        var list = results.Where(r => r.IsSuccess).Select(r => r.Value!).ToList();
 
        var route = urlTemplateBuilder.BuildFunctionListUrl(domain, workflow,
            Definitions.Functions.FunctionTypeConst.Hierarchy, InstanceUrlTemplates.GetApiVersionPrefix("1"));
        var output = linkGenerator.CreateHateoasResult(instanceListResult, list, route);
 
        return Result<HateoasPagedResultDto<GetInstanceHierarchyOutput>>.Ok(output);
    }
    private async Task<Result<HateoasPagedResultDto<GetViewOutput>>> ProcessViewFunctionListAsync(
        string domain,
        string workflow,
        HateoasPagedList<GetInstanceOutput> instanceListResult,
        CancellationToken cancellationToken)
    {
        var tasks = instanceListResult.Items.Select(async instance =>
        {
            await using var scope = serviceScopeFactory.CreateAsyncScope();
            var scopedQueryService = scope.ServiceProvider.GetRequiredService<IInstanceQueryAppService>();
            var input = new GetViewInput
            {
                Domain = domain,
                Workflow = workflow,
                Instance = instance.Key!,
                Version = instance.FlowVersion
            };
            return await scopedQueryService.GetViewAsync(input,  string.Empty,
                cancellationToken);
        });

        var results = await Task.WhenAll(tasks);
        var list = results.Where(r => r.IsSuccess).Select(r => r.Value!).ToList();

        var route = urlTemplateBuilder.BuildFunctionListUrl(domain, workflow,
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

        if (response.Result.IsSuccess && response.Result.Value is { } dataValue)
        {
            if (!string.IsNullOrEmpty(dataValue.ETag))
                HttpContext.Response.Headers[HeadersConstants.ETag] = dataValue.ETag;
            if (!string.IsNullOrEmpty(dataValue.EntityEtag))
                HttpContext.Response.Headers[HeadersConstants.XEntityETag] = dataValue.EntityEtag;
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
        var tasks = instanceListResult.Items.Select(async instance =>
        {
            await using var scope = serviceScopeFactory.CreateAsyncScope();
            var scopedQueryService = scope.ServiceProvider.GetRequiredService<IInstanceQueryAppService>();
            var input = new GetInstanceDataInput
            {
                Domain = domain,
                Workflow = workflow,
                Instance = instance.Key!,
                Headers = headers,
                QueryParameters = queryParams
            };
            return await scopedQueryService.GetInstanceDataAsync(input, cancellationToken);
        });

        var results = await Task.WhenAll(tasks);
        var list = results.Where(r => r.Result.IsSuccess).Select(r => r.Result.Value!).ToList();

        var route = urlTemplateBuilder.BuildFunctionListUrl(domain, workflow,
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
        var tasks = instanceListResult.Items.Select(async instance =>
        {
            await using var scope = serviceScopeFactory.CreateAsyncScope();
            var scopedFunctionService = scope.ServiceProvider.GetRequiredService<IFunctionAppService>();
            return await scopedFunctionService.GetFunctionByInstanceAsync(function, workflow, domain, instance.Key!,
                headers, queryParams, cancellationToken);
        });

        var results = await Task.WhenAll(tasks);
        if (results.Any(r => !r.IsSuccess))
            return Result<HateoasPagedResultDto<Dictionary<string, dynamic?>>>.Fail(results.First(r => !r.IsSuccess)
                .Error);

        var list = results.Select(r => r.Value!).ToList();
        var route = InstanceUrlTemplates.FunctionList(domain, workflow, function,
            InstanceUrlTemplates.GetApiVersionPrefix("1"));
        var output = linkGenerator.CreateHateoasResult(instanceListResult, list, route);
        return Result<HateoasPagedResultDto<Dictionary<string, dynamic?>>>.Ok(output);
    }

    #endregion
}