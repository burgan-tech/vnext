using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Security;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Application.Security;

/// <summary>
/// Application service for invalidating schema cache when flows are created or deleted
/// This should be called from flow management application services
/// Application Layer - orchestrates domain and infrastructure services
/// </summary>
public class SchemaCacheInvalidationService
{
    private readonly ISchemaValidator _schemaValidator;
    private readonly ILogger<SchemaCacheInvalidationService> _logger;

    public SchemaCacheInvalidationService(
        ISchemaValidator schemaValidator,
        ILogger<SchemaCacheInvalidationService> logger)
    {
        _schemaValidator = schemaValidator ?? throw new ArgumentNullException(nameof(schemaValidator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Invalidates the schema cache after a flow is created
    /// </summary>
    /// <param name="flowKey">The key of the created flow</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task InvalidateAfterFlowCreatedAsync(string flowKey, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Invalidating schema cache after flow created: {FlowKey}", flowKey);
            await _schemaValidator.InvalidateCacheAsync(cancellationToken);
            _logger.LogInformation("Schema cache invalidated successfully for flow: {FlowKey}", flowKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to invalidate schema cache after flow created: {FlowKey}", flowKey);
            // Don't throw - cache invalidation failure shouldn't break flow creation
        }
    }

    /// <summary>
    /// Invalidates the schema cache after a flow is deleted
    /// </summary>
    /// <param name="flowKey">The key of the deleted flow</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task InvalidateAfterFlowDeletedAsync(string flowKey, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Invalidating schema cache after flow deleted: {FlowKey}", flowKey);
            await _schemaValidator.InvalidateCacheAsync(cancellationToken);
            _logger.LogInformation("Schema cache invalidated successfully for deleted flow: {FlowKey}", flowKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to invalidate schema cache after flow deleted: {FlowKey}", flowKey);
            // Don't throw - cache invalidation failure shouldn't break flow deletion
        }
    }

    /// <summary>
    /// Invalidates the schema cache after a flow status changes
    /// </summary>
    /// <param name="flowKey">The key of the flow</param>
    /// <param name="oldStatus">Old status</param>
    /// <param name="newStatus">New status</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public async Task InvalidateAfterFlowStatusChangedAsync(
        string flowKey, 
        string oldStatus, 
        string newStatus, 
        CancellationToken cancellationToken = default)
    {
        // Only invalidate if status changed to/from Active
        if ((oldStatus == "A" || newStatus == "A") && oldStatus != newStatus)
        {
            try
            {
                _logger.LogInformation(
                    "Invalidating schema cache after flow status changed: {FlowKey} from {OldStatus} to {NewStatus}", 
                    flowKey, oldStatus, newStatus);
                await _schemaValidator.InvalidateCacheAsync(cancellationToken);
                _logger.LogInformation("Schema cache invalidated successfully for flow status change: {FlowKey}", flowKey);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to invalidate schema cache after flow status changed: {FlowKey}", flowKey);
                // Don't throw - cache invalidation failure shouldn't break status change
            }
        }
    }
}

