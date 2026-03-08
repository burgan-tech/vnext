using BBT.Aether.Domain.Repositories;

namespace BBT.Workflow.Instances;

/// <summary>
/// Repository interface for managing workflow instance correlations.
/// This interface provides data access methods for handling parent-child relationships
/// between workflow instances, particularly for SubFlow and SubProcess scenarios.
/// </summary>
public interface IInstanceCorrelationRepository : IRepository<InstanceCorrelation, Guid>
{
    /// <summary>
    /// Finds all correlations where the specified instance ID is the parent instance.
    /// Includes both active and completed correlations for building full hierarchy trees.
    /// </summary>
    /// <param name="parentInstanceId">The unique identifier of the parent workflow instance.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task representing the asynchronous operation.
    /// The result contains all correlations where the instance is a parent (active and completed).
    /// </returns>
    Task<List<InstanceCorrelation>> GetByParentAsync(
        Guid parentInstanceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds active correlations where the specified instance ID is the parent instance.
    /// This method is used to identify all active child flows for a given parent workflow.
    /// </summary>
    /// <param name="parentInstanceId">The unique identifier of the parent workflow instance.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task representing the asynchronous operation.
    /// The result contains a collection of active correlations where the instance is a parent.
    /// </returns>
    Task<List<InstanceCorrelation>> GetActiveByParentAsync(
        Guid parentInstanceId, 
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Checks whether there are any active SubFlow correlations for the specified parent instance.
    /// This method provides a fast existence check without retrieving the actual correlation records.
    /// </summary>
    /// <param name="parentInstanceId">The unique identifier of the parent workflow instance.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task representing the asynchronous operation.
    /// The result contains true if any active SubFlow correlations exist for the parent instance, false otherwise.
    /// </returns>
    Task<bool> AnyActiveSubFlowByParentAsync(
        Guid parentInstanceId,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Finds the active SubFlow correlation for the specified parent instance.
    /// This method retrieves the correlation record linking a parent workflow to its active SubFlow.
    /// </summary>
    /// <param name="parentInstanceId">The unique identifier of the parent workflow instance.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task representing the asynchronous operation.
    /// The result contains the active SubFlow correlation if found, null if no active SubFlow exists for the parent.
    /// </returns>
    Task<InstanceCorrelation?> FindActiveSubFlowByParentAsync(
        Guid parentInstanceId,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Finds a correlation by SubFlow instance ID.
    /// This method is used when a SubFlow or SubProcess completes to find the correlation record
    /// linking it to the parent workflow.
    /// </summary>
    /// <param name="subInstanceId">The unique identifier of the SubFlow or SubProcess instance.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>
    /// A task representing the asynchronous operation.
    /// The result contains the correlation record if found, null otherwise.
    /// </returns>
    Task<InstanceCorrelation?> FindBySubInstanceIdAsync(
        Guid subInstanceId,
        CancellationToken cancellationToken = default);
} 