using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Workflow.Instances;
using Microsoft.Extensions.DependencyInjection;

namespace BBT.Workflow.Gateway;

/// <summary>
/// Local implementation of instance retry gateway.
/// Executes retry locally with proper schema context.
/// Uses IServiceScopeFactory to create fresh scope for each operation ensuring clean transaction state.
/// </summary>
public sealed class LocalInstanceRetryGateway : IInstanceRetryGateway
{
    private readonly IServiceScopeFactory _serviceScopeFactory;

    /// <summary>
    /// Initializes a new instance of LocalInstanceRetryGateway.
    /// </summary>
    /// <param name="serviceScopeFactory">Factory for creating service scopes.</param>
    public LocalInstanceRetryGateway(IServiceScopeFactory serviceScopeFactory)
    {
        _serviceScopeFactory = serviceScopeFactory;
    }

    /// <inheritdoc />
    public async Task<Result<RetryInstanceOutput>> RetryAsync(
        RetryInstanceInput input,
        CancellationToken cancellationToken = default)
    {
        return await _serviceScopeFactory.ExecuteInScopeAsync(async (sp, ct) =>
        {
            var currentSchema = sp.GetRequiredService<ICurrentSchema>();
            var retryService = sp.GetRequiredService<IInstanceRetryAppService>();

            using (currentSchema.Use(input.Workflow))
            {
                return await retryService.RetryAsync(input, ct);
            }
        }, cancellationToken);
    }
}