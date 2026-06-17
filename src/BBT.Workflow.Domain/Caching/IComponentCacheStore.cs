using BBT.Aether.Domain.Entities;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Caching;

/// <summary>
/// Provides caching operations for workflow components including flows, tasks, schemas, functions, views, and extensions.
/// Supports domain-specific caching with versioning capabilities.
/// </summary>
public interface IComponentCacheStore
{
    /// <summary>
    /// Retrieves a workflow definition from the cache.
    /// </summary>
    /// <param name="domain">The domain identifier where the workflow belongs.</param>
    /// <param name="key">The unique key/name identifier of the workflow.</param>
    /// <param name="version">The specific version of the workflow. If null, returns the latest version.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>
    /// A <see cref="Task{TResult}"/> representing the asynchronous operation.
    /// The task result contains a <see cref="Result{T}"/> with the <see cref="Definitions.Workflow"/> if found,
    /// or a <see cref="Error.NotFound"/> if the workflow does not exist in cache.
    /// </returns>
    Task<Result<Definitions.Workflow>> GetFlowAsync(
        string domain,
        string key,
        string? version,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a workflow task definition from the cache.
    /// </summary>
    /// <param name="domain">The domain identifier where the task belongs.</param>
    /// <param name="key">The unique key/name identifier of the task.</param>
    /// <param name="version">The specific version of the task. If null, returns the latest version.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>
    /// A <see cref="Task{TResult}"/> representing the asynchronous operation.
    /// The task result contains a <see cref="Result{T}"/> with the <see cref="WorkflowTask"/> if found,
    /// or a <see cref="Error.NotFound"/> if the task does not exist in cache.
    /// </returns>
    Task<Result<WorkflowTask>> GetTaskAsync(
        string domain,
        string key,
        string? version,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a schema definition from the cache.
    /// </summary>
    /// <param name="domain">The domain identifier where the schema belongs.</param>
    /// <param name="key">The unique key/name identifier of the schema.</param>
    /// <param name="version">The specific version of the schema. If null, returns the latest version.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>
    /// A <see cref="Task{TResult}"/> representing the asynchronous operation.
    /// The task result contains a <see cref="Result{T}"/> with the <see cref="SchemaDefinition"/> if found,
    /// or a <see cref="Error.NotFound"/> if the schema does not exist in cache.
    /// </returns>
    Task<Result<SchemaDefinition>> GetSchemaAsync(
        string domain,
        string key,
        string? version,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a function definition from the cache.
    /// </summary>
    /// <param name="domain">The domain identifier where the function belongs.</param>
    /// <param name="key">The unique key/name identifier of the function.</param>
    /// <param name="version">The specific version of the function. If null, returns the latest version.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>
    /// A <see cref="Task{TResult}"/> representing the asynchronous operation.
    /// The task result contains a <see cref="Result{T}"/> with the <see cref="Function"/> if found,
    /// or a <see cref="Error.NotFound"/> if the function does not exist in cache.
    /// </returns>
    Task<Result<Function>> GetFunctionAsync(
        string domain,
        string key,
        string? version,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a view definition from the cache.
    /// </summary>
    /// <param name="domain">The domain identifier where the view belongs.</param>
    /// <param name="key">The unique key/name identifier of the view.</param>
    /// <param name="version">The specific version of the view. If null, returns the latest version.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>
    /// A <see cref="Task{TResult}"/> representing the asynchronous operation.
    /// The task result contains a <see cref="Result{T}"/> with the <see cref="View"/> if found,
    /// or a <see cref="Error.NotFound"/> if the view does not exist in cache.
    /// </returns>
    Task<Result<View>> GetViewAsync(
        string domain,
        string key,
        string? version,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves an extension definition from the cache.
    /// </summary>
    /// <param name="domain">The domain identifier where the extension belongs.</param>
    /// <param name="key">The unique key/name identifier of the extension.</param>
    /// <param name="version">The specific version of the extension. If null, returns the latest version.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>
    /// A <see cref="Task{TResult}"/> representing the asynchronous operation.
    /// The task result contains a <see cref="Result{T}"/> with the <see cref="Extension"/> if found,
    /// or a <see cref="Error.NotFound"/> if the extension does not exist in cache.
    /// </returns>
    Task<Result<Extension>> GetExtensionAsync(
        string domain,
        string key,
        string? version,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves all extension definitions for the specified domain from the cache.
    /// This method is used to get core extensions that should be executed runtime-wide.
    /// </summary>
    /// <param name="domain">The domain identifier to retrieve extensions for.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>
    /// A <see cref="Task{TResult}"/> representing the asynchronous operation.
    /// The task result contains a <see cref="Result{T}"/> with a collection of all <see cref="Extension"/> definitions for the domain.
    /// Returns an empty collection if no extensions are found (success with empty list).
    /// </returns>
    Task<Result<IEnumerable<Extension>>> GetAllExtensionsAsync(
        string domain,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a mapping (script-library) component definition (<c>sys-mappings</c>) from the cache.
    /// </summary>
    /// <param name="domain">The domain identifier where the mapping belongs.</param>
    /// <param name="key">The unique key/name identifier of the mapping component.</param>
    /// <param name="version">The specific version of the mapping. If null, returns the latest version.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>
    /// A <see cref="Task{TResult}"/> whose result contains a <see cref="Result{T}"/> with the
    /// <see cref="Mapping"/> if found, or <see cref="Error.NotFound"/> otherwise.
    /// </returns>
    Task<Result<Mapping>> GetMappingAsync(
        string domain,
        string key,
        string? version,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores an entity in the cache.
    /// </summary>
    /// <typeparam name="T">The type of entity to store. Must implement <see cref="IDomainEntity"/>.</typeparam>
    /// <param name="entity">The entity instance to be cached.</param>
    /// <param name="cancellationToken">Token to monitor for cancellation requests.</param>
    /// <returns>
    /// A <see cref="Task{TResult}"/> representing the asynchronous cache operation.
    /// The task result contains a <see cref="Result"/> indicating success or failure.
    /// Returns validation error if entity is null or invalid.
    /// Returns not supported error if the entity type is not supported for caching.
    /// </returns>
    public Task<Result> SetAsync<T>(T entity, CancellationToken cancellationToken = default)
        where T : class, IDomainEntity, IReferenceSetter;
}