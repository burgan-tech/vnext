using System.Text.Json;
using BBT.Aether.MultiSchema;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Runtime;

/// <summary>
/// Reads published components (stored as instances of the component schemas) and turns them into domain
/// entities.
/// </summary>
/// <remarks>
/// Versions are listed from the version column alone and only the one chosen body is deserialized. A malformed
/// body (<see cref="JsonException"/>) reads as absent, and is logged. Any other failure — above all
/// <see cref="OutOfMemoryException"/> — propagates: a row the process could not afford to load must fail the
/// read, never pass for a version that does not exist.
/// </remarks>
public sealed class RuntimeService(
    IInstanceRepository instanceRepository,
    ICurrentSchema currentSchema,
    IOptions<RuntimeOptions> runtimeOptions,
    IRuntimeInfoProvider runtimeInfoProvider,
    ILogger<RuntimeService> logger) : IRuntimeService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetVersionsAsync<T>(string key, CancellationToken cancellationToken = default)
        where T : class, IDomainEntity, IReferenceSetter
    {
        var schemaName = runtimeOptions.Value.GetSchemaNameByType(typeof(T));
        var schemaInfo = runtimeOptions.Value.Schemas[schemaName];

        using (currentSchema.Change(schemaInfo.Schema))
        {
            return await instanceRepository.GetActiveVersionsByKeyAsync(key, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ComponentVersionSummary>> GetVersionPageAsync<T>(
        string key, int skip, int take, CancellationToken cancellationToken = default)
        where T : class, IDomainEntity, IReferenceSetter
    {
        var schemaName = runtimeOptions.Value.GetSchemaNameByType(typeof(T));
        var schemaInfo = runtimeOptions.Value.Schemas[schemaName];

        using (currentSchema.Change(schemaInfo.Schema))
        {
            return await instanceRepository.GetVersionsPagedAsync(key, skip, take, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<PublishedComponentVersion>> GetPublishedVersionsAsync<T>(
        CancellationToken cancellationToken = default)
        where T : class, IDomainEntity, IReferenceSetter
    {
        var schemaName = runtimeOptions.Value.GetSchemaNameByType(typeof(T));
        var schemaInfo = runtimeOptions.Value.Schemas[schemaName];

        using (currentSchema.Change(schemaInfo.Schema))
        {
            return await instanceRepository.GetActiveComponentVersionsAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task<IEnumerable<string>> GetActiveCacheKeysAsync<T>(CancellationToken cancellationToken = default)
        where T : class, IDomainEntity, IReferenceSetter
    {
        var schemaName = runtimeOptions.Value.GetSchemaNameByType(typeof(T));
        var schemaInfo = runtimeOptions.Value.Schemas[schemaName];

        using (currentSchema.Change(schemaInfo.Schema))
        {
            var keys = await instanceRepository.GetActiveInstanceKeysAsync(cancellationToken);
            return keys
                .Select(k => $"{schemaInfo.Name}:{runtimeInfoProvider.Domain}:{k.Key}:{k.Version}")
                .ToList();
        }
    }

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, string version,
        CancellationToken cancellationToken = default) where T : class, IDomainEntity, IReferenceSetter
    {
        var schemaName = runtimeOptions.Value.GetSchemaNameByType(typeof(T));
        var schemaInfo = runtimeOptions.Value.Schemas[schemaName];
        using (currentSchema.Change(schemaInfo.Schema))
        {
            var item = await instanceRepository.FindActiveDataAsync(key, version, cancellationToken);

            if (item == null)
            {
                return null;
            }

            try
            {
                var flow = item.InstanceData.Data.JsonElement
                    .Deserialize<T>(JsonSerializerConstants.JsonOptions);

                if (flow != null)
                {
                    flow.SetReference(new Reference(
                        item.Instance.Key ?? string.Empty,
                        runtimeInfoProvider.Domain,
                        schemaInfo.Name,
                        item.InstanceData.Version
                    ));
                }

                return flow;
            }
            catch (JsonException ex)
            {
                logger.InstanceDeserializationFailed(ex, schemaName, key, version);
                return null;
            }
        }
    }
}
