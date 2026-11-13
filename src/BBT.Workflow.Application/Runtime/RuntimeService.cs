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
/// Only a malformed row (<see cref="JsonException"/>) is skipped, and logged. Any other failure — above all
/// <see cref="OutOfMemoryException"/> — propagates: skipping a row the process could not afford to load
/// returns a silently incomplete list, and a version resolved from that list is a wrong answer, not a
/// degraded one.
/// </remarks>
public sealed class RuntimeService(
    IInstanceRepository instanceRepository,
    ICurrentSchema currentSchema,
    IOptions<RuntimeOptions> runtimeOptions,
    IRuntimeInfoProvider runtimeInfoProvider,
    ILogger<RuntimeService> logger) : IRuntimeService
{
    private const int PageSize = 100;

    public async Task<IEnumerable<T?>> GetAsync<T>(CancellationToken cancellationToken = default)
        where T : class, IDomainEntity, IReferenceSetter
        => await GetAsync<T>(since: null, cancellationToken);

    public async Task<IEnumerable<T?>> GetAsync<T>(DateTime? since, CancellationToken cancellationToken = default)
        where T : class, IDomainEntity, IReferenceSetter
    {
        var schemaName = runtimeOptions.Value.GetSchemaNameByType(typeof(T));
        var schemaInfo = runtimeOptions.Value.Schemas[schemaName];

        using (currentSchema.Change(schemaInfo.Schema))
        {
            var result = new List<T?>();
            int skip = 0;
            List<InstanceAndDataModel> page;

            do
            {
                page = since.HasValue
                    ? await instanceRepository.GetActiveDataListSinceAsync(since.Value, skip, PageSize, cancellationToken)
                    : await instanceRepository.GetActiveDataListPagedAsync(skip, PageSize, cancellationToken);

                foreach (var item in page)
                {
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

                        result.Add(flow);
                    }
                    catch (JsonException ex)
                    {
                        logger.InstanceDeserializationFailed(ex, schemaName, item.Instance.Key, item.InstanceData.Version);
                    }
                }

                skip += PageSize;
            }
            while (page.Count == PageSize);

            return result.Where(f => f != null);
        }
    }

    public async Task<IEnumerable<T?>> GetAsync<T>(string key, CancellationToken cancellationToken = default)
        where T : class, IDomainEntity, IReferenceSetter
    {
        var schemaName = runtimeOptions.Value.GetSchemaNameByType(typeof(T));
        var schemaInfo = runtimeOptions.Value.Schemas[schemaName];

        using (currentSchema.Change(schemaInfo.Schema))
        {
            var items = await instanceRepository.GetActiveDataListByKeyAsync(key, cancellationToken);
            var result = new List<T?>();

            foreach (var item in items)
            {
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

                    result.Add(flow);
                }
                catch (JsonException ex)
                {
                    logger.InstanceDeserializationFailed(ex, schemaName, item.Instance.Key, item.InstanceData.Version);
                }
            }

            return result.Where(f => f != null);
        }
    }

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
