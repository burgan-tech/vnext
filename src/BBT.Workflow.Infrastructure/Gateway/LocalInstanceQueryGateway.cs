using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.DTOs;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Workflow.Gateway;

/// <summary>
/// Local implementation of instance query gateway.
/// Executes queries locally with proper schema context.
/// Uses ExecuteInScopeAsync/ExecuteInScopeRawAsync to create fresh scope for each operation
/// and update AmbientServiceProvider.Current, preventing cross-scope UoW interference.
/// </summary>
public sealed class LocalInstanceQueryGateway : IInstanceQueryGateway
{
    private readonly IServiceScopeFactory _serviceScopeFactory;

    /// <summary>
    /// Initializes a new instance of LocalInstanceQueryGateway.
    /// </summary>
    /// <param name="serviceScopeFactory">Factory for creating service scopes.</param>
    public LocalInstanceQueryGateway(IServiceScopeFactory serviceScopeFactory)
    {
        _serviceScopeFactory = serviceScopeFactory;
    }

    /// <inheritdoc />
    public Task<ConditionalResult<GetInstanceOutput>> GetInstanceAsync(
        GetInstanceInput input,
        CancellationToken cancellationToken = default)
    {
        return _serviceScopeFactory.ExecuteWithWorkflowAsync(input.Domain, input.Workflow, input.Version,
            async (sp, ct) =>
            {
                var queryService = sp.GetRequiredService<IInstanceQueryAppService>();
                return await queryService.GetInstanceAsync(input, ct);
            }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ConditionalResult<GetInstanceDataOutput>> GetInstanceDataAsync(
        GetInstanceDataInput input,
        CancellationToken cancellationToken = default)
    {
        return _serviceScopeFactory.ExecuteWithWorkflowAsync(input.Domain, input.Version, input.Version,
            async (sp, ct) =>
            {
                var queryService = sp.GetRequiredService<IInstanceQueryAppService>();
                return await queryService.GetInstanceDataAsync(input, ct);
            }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<InstanceListWithGroupsResponse<GetInstanceOutput>>> GetInstanceListAsync(
        GetInstanceListInput input,
        CancellationToken cancellationToken = default)
    {
        return _serviceScopeFactory.ExecuteWithWorkflowAsync(input.Domain, input.Workflow, input.Version,
            async (sp, ct) =>
            {
                var queryService = sp.GetRequiredService<IInstanceQueryAppService>();
                return await queryService.GetInstanceListAsync(input, ct);
            }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<GetInstanceHistoryOutput>> GetInstanceHistoryAsync(
        GetInstanceHistoryInput input,
        CancellationToken cancellationToken = default)
    {
        return _serviceScopeFactory.ExecuteWithWorkflowAsync(input.Domain, input.Workflow, input.Version,
            async (sp, ct) =>
            {
                var queryService = sp.GetRequiredService<IInstanceQueryAppService>();
                return await queryService.GetInstanceHistoryAsync(input, ct);
            }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<ConditionalResult<GetInstanceStateOutput>> GetFunctionWithStateAsync(
        GetFunctionWithInstanceInput input,
        CancellationToken cancellationToken = default)
    {
        return _serviceScopeFactory.ExecuteWithWorkflowAsync(input.Domain, input.Workflow, input.Version,
            async (sp, ct) =>
            {
                var queryService = sp.GetRequiredService<IInstanceQueryAppService>();
                var stateInput = new GetInstanceStateInput
                {
                    Domain = input.Domain,
                    Workflow = input.Workflow,
                    Version = input.Version,
                    Instance = input.Instance,
                    Extensions = input.Extensions,
                    Headers = input.Headers,
                    QueryParams = input.QueryParams,
                    Role = input.Role,
                    IfNoneMatch = input.IfNoneMatch
                };
                return await queryService.GetInstanceStateAsync(stateInput, ct);
            }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<GetViewOutput>> GetFunctionWithViewAsync(
        GetFunctionWithInstanceInput input,
        string? transitionKey,
        CancellationToken cancellationToken = default)
    {
        return _serviceScopeFactory.ExecuteWithWorkflowAsync(input.Domain, input.Workflow, input.Version,
            async (sp, ct) =>
            {
                var queryService = sp.GetRequiredService<IInstanceQueryAppService>();
                var viewInput = new GetViewInput
                {
                    Domain = input.Domain,
                    Workflow = input.Workflow,
                    Version = input.Version,
                    Instance = input.Instance,
                    Headers = input.Headers,
                    QueryParameters = input.QueryParams
                };
                return await queryService.GetViewAsync(viewInput, transitionKey, ct);
            }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<GetSchemaOutput>> GetFunctionWithSchemaAsync(
        GetFunctionWithInstanceInput input,
        string transitionKey,
        CancellationToken cancellationToken = default)
    {
        return _serviceScopeFactory.ExecuteWithWorkflowAsync(input.Domain, input.Workflow, input.Version,
            async (sp, ct) =>
            {
                var queryService = sp.GetRequiredService<IInstanceQueryAppService>();
                var schemaInput = new GetSchemaInput
                {
                    Domain = input.Domain,
                    Workflow = input.Workflow,
                    Version = input.Version,
                    Instance = input.Instance
                };
                return await queryService.GetSchemaAsync(schemaInput, transitionKey, ct);
            }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Result<GetExtensionsOutput>> GetFunctionWithExtensionsAsync(
        GetFunctionWithInstanceInput input,
        CancellationToken cancellationToken = default)
    {
        return _serviceScopeFactory.ExecuteWithWorkflowAsync(input.Domain, input.Workflow, input.Version,
            async (sp, ct) =>
            {
                var queryService = sp.GetRequiredService<IInstanceQueryAppService>();

                var extensionsInput = new GetExtensionsInput
                {
                    Domain = input.Domain,
                    Workflow = input.Workflow,
                    Version = input.Version,
                    Instance = input.Instance,
                    Extensions = input.Extensions
                };
                return await queryService.GetExtensionsAsync(extensionsInput, ct);
            }, cancellationToken);
    }
}