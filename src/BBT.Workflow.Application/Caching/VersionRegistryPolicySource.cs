using System.Text.Json;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Caching;

/// <summary>
/// Reads a flow's default-version pointer from the <c>sys-version-registry</c> system flow: one
/// instance per flow key whose latest data row carries <c>defaultVersion</c>.
/// </summary>
/// <remarks>
/// Registry not published in the domain (an older core package) or no instance for the key means no
/// policy, not an error. The pointer is the latest committed row of the newest instance with the key,
/// whatever its status (<see cref="IInstanceRepository.FindLatestDataByKeyAsync"/>). A read failure is
/// <c>VersionRegistryUnavailable</c> (503, cause in the log only); a stored pointer that is not an
/// accepted form, a non-string one included, is <c>FlowDefaultVersionInvalid</c> (409). Both are a
/// failed result and never a throw: <see cref="CacheSet{T}"/> trusts the source and must not fall back
/// to <c>latest</c>. The read runs in its own scope and non-transactional unit of work, like
/// <see cref="RuntimeCacheBackend{T}"/>, so it never shares a caller's DbContext. The schema is entered
/// by the system-flow key (<see cref="RuntimeSysSchemaInfo.VersionRegistry"/>) — the same form
/// <c>RuntimeService</c> and the script engine use for <c>sys-*</c> flows.
/// </remarks>
public sealed class VersionRegistryPolicySource(
    IServiceScopeFactory scopeFactory,
    ILogger<VersionRegistryPolicySource> logger) : IVersionPolicySource
{
    private static readonly JsonSerializerOptions EntryOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc />
    public async Task<Result<string?>> GetDefaultVersionAsync(string domain, string key, CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var uowManager = services.GetRequiredService<IUnitOfWorkManager>();
            await using var uow = uowManager.Begin(new UnitOfWorkOptions
            {
                Scope = UnitOfWorkScopeOption.RequiresNew,
                IsTransactional = false
            });

            var result = await ReadAsync(services, domain, key, ct);
            await uow.CommitAsync(ct);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Failed(domain, key, WorkflowErrorCodes.VersionRegistryUnavailable, ex);
        }
    }

    private async Task<Result<string?>> ReadAsync(
        IServiceProvider services, string domain, string key, CancellationToken ct)
    {
        var runtimeInfoProvider = services.GetRequiredService<IRuntimeInfoProvider>();
        runtimeInfoProvider.Check(domain);
        using var domainScope = runtimeInfoProvider.UseDomain(domain);

        // Resolved lazily: DomainCacheContext (a singleton) receives this source, so a constructor
        // dependency on the cache store would be a cycle.
        var cacheStore = services.GetRequiredService<IComponentCacheStore>();
        var flow = await cacheStore.GetFlowAsync(domain, RuntimeSysSchemaInfo.VersionRegistry, null, ct);
        if (!flow.IsSuccess)
        {
            // Registry flow not published here (older core package): no policy, and no DB access
            // that would hit a schema which does not exist.
            return flow.Error.Code == WorkflowErrorCodes.CacheItemNotFound
                ? Result<string?>.Ok(null)
                : Failed(domain, key, flow.Error.Code, null);
        }

        var currentSchema = services.GetRequiredService<ICurrentSchema>();
        InstanceData? latest;
        using (currentSchema.Change(RuntimeSysSchemaInfo.VersionRegistry))
        {
            // Status-independent on purpose: the registry instance is Busy between its commit and the
            // runner's settle — exactly when the post-commit job bumps the generation — and a Faulted
            // one still carries its last pointer. A status-filtered read would fail open in both.
            var repository = services.GetRequiredService<IInstanceRepository>();
            latest = await repository.FindLatestDataByKeyAsync(key, ct);
        }

        if (latest is null)
        {
            return Result<string?>.Ok(null);
        }

        string? pointer;
        try
        {
            pointer = JsonSerializer.Deserialize<VersionRegistryEntry>(latest.Data.Json, EntryOptions)?.DefaultVersion;
        }
        catch (JsonException)
        {
            // A non-string defaultVersion is a broken pointer (409), not an unreadable registry (503).
            return PointerInvalid(domain, key, ReadRawPointer(latest.Data.Json));
        }

        if (!DefaultVersionResolver.IsValidPointer(pointer))
        {
            // The write guard rejects these; reaching one means it was bypassed. Fail closed.
            return PointerInvalid(domain, key, pointer!);
        }

        // "latest" is the stored spelling of Automatic (the merge cannot clear a key with null): no policy.
        return Result<string?>.Ok(DefaultVersionResolver.IsAutomaticPointer(pointer) ? null : pointer);
    }

    private Result<string?> PointerInvalid(string domain, string key, string pointer)
    {
        logger.FlowDefaultVersionInvalid(domain, key, pointer);
        return Result<string?>.Fail(WorkflowErrors.FlowDefaultVersionInvalid(domain, key, pointer));
    }

    private static string ReadRawPointer(string json)
    {
        // Same spelling as VersionRegistryWriteGuard: the raw JSON of the stored value names it in the
        // log and the 409. Only reached after the row parsed as JSON, so Parse does not throw here.
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("defaultVersion", out var raw) ? raw.GetRawText() : string.Empty;
    }

    private Result<string?> Failed(string domain, string key, string errorCode, Exception? exception)
    {
        // The cause goes to the log only: the error's message reaches the client.
        logger.VersionRegistryReadFailed(domain, key, errorCode, exception);
        return Result<string?>.Fail(WorkflowErrors.VersionRegistryUnavailable(domain, key));
    }
}
