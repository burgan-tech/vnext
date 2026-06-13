using System.Text.Json.Serialization;
using BBT.Aether.Domain.Values;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Script-level settings shared by a mapping (<c>mapping.scripts</c>) and a workflow definition
/// (flow-level <c>scripts</c>). Declares the helper components a script may call and the per-compile
/// sandbox assembly grant. Flow-level and task/mapping-level settings are unioned at compile time
/// (see <see cref="Union"/>).
/// </summary>
public sealed class ScriptSettings : ValueObject
{
    /// <summary>
    /// References to mapping (script-library) components (<c>sys-mappings</c>) whose public types the
    /// script may call. The referenced set is compiled (sandboxed, cached) before the script, its
    /// assembly referenced, and its public namespaces auto-imported.
    /// </summary>
    public IReadOnlyList<Reference>? Helpers { get; private set; }

    /// <summary>
    /// Per-compile sandbox grant: assembly simple names merged on top of the global baseline
    /// (<c>Scripting:Sandbox:AllowedAssemblies</c>). Resolves only against assemblies actually available
    /// (framework TPA + operator-mounted plugins); the mandatory banned-namespace baseline always applies.
    /// </summary>
    public IReadOnlyList<string>? AllowedAssemblies { get; private set; }

    private ScriptSettings()
    {
    }

    [JsonConstructor]
    public ScriptSettings(
        IReadOnlyList<Reference>? helpers = null,
        IReadOnlyList<string>? allowedAssemblies = null)
    {
        Helpers = helpers is { Count: > 0 } ? helpers : null;
        AllowedAssemblies = allowedAssemblies is { Count: > 0 } ? allowedAssemblies : null;
    }

    /// <summary>
    /// True when at least one helper reference is declared.
    /// </summary>
    [JsonIgnore]
    public bool HasHelpers => Helpers is { Count: > 0 };

    /// <summary>
    /// Unions a flow-level and a task/mapping-level settings object: helper references are concatenated
    /// and de-duplicated by <c>domain/flow/key/version</c>; allowed assemblies are distinct-merged
    /// (case-insensitive). Returns <c>null</c> when both inputs are null/empty.
    /// </summary>
    public static ScriptSettings? Union(ScriptSettings? flow, ScriptSettings? task)
    {
        if (flow is null)
        {
            return task;
        }

        if (task is null)
        {
            return flow;
        }

        var helpers = new List<Reference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in (flow.Helpers ?? []).Concat(task.Helpers ?? []))
        {
            if (seen.Add(reference.ToString()))
            {
                helpers.Add(reference);
            }
        }

        var assemblies = (flow.AllowedAssemblies ?? [])
            .Concat(task.AllowedAssemblies ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new ScriptSettings(
            helpers.Count > 0 ? helpers : null,
            assemblies.Length > 0 ? assemblies : null);
    }

    protected override IEnumerable<object> GetAtomicValues()
    {
        if (Helpers is { Count: > 0 })
        {
            foreach (var helper in Helpers)
            {
                yield return helper.ToString();
            }
        }

        if (AllowedAssemblies is { Count: > 0 })
        {
            foreach (var assembly in AllowedAssemblies)
            {
                yield return assembly;
            }
        }
    }
}
