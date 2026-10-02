using System.Text.Json;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting.Sandbox;

namespace BBT.Workflow.Definitions.Validators;

/// <summary>
/// Publish-time check that every assembly a component lists under <c>scripts.allowedAssemblies</c> —
/// flow level or on any script slot — resolves in this runtime. Without it an unresolvable name is
/// dropped silently by the sandboxed compile and the author only learns about it from a CS0012/CS1069
/// fault mid-transition.
/// </summary>
/// <remarks>
/// Walks the raw <c>attributes</c> JSON instead of the materialised definitions so that every slot of
/// every component type is covered by one rule, including slots added later. Only a <c>scripts</c>
/// object whose <c>allowedAssemblies</c> is an array is inspected, and only for the component types
/// whose schema declares <c>scripts</c>.
/// </remarks>
public static class AllowedAssembliesPublishCheck
{
    private const string ScriptsProperty = "scripts";
    private const string AllowedAssembliesProperty = "allowedAssemblies";

    /// <summary>True for the component types whose schema can carry <c>scripts</c>.</summary>
    public static bool AppliesTo(string componentType) =>
        componentType is RuntimeSysSchemaInfo.Flows
            or RuntimeSysSchemaInfo.Tasks
            or RuntimeSysSchemaInfo.Functions
            or RuntimeSysSchemaInfo.Extensions;

    /// <summary>Appends one error per declared assembly name the catalog cannot resolve.</summary>
    public static void Validate(
        string componentType,
        JsonElement attributes,
        IScriptAssemblyCatalog catalog,
        ComponentValidationResult result)
    {
        Walk(attributes, componentType, catalog, result);
    }

    private static void Walk(JsonElement node, string path, IScriptAssemblyCatalog catalog, ComponentValidationResult result)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in node.EnumerateObject())
                {
                    var childPath = $"{path}.{property.Name}";
                    if (property.Value.ValueKind == JsonValueKind.Object &&
                        property.Name.Equals(ScriptsProperty, StringComparison.OrdinalIgnoreCase))
                    {
                        CheckScripts(property.Value, childPath, catalog, result);
                    }

                    Walk(property.Value, childPath, catalog, result);
                }

                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in node.EnumerateArray())
                {
                    Walk(item, $"{path}[{index}]", catalog, result);
                    index++;
                }

                break;
        }
    }

    private static void CheckScripts(JsonElement scripts, string path, IScriptAssemblyCatalog catalog, ComponentValidationResult result)
    {
        foreach (var property in scripts.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Array ||
                !property.Name.Equals(AllowedAssembliesProperty, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var index = 0;
            foreach (var item in property.Value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    var name = item.GetString() ?? string.Empty;
                    if (!catalog.IsAvailable(name))
                    {
                        var member = $"{path}.{property.Name}[{index}]";
                        result.AddError(
                            $"Assembly '{name}' declared in '{member}' is not available in this runtime " +
                            "(neither a framework assembly nor in the plugin directory). Use the simple " +
                            "assembly name without extension, or have the assembly mounted by the platform team.",
                            member);
                    }
                }

                index++;
            }
        }
    }
}
