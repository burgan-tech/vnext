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
/// policy, not an error. Any read failure, or a stored pointer that is not an accepted form, is a
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
            return Failed(domain, key, WorkflowErrorCodes.VersionRegistryUnavailable, ex.Message, ex);
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
                : Failed(domain, key, flow.Error.Code,
                    $"registry flow lookup failed: {flow.Error.Code}", null);
        }

        var currentSchema = services.GetRequiredService<ICurrentSchema>();
        List<InstanceAndDataModel> rows;
        using (currentSchema.Change(RuntimeSysSchemaInfo.VersionRegistry))
        {
            var repository = services.GetRequiredService<IInstanceRepository>();
            rows = await repository.GetActiveDataListByKeyAsync(key, ct);
        }

        var row = SelectLatest(rows);
        if (row is null)
        {
            return Result<string?>.Ok(null);
        }

        var pointer = JsonSerializer.Deserialize<VersionRegistryEntry>(row.Json, EntryOptions)?.DefaultVersion;
        if (!DefaultVersionResolver.IsValidPointer(pointer))
        {
            // The write guard rejects these; reaching one means it was bypassed. Fail closed.
            logger.FlowDefaultVersionInvalid(domain, key, pointer!);
            return Result<string?>.Fail(WorkflowErrors.FlowDefaultVersionInvalid(domain, key, pointer!));
        }

        return Result<string?>.Ok(pointer);
    }

    private static JsonData? SelectLatest(List<InstanceAndDataModel> rows)
    {
        InstanceData? best = null;
        foreach (var data in rows.Select(r => r.InstanceData).Where(d => d is not null))
        {
            if (data.IsLatest)
            {
                return data.Data;
            }

            if (best is null ||
                InstanceDataVersionComparer.StringVersionComparer.Instance.Compare(data.Version, best.Version) > 0)
            {
                best = data;
            }
        }

        return best?.Data;
    }

    private Result<string?> Failed(string domain, string key, string errorCode, string reason, Exception? exception)
    {
        logger.VersionRegistryReadFailed(domain, key, errorCode, exception);
        return Result<string?>.Fail(WorkflowErrors.VersionRegistryUnavailable(domain, key, reason));
    }
}
