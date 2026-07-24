using System.Globalization;
using System.Text.Json;
using BBT.Aether;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Instances;

public static class InstanceMetadataExtensions
{
    /// <summary>
    /// Records a distributed resource-lock key acquired by this instance so that the instance's
    /// terminal cleanup can release it automatically (owner = instance ID), regardless of which
    /// transition path completes/faults the instance. Idempotent: a key already tracked is ignored.
    /// The key set is stored as a JSON array string under <see cref="DomainConsts.MetaDataKeys.ResourceLocks"/>.
    /// </summary>
    public static void TrackResourceLock(this Instance instance, string resourceKey)
    {
        if (instance is null)
            throw new ArgumentNullException(nameof(instance));
        if (string.IsNullOrWhiteSpace(resourceKey))
            return;

        var keys = instance.GetTrackedResourceLocks().ToList();
        if (keys.Contains(resourceKey))
            return;

        keys.Add(resourceKey);

        var metadata = new ExtraPropertyDictionary(instance.ExtraProperties ?? new ExtraPropertyDictionary())
        {
            [DomainConsts.MetaDataKeys.ResourceLocks] = JsonSerializer.Serialize(keys)
        };
        instance.SetMetaData(metadata);
    }

    /// <summary>
    /// Returns the distributed resource-lock keys currently tracked for this instance
    /// (see <see cref="TrackResourceLock"/>). Never returns null; tolerates missing or malformed metadata.
    /// </summary>
    public static IReadOnlyList<string> GetTrackedResourceLocks(this Instance instance)
    {
        if (instance is null)
            throw new ArgumentNullException(nameof(instance));

        var raw = GetString(instance.ExtraProperties, DomainConsts.MetaDataKeys.ResourceLocks);
        if (string.IsNullOrWhiteSpace(raw))
            return Array.Empty<string>();

        try
        {
            return JsonSerializer.Deserialize<List<string>>(raw) ?? (IReadOnlyList<string>)Array.Empty<string>();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    public static SubFlowContractInfo ToSubFlowContractInfo(this Instance instance)
    {
        if (instance is null)
            throw new ArgumentNullException(nameof(instance));

        var md = instance.ExtraProperties;

        return new SubFlowContractInfo
        {
            Id      = GetGuid(md, DomainConsts.MetaDataKeys.Id),
            Key     = GetString(md, DomainConsts.MetaDataKeys.Key),
            Domain  = GetString(md, DomainConsts.MetaDataKeys.Domain) ?? string.Empty,
            Flow    = GetString(md, DomainConsts.MetaDataKeys.Flow) ?? string.Empty,
            Version = GetString(md, DomainConsts.MetaDataKeys.Version),
            State   = GetString(md, DomainConsts.MetaDataKeys.State),
            Transition   = GetString(md, DomainConsts.MetaDataKeys.Transition),
            SubType = GetString(md, DomainConsts.MetaDataKeys.FlowType) ?? string.Empty,
            
        };
    }
    
    public static SubFlowContractInfo ToSubFlowContractInfo(this ExtraPropertyDictionary metaData)
    {
        return new SubFlowContractInfo
        {
            Id      = GetGuid(metaData, DomainConsts.MetaDataKeys.Id),
            Key     = GetString(metaData, DomainConsts.MetaDataKeys.Key),
            Domain  = GetString(metaData, DomainConsts.MetaDataKeys.Domain) ?? string.Empty,
            Flow    = GetString(metaData, DomainConsts.MetaDataKeys.Flow) ?? string.Empty,
            Version = GetString(metaData, DomainConsts.MetaDataKeys.Version),
            State   = GetString(metaData, DomainConsts.MetaDataKeys.State),
            Transition   = GetString(metaData, DomainConsts.MetaDataKeys.Transition),
            SubType = GetString(metaData, DomainConsts.MetaDataKeys.FlowType) ?? string.Empty
        };
    }

    public static WorkflowType? ToFlowType(this Instance instance)
    {
        var md = instance.ExtraProperties;
        var type = GetString(md, DomainConsts.MetaDataKeys.FlowType);
        return !string.IsNullOrEmpty(type) 
            ? WorkflowType.FromCode(type)
            : null;
    }
    
    private static string? GetString(ExtraPropertyDictionary md, string key)
    {
        if (!md.TryGetValue(key, out var raw) || raw is null)
            return null;

        return raw switch
        {
            string s => s,
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => raw.ToString()
        };
    }

    private static Guid GetGuid(ExtraPropertyDictionary md, string key)
    {
        if (!md.TryGetValue(key, out var raw) || raw is null)
            return Guid.Empty;

        Guid.TryParse(raw.ToString(), out var g);
        return g;
    }

    public static T? GetValue<T>(this Instance instance, string key)
    {
        if (instance.ExtraProperties == null)
            return default;

        if (instance.ExtraProperties.TryGetValue(key, out var value) && value is T typed)
            return typed;

        return default;
    }

    /// <summary>
    /// Returns the root (ancestor) instance ID stored in <see cref="DomainConsts.MetaDataKeys.RootInstanceId"/>.
    /// If the key is absent (i.e. this instance IS the root), returns the instance's own <c>Id</c>.
    /// </summary>
    public static Guid GetRootInstanceId(this Instance instance)
    {
        if (instance.ExtraProperties != null
            && instance.ExtraProperties.TryGetValue(DomainConsts.MetaDataKeys.RootInstanceId, out var raw)
            && raw != null
            && Guid.TryParse(raw.ToString(), out var rootId)
            && rootId != Guid.Empty)
        {
            return rootId;
        }

        return instance.Id;
    }
}