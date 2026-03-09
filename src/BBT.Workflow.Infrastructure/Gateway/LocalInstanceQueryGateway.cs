using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.DTOs;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Workflow.Gateway;

/// <summary>
/// Local implementation of instance query gateway.
/// Executes queries locally with proper schema context.
/// Uses IServiceScopeFactory to create fresh scope for each operation.
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
    public async Task<ConditionalResult<GetInstanceOutput>> GetInstanceAsync(
        GetInstanceInput input,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        var queryService = scope.ServiceProvider.GetRequiredService<IInstanceQueryAppService>();

        using (currentSchema.Use(input.Workflow))
        {
            return await queryService.GetInstanceAsync(input, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<ConditionalResult<GetInstanceDataOutput>> GetInstanceDataAsync(
        GetInstanceDataInput input,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        var queryService = scope.ServiceProvider.GetRequiredService<IInstanceQueryAppService>();

        using (currentSchema.Use(input.Workflow))
        {
            return await queryService.GetInstanceDataAsync(input, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<Result<InstanceListWithGroupsResponse<GetInstanceOutput>>> GetInstanceListAsync(
        GetInstanceListInput input,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        var queryService = scope.ServiceProvider.GetRequiredService<IInstanceQueryAppService>();

        using (currentSchema.Use(input.Workflow))
        {
            return await queryService.GetInstanceListAsync(input, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<Result<GetInstanceHistoryOutput>> GetInstanceHistoryAsync(
        GetInstanceHistoryInput input,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        var queryService = scope.ServiceProvider.GetRequiredService<IInstanceQueryAppService>();

        using (currentSchema.Use(input.Workflow))
        {
            return await queryService.GetInstanceHistoryAsync(input, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<ConditionalResult<GetInstanceStateOutput>> GetFunctionWithStateAsync(
        GetFunctionWithInstanceInput input,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        var queryService = scope.ServiceProvider.GetRequiredService<IInstanceQueryAppService>();

        using (currentSchema.Use(input.Workflow))
        {
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
            return await queryService.GetInstanceStateAsync(stateInput, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<Result<GetViewOutput>> GetFunctionWithViewAsync(
        GetFunctionWithInstanceInput input,
        string? transitionKey,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        var queryService = scope.ServiceProvider.GetRequiredService<IInstanceQueryAppService>();

        using (currentSchema.Use(input.Workflow))
        {
            var viewInput = new GetViewInput
            {
                Domain = input.Domain,
                Workflow = input.Workflow,
                Version = input.Version,
                Instance = input.Instance,
                Headers=input.Headers,
                QueryParameters=input.QueryParams
            };
            return await queryService.GetViewAsync(viewInput, transitionKey, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<Result<GetSchemaOutput>> GetFunctionWithSchemaAsync(
        GetFunctionWithInstanceInput input,
        string transitionKey,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        var queryService = scope.ServiceProvider.GetRequiredService<IInstanceQueryAppService>();

        using (currentSchema.Use(input.Workflow))
        {
            var schemaInput = new GetSchemaInput
            {
                Domain = input.Domain,
                Workflow = input.Workflow,
                Version = input.Version,
                Instance = input.Instance
            };
            return await queryService.GetSchemaAsync(schemaInput, transitionKey, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<Result<GetExtensionsOutput>> GetFunctionWithExtensionsAsync(
        GetFunctionWithInstanceInput input,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var currentSchema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        var queryService = scope.ServiceProvider.GetRequiredService<IInstanceQueryAppService>();

        using (currentSchema.Use(input.Workflow))
        {
            var extensionsInput = new GetExtensionsInput
            {
                Domain = input.Domain,
                Workflow = input.Workflow,
                Version = input.Version,
                Instance = input.Instance,
                Extensions = input.Extensions
            };
            return await queryService.GetExtensionsAsync(extensionsInput, cancellationToken);
        }
    }
}

