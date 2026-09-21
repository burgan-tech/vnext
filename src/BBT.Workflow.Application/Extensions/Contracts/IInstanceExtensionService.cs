using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;

namespace BBT.Workflow.Extentions;

/// <summary>
/// Service responsible for processing instance extensions to enrich data responses.
/// Extensions provide additional data from various sources using the TaskExecution infrastructure.
/// All methods return Result types following Railway pattern.
/// </summary>
public interface IInstanceExtensionService
{
    /// <summary>
    /// Processes extensions for the given instance and returns enriched data.
    /// </summary>
    /// <param name="extensionRequested">Array of specifically requested extensions</param>
    /// <param name="scriptContext">Script context for extension execution</param>
    /// <param name="workflow">Workflow containing extensions</param>
    /// <param name="currentScope">Current execution scope to determine which extensions should run</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Result containing dictionary of extension results</returns>
    Task<Result<Dictionary<string, object>>> ProcessExtensionsAsync(
        string[]? extensionRequested,
        ScriptContext scriptContext,
        Definitions.Workflow workflow,
        ExtensionScope currentScope,
        CancellationToken cancellationToken = default);
}

/// <summary>Creates an isolated list operation that reuses extension definitions, never execution results.</summary>
public interface IListExtensionServiceFactory
{
    /// <summary>Returns an extension processor owned by one sequential list invocation.</summary>
    IInstanceExtensionService CreateForList();
}
