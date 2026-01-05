using System.Extensions;
using System.Text;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Coordinator;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Extentions;

/// <summary>
/// Implementation of extension processing service using Railway pattern.
/// </summary>
public sealed class InstanceExtensionService(
    IComponentCacheStore componentCacheStore,
    ITaskCoordinator taskCoordinator,
    IRuntimeInfoProvider runtimeInfoProvider,
    ICurrentSchema currentSchema,
    ILogger<InstanceExtensionService> logger) : IInstanceExtensionService
{
    /// <inheritdoc />
    public async Task<Result<Dictionary<string, object>>> ProcessExtensionsAsync(
        string[]? extensionRequested,
        ScriptContext scriptContext,
        Definitions.Workflow workflow,
        ExtensionScope currentScope,
        CancellationToken cancellationToken = default)
    {
        var context = new ExtensionProcessingContext(
            new Dictionary<string, object>(),
            new HashSet<string>());
        
        var requestedSet = extensionRequested is { Length: > 0 }
            ? new HashSet<string>(extensionRequested, StringComparer.OrdinalIgnoreCase)
            : null;

        // Process core system extensions first (runtime-wide, always included)
        // Core extensions failing should not block workflow extensions
        await ProcessCoreExtensionsAsync(requestedSet, scriptContext, currentScope, context, cancellationToken);

        // Process workflow-specific extensions (excluding already executed core extensions)
        await ProcessWorkflowExtensionsAsync(
            requestedSet,
            scriptContext,
            workflow,
            currentScope,
            context,
            cancellationToken);

        return Result<Dictionary<string, object>>.Ok(context.Response);
    }

    /// <summary>
    /// Processes core extensions that are runtime-wide and always included in instance responses.
    /// Core extensions provide essential data like state, createBy, etc.
    /// Failures here are logged but don't break the flow - the system continues without core extensions.
    /// </summary>
    private async Task ProcessCoreExtensionsAsync(
        HashSet<string>? extensionRequested,
        ScriptContext scriptContext,
        ExtensionScope currentScope,
        ExtensionProcessingContext context,
        CancellationToken cancellationToken)
    {
        var coreExtensionsResult = await GetCoreExtensionsAsync(extensionRequested, cancellationToken);

        if (!coreExtensionsResult.IsSuccess || coreExtensionsResult.Value!.Count == 0)
            return;

        await ExecuteExtensionsInternalAsync(
            null,
            scriptContext,
            coreExtensionsResult.Value,
            currentScope,
            context,
            cancellationToken);
    }

    /// <summary>
    /// Processes workflow-specific extensions excluding already executed core extensions.
    /// </summary>
    private async Task ProcessWorkflowExtensionsAsync(
        HashSet<string>? extensionRequested,
        ScriptContext scriptContext,
        Definitions.Workflow workflow,
        ExtensionScope currentScope,
        ExtensionProcessingContext context,
        CancellationToken cancellationToken)
    {
        var extensionReferences = workflow.Extensions.ToList();
        var extensions = await FetchExtensionsFromReferencesAsync(extensionReferences, cancellationToken);

        // Filter out extensions that were already executed as core extensions
        var filteredExtensions = extensions
            .Where(ext => !context.ExecutedKeys.Contains(ext.Key))
            .ToList();

        await ExecuteExtensionsInternalAsync(
            extensionRequested,
            scriptContext,
            filteredExtensions,
            currentScope,
            context,
            cancellationToken);
    }

    /// <summary>
    /// Retrieves all core extensions from the cache using Railway pattern.
    /// - Global extensions are always included.
    /// - GlobalAndRequested extensions are included only if they are in the extensionRequested list.
    /// </summary>
    /// <param name="extensionRequested">The list of requested extension keys.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    private async Task<Result<List<Extension>>> GetCoreExtensionsAsync(
        HashSet<string>? extensionRequested,
        CancellationToken cancellationToken)
    {
        using (currentSchema.Use(RuntimeSysSchemaInfo.Extensions))
        {
            var allExtensionsResult = await componentCacheStore.GetAllExtensionsAsync(
                runtimeInfoProvider.Domain,
                cancellationToken);
            
            return allExtensionsResult
                .Map(extensions => extensions
                    .Where(ext => IsGlobalExtensionIncluded(ext, extensionRequested))
                    .ToList());
        }
    }

    /// <summary>
    /// Determines if a global extension should be included based on its type and request status.
    /// </summary>
    /// <param name="extension">The extension to check.</param>
    /// <param name="requestedSet">The set of requested extension keys (null if none requested).</param>
    /// <returns>True if the extension should be included.</returns>
    private static bool IsGlobalExtensionIncluded(Extension extension, HashSet<string>? requestedSet)
    {
        return extension.Type switch
        {
            // Global extensions are always included
            ExtensionType.Global => true,

            // GlobalAndRequested extensions are included only if explicitly requested
            ExtensionType.GlobalAndRequested => requestedSet?.Contains(extension.Key) == true,

            // Other types are not core extensions
            _ => false
        };
    }

    /// <summary>
    /// Fetches extensions from references in parallel.
    /// Failed fetches are filtered out - the system continues with available extensions.
    /// </summary>
    private async Task<List<Extension>> FetchExtensionsFromReferencesAsync(
        List<IReference> extensionReferences,
        CancellationToken cancellationToken)
    {
        if (extensionReferences.Count == 0)
            return [];

        var extensionTasks = extensionReferences.Select(async reference =>
        {
            var result = await componentCacheStore.GetExtensionAsync(reference, cancellationToken);
            return result.IsSuccess ? result.Value : null;
        });

        var extensionResults = await Task.WhenAll(extensionTasks);
        return extensionResults.Where(ext => ext != null).ToList()!;
    }

    /// <summary>
    /// Executes extensions and extracts their responses into the context.
    /// Extension execution errors are logged but don't propagate - extensions are best-effort enrichment.
    /// </summary>
    private async Task ExecuteExtensionsInternalAsync(
        HashSet<string>? extensionRequested,
        ScriptContext scriptContext,
        List<Extension> extensions,
        ExtensionScope currentScope,
        ExtensionProcessingContext context,
        CancellationToken cancellationToken)
    {
        var executableExtensions = extensions
            .Where(ext => ext.Task != null && ext.ShouldExecute(extensionRequested, currentScope))
            .ToList();

        if (executableExtensions.Count == 0)
            return;

        var tasks = executableExtensions.Select(ext => ext.Task);

        // Execute tasks and log errors (extensions are best-effort, don't fail the chain)
        var executeResult = await taskCoordinator.ExecuteAsync(
            tasks,
            null,
            TaskTrigger.Extension,
            scriptContext,
            cancellationToken);

        if (!executeResult.IsSuccess)
        {
            logger.LogWarning(
                "Extension task execution failed: {ErrorCode} - {ErrorMessage}. Extensions are best-effort, continuing.",
                executeResult.Error.Code,
                executeResult.Error.Message);
        }

        // Extract responses from executed extensions (even partial results)
        foreach (var extension in executableExtensions)
        {
            ExtractExtensionResponse(extension, scriptContext, context);
        }
    }

    /// <summary>
    /// Extracts the response from an executed extension and adds it to the context.
    /// </summary>
    private static void ExtractExtensionResponse(
        Extension extension,
        ScriptContext scriptContext,
        ExtensionProcessingContext context)
    {
        var variableKeyExtension = extension.Key.ToVariableName();
        var variableKeyTask = extension.Task.Task.Key.ToVariableName();

        if (!scriptContext.OutputResponse.TryGetValue(variableKeyTask, out var value))
            return;

        context.Response[variableKeyExtension] = value!;
        context.ExecutedKeys.Add(extension.Key);
    }

    /// <summary>
    /// Internal context for tracking extension processing state.
    /// </summary>
    private sealed record ExtensionProcessingContext(
        Dictionary<string, object> Response,
        HashSet<string> ExecutedKeys);
}
