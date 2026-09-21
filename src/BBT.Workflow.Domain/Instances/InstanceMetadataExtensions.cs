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

    /// <summary>
    /// Resolves the timeout actually in force for this instance: the parent-supplied SubFlow
    /// override stamped at start (<see cref="DomainConsts.MetaDataKeys.TimeoutOverride"/>) when one
    /// is present, otherwise the workflow's own <see cref="Definitions.Workflow.Timeout"/>. Returns
    /// <c>null</c> when neither exists — a valid answer meaning "this instance has no timeout".
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One resolver, four call sites, deliberately.</b> The arm
    /// (<c>InstanceCommandAppService.ScheduleWorkflowTimeoutIfConfiguredAsync</c>), the fire path
    /// (<c>FlowTimeoutJobHandler</c> and <c>ApplyTimeoutStateStep</c>) and the state function's
    /// <c>timeout</c> block all answer the same question, so none of them may answer it alone.
    /// Before this existed the override was consumed by the arm and read nowhere else: a child whose
    /// own definition carried no <c>timeout</c> had a job armed from the override's timer and then
    /// fired into <c>TimeoutConfigMissing</c> — an advertised deadline after which nothing happened,
    /// reproducible against vnext-example's <c>subflow-orchestration</c> fixture. Publishing a
    /// deadline and honouring it are now the same fact.
    /// </para>
    /// <para>
    /// Malformed metadata degrades to the workflow's own timeout instead of throwing, and
    /// <paramref name="overrideMalformed"/> reports it so the caller can log through
    /// <c>WorkflowLogs</c>. Neither surface may fail on it: the state function would answer 500 on
    /// its hottest read, and the arm deliberately treats a scheduling failure as non-fatal (the
    /// start proceeds). <see cref="JsonException"/> covers a broken document;
    /// <see cref="ArgumentException"/> covers a well-formed one whose values fail the
    /// <see cref="WorkflowTimeout"/> constructor's own <c>Check</c> guards, which System.Text.Json
    /// does not wrap.
    /// </para>
    /// <para>
    /// The shared <see cref="JsonSerializerConstants.JsonOptions"/> are mandatory here, matching
    /// <c>SubFlowTransitionOverrideReader</c>: <see cref="WorkflowTimeout"/>'s properties are
    /// PascalCase and its constructor guards <c>Key</c> and <c>Target</c> with
    /// <c>Check.NotNullOrWhiteSpace</c>, so a case-sensitive read would leave them null and throw —
    /// every override would look malformed. The same options carry
    /// <c>ScriptCodeJsonConverter</c>, which the <c>Mapping</c> member needs, so the writer
    /// (<c>SubflowStarter</c>) uses them too; <c>PropertyNameCaseInsensitive</c> keeps stamps
    /// written by earlier runtimes readable.
    /// </para>
    /// </remarks>
    public static WorkflowTimeout? ResolveEffectiveTimeout(
        this ExtraPropertyDictionary? metaData,
        Definitions.Workflow workflow,
        out bool overrideMalformed)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        overrideMalformed = false;

        // Gate before parsing: an instance with no override pays nothing on the read path.
        if (metaData is null
            || !metaData.TryGetValue(DomainConsts.MetaDataKeys.TimeoutOverride, out var raw)
            || raw is null)
        {
            return workflow.Timeout;
        }

        var json = raw.ToString();
        if (string.IsNullOrWhiteSpace(json))
            return workflow.Timeout;

        try
        {
            return JsonSerializer.Deserialize<WorkflowTimeout>(json, JsonSerializerConstants.JsonOptions)
                   ?? workflow.Timeout;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            overrideMalformed = true;
            return workflow.Timeout;
        }
    }

    /// <inheritdoc cref="ResolveEffectiveTimeout(ExtraPropertyDictionary?, Definitions.Workflow, out bool)"/>
    public static WorkflowTimeout? ResolveEffectiveTimeout(
        this ExtraPropertyDictionary? metaData,
        Definitions.Workflow workflow)
        => metaData.ResolveEffectiveTimeout(workflow, out _);

    /// <inheritdoc cref="ResolveEffectiveTimeout(ExtraPropertyDictionary?, Definitions.Workflow, out bool)"/>
    public static WorkflowTimeout? ResolveEffectiveTimeout(
        this Instance instance,
        Definitions.Workflow workflow,
        out bool overrideMalformed)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return instance.ExtraProperties.ResolveEffectiveTimeout(workflow, out overrideMalformed);
    }

    /// <inheritdoc cref="ResolveEffectiveTimeout(ExtraPropertyDictionary?, Definitions.Workflow, out bool)"/>
    public static WorkflowTimeout? ResolveEffectiveTimeout(
        this Instance instance,
        Definitions.Workflow workflow)
        => instance.ResolveEffectiveTimeout(workflow, out _);

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