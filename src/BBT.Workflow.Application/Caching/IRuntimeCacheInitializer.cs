namespace BBT.Workflow.Caching;

/// <summary>
/// Service responsible for initializing the runtime cache with workflow components.
/// This centralizes the logic for loading all workflow entities (flows, tasks, functions, etc.)
/// from the database and populating the domain cache context.
/// </summary>
public interface IRuntimeCacheInitializer
{
    /// <summary>
    /// Initializes the domain cache context by loading all workflow components from the database.
    /// This includes workflows, tasks, functions, views, schemas, and extensions.
    /// Updates only in-memory cache (used by receiving pods).
    /// </summary>
    /// <param name="fullLoad">
    /// When <c>true</c>, all records are fetched regardless of previous initialization state (used at startup).
    /// When <c>false</c> (default), only records modified since the last successful initialization are fetched
    /// and merged into the existing cache (incremental update).
    /// </param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests</param>
    /// <returns>A task representing the asynchronous initialization operation</returns>
    Task InitializeAsync(bool fullLoad = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Initializes both in-memory and distributed cache by loading all workflow components from the database.
    /// This includes workflows, tasks, functions, views, schemas, and extensions.
    /// Use this when triggering cache refresh that should propagate to all pods.
    /// </summary>
    /// <param name="fullLoad">
    /// When <c>true</c>, all records are fetched (full reload).
    /// When <c>false</c> (default), only records modified since the last initialization are fetched.
    /// </param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests</param>
    /// <returns>A task representing the asynchronous initialization operation</returns>
    Task InitializeWithDistributedCacheAsync(bool fullLoad = false, CancellationToken cancellationToken = default);
}

