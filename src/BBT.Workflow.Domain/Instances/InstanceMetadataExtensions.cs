using System.Globalization;
using BBT.Aether;
using BBT.Workflow.Definitions;

namespace BBT.Workflow.Instances;

public static class InstanceMetadataExtensions
{
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
    /// If the key is absent (i.e. this instance IS the root), returns the instance's own <see cref="Instance.Id"/>.
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