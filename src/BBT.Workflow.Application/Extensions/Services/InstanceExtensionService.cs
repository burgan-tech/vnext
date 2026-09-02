using System.Extensions;
using System.Text;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Coordinator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Extentions;

/// <summary>
/// Implementation of extension processing service using Railway pattern.
/// </summary>
public sealed class InstanceExtensionService(
    IComponentCacheStore componentCacheStore,
    ITaskCoordinatorExtended taskCoordinator,
    IRuntimeInfoProvider runtimeInfoProvider,
    ICurrentSchema currentSchema,
    IServiceScopeFactory serviceScopeFactory,
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
        using var processActivity = ExtensionActivityHelper.StartProcess(workflow.Key, currentScope);

        var context = new ExtensionProcessingContext(
            new Dictionary<string, object>(),
            new HashSet<string>());
        
        var requestedSet = extensionRequested is { Length: > 0 }
            ? new HashSet<string>(extensionRequested, StringComparer.OrdinalIgnoreCase)
            : null;

        // Process core system extensions first (runtime-wide, always included)
        // Fail-fast: if core extensions fail, return error immediately
        var coreResult = await ProcessCoreExtensionsAsync(requestedSet, scriptContext, workflow.Key, currentScope, context, cancellationToken);
        if (!coreResult.IsSuccess)
        {
            return Result<Dictionary<string, object>>.Fail(coreResult.Error);
        }

        // Process workflow-specific extensions (excluding already executed core extensions)
        // Fail-fast: if workflow extensions fail, return error immediately
        var workflowResult = await ProcessWorkflowExtensionsAsync(
            requestedSet,
            scriptContext,
            workflow,
            currentScope,
            context,
            cancellationToken);
        
        if (!workflowResult.IsSuccess)
        {
            return Result<Dictionary<string, object>>.Fail(workflowResult.Error);
        }

        return Result<Dictionary<string, object>>.Ok(context.Response);
    }

    /// <summary>
    /// Processes core extensions that are runtime-wide and always included in instance responses.
    /// Core extensions provide essential data like state, createBy, etc.
    /// Uses fail-fast behavior: if any core extension fails, returns error immediately.
    /// </summary>
    private async Task<Result> ProcessCoreExtensionsAsync(
        HashSet<string>? extensionRequested,
        ScriptContext scriptContext,
        string workflowKey,
        ExtensionScope currentScope,
        ExtensionProcessingContext context,
        CancellationToken cancellationToken)
    {
        var coreExtensionsResult = await GetCoreExtensionsAsync(extensionRequested, cancellationToken);

        if (!coreExtensionsResult.IsSuccess || coreExtensionsResult.Value!.Count == 0)
            return Result.Ok();

        return await ExecuteExtensionsInternalAsync(
            null,
            scriptContext,
            coreExtensionsResult.Value,
            workflowKey,
            currentScope,
            context,
            cancellationToken);
    }

    /// <summary>
    /// Processes workflow-specific extensions excluding already executed core extensions.
    /// Uses fail-fast behavior: if any workflow extension fails, returns error immediately.
    /// </summary>
    private async Task<Result> ProcessWorkflowExtensionsAsync(
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

        return await ExecuteExtensionsInternalAsync(
            extensionRequested,
            scriptContext,
            filteredExtensions,
            workflow.Key,
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
        using (currentSchema.Change(RuntimeSysSchemaInfo.Extensions))
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
    /// Each fetch runs in its own DI scope to avoid concurrent DbContext access.
    /// Failed fetches are filtered out - the system continues with available extensions.
    /// </summary>
    private async Task<List<Extension>> FetchExtensionsFromReferencesAsync(
        List<IReference> extensionReferences,
        CancellationToken cancellationToken)
    {
        if (extensionReferences.Count == 0)
            return [];

        using var resolveActivity = ExtensionActivityHelper.StartResolve(extensionReferences.Count);

        var extensionTasks = extensionReferences.Select(async reference =>
        {
            await using var scope = serviceScopeFactory.CreateAsyncScope();
            var scopedCacheStore = scope.ServiceProvider.GetRequiredService<IComponentCacheStore>();
            var result = await scopedCacheStore.GetExtensionAsync(reference, cancellationToken);
            return result.IsSuccess ? result.Value : null;
        });

        var extensionResults = await Task.WhenAll(extensionTasks);
        return extensionResults.Where(ext => ext != null).ToList()!;
    }

    /// <summary>
    /// Executes extensions and extracts their responses into the context.
    /// Uses fail-fast behavior: if any extension fails, returns error with target indicating which extension failed.
    /// </summary>
    private async Task<Result> ExecuteExtensionsInternalAsync(
        HashSet<string>? extensionRequested,
        ScriptContext scriptContext,
        List<Extension> extensions,
        string workflowKey,
        ExtensionScope currentScope,
        ExtensionProcessingContext context,
        CancellationToken cancellationToken)
    {
        var executableExtensions = extensions
            .Where(ext => ext.Task != null && ext.ShouldExecute(extensionRequested, currentScope))
            .ToList();

        if (executableExtensions.Count == 0)
            return Result.Ok();

        var tasks = executableExtensions.Select(ext => ext.Task);

        // Two extensions can share a task Reference while applying different Mapping/Order —
        // their outputs are supposed to differ (OnExecuteTask carries Mapping/ErrorBoundary per
        // entry; Task is only a Reference). Keyed by OnExecuteTask identity — each extension owns
        // its own OnExecuteTask instance even when it points at a shared task — so the per-task
        // options refiner below can look up which extension a given execution belongs to and file
        // its response under the EXTENSION's own variable name instead of the shared task's. Task
        // key alone let the second extension's write silently clobber the first's entry
        // (sequential orders) or collide during the parallel merge with
        // InvalidOperationException: "Parallel tasks produced conflicting output for key '...'"
        // (same order) — the Preprod fault this fixes.
        //
        // Built with a plain last-wins loop, NOT ToDictionary: WorkflowValidator has no uniqueness
        // check on Extensions, so a workflow can legally list the SAME extension reference twice.
        // FetchExtensionsFromReferencesAsync resolves references in parallel, and
        // CacheSet._inFlightResolutions coalesces concurrent identical resolutions into one
        // Lazy<Task<Result<T>>> — so both fetches hand back the SAME Extension instance, hence the
        // SAME OnExecuteTask instance (OnExecuteTask is a sealed class with no equality override,
        // so this is a genuine duplicate KEY, not just a duplicate value). ToDictionary throws
        // ArgumentException on that duplicate key, breaking a read that worked before this fix
        // (both executions produced identical values pre-fix, so the merge's JsonEquivalent check
        // accepted them). Last-wins never throws, and is also semantically correct here: a
        // duplicated reference belongs to one extension, so every entry maps to the same key
        // anyway.
        // A key already present here means the SAME OnExecuteTask instance came around twice —
        // which only happens when the SAME extension reference is listed more than once (see the
        // CacheSet-coalescing note above). That is a genuinely different shape from two DIFFERENT
        // extensions sharing a task Reference (each owns its own OnExecuteTask instance and never
        // collides here): it is one extension's task running twice for one output slot, still able
        // to throw the parallel-merge conflict this whole fix exists to prevent. The last-wins
        // write below already tolerates it without throwing; this only adds the diagnostic that
        // TaskCoordinator.LogDuplicateTaskKeysIfAny cannot provide for Extension-origin executions.
        var responseKeyByTask = new Dictionary<OnExecuteTask, string>();
        foreach (var ext in executableExtensions)
        {
            if (responseKeyByTask.ContainsKey(ext.Task))
            {
                logger.DuplicateExtensionReference(ext.Key, workflowKey);
            }

            responseKeyByTask[ext.Task] = ext.Key.ToVariableName();
        }

        // Execute tasks with fail-fast behavior.
        // Note: if a task has AcceptedStatusCodes and the response status code matches,
        // TaskExecutorBase overrides IsSuccess to true so the result arrives here as successful —
        // the extension continues normally without interruption.
        // ExecuteWithDetailsAsync (not the base ExecuteAsync) is required here: only its
        // optionsRefiner lets each task's TaskEngineExecutionOptions.ResponseVariableKey be set
        // per extension, on top of whatever ResolveGroupEngineOptions already resolved.
        var detailsResult = await taskCoordinator.ExecuteWithDetailsAsync(
            tasks,
            null,
            TaskTrigger.Extension,
            TaskExecutionOrigin.Extension,
            scriptContext,
            completedTaskIds: [],
            skipJournalProbe: false,
            optionsRefiner: (task, options) =>
            {
                // TryGetValue, not the indexer: a task not found here would be a caller bug
                // (TaskCoordinator only ToList/Where/GroupBy's the same instances, never clones
                // them), but degrading to null — which falls back to today's task-key behavior in
                // TaskExecutorBase — is the right failure mode rather than a thrown
                // KeyNotFoundException. Unreachable today, but a miss here means the extension's
                // output files under the task-derived key while ExtractExtensionResponse only ever
                // reads by the extension's own key — silent data loss — so it is logged rather than
                // left to fail quietly if this assumption is ever broken.
                if (!responseKeyByTask.TryGetValue(task, out var key))
                {
                    logger.ExtensionResponseKeyMappingMissing(task.Task.Key, scriptContext.Instance?.Id);
                }

                return options with { ResponseVariableKey = key };
            },
            cancellationToken: cancellationToken);

        var executeResult = CollapseExecutionResult(detailsResult);

        if (!executeResult.IsSuccess)
        {
            var failedExtensionKey = FindFailedExtensionKey(executableExtensions, scriptContext);
            
            logger.LogError(
                "Extension execution failed for '{ExtensionKey}': {ErrorCode} - {ErrorMessage}",
                failedExtensionKey,
                executeResult.Error.Code,
                executeResult.Error.Message);
            
            return Result.Fail(WorkflowErrors.ExtensionExecutionFailed(
                failedExtensionKey,
                executeResult.Error.Message ?? "Unknown error"));
        }

        // Extract responses from executed extensions
        foreach (var extension in executableExtensions)
        {
            ExtractExtensionResponse(extension, scriptContext, context);
        }
        
        return Result.Ok();
    }

    /// <summary>
    /// Collapses the two-level <see cref="TasksExecutionResult"/> outcome — infrastructure-level
    /// <see cref="Result{T}"/> failure vs. business-level <c>TasksExecutionResult.IsSuccess</c>
    /// failure — into a single <see cref="Result"/>, exactly as <c>TaskCoordinator.ExecuteAsync</c>
    /// does internally. This service used to call that overload directly; it now calls
    /// <c>ExecuteWithDetailsAsync</c> instead (required for the per-task <c>optionsRefiner</c>), so
    /// it must do the same collapsing itself.
    /// </summary>
    private static Result CollapseExecutionResult(Result<TasksExecutionResult> detailsResult)
    {
        if (!detailsResult.IsSuccess)
            return Result.Fail(detailsResult.Error);

        if (!detailsResult.Value!.IsSuccess)
        {
            var error = detailsResult.Value.TaskError?.ToError() ??
                        Error.Failure("TaskExecutionFailed", "One or more tasks failed");
            return Result.Fail(error);
        }

        return Result.Ok();
    }

    /// <summary>
    /// Finds the key of the extension that failed execution.
    /// Identifies the failed extension by checking which EXTENSION's own response is missing from
    /// the script context — not which task's, since two extensions can share one task Reference and
    /// a task-keyed check cannot tell them apart (a successful sibling's write under the shared task
    /// key masks the actually-failed extension).
    /// </summary>
    /// <param name="extensions">List of extensions that were executed.</param>
    /// <param name="scriptContext">The script context containing task outputs.</param>
    /// <returns>The key of the failed extension, or "unknown" if not determinable.</returns>
    private static string FindFailedExtensionKey(
        List<Extension> extensions,
        ScriptContext scriptContext)
    {
        // Find extension whose own response is missing (failed)
        foreach (var extension in extensions)
        {
            var variableKey = extension.Key.ToVariableName();
            if (!scriptContext.OutputResponse.ContainsKey(variableKey))
                return extension.Key;
        }

        return extensions.FirstOrDefault()?.Key ?? "unknown";
    }

    /// <summary>
    /// Extracts the response from an executed extension and adds it to the context.
    /// Reads by the EXTENSION's own variable key (set via
    /// <see cref="TaskEngineExecutionOptions.ResponseVariableKey"/> at execution time), not the
    /// task's — two extensions sharing a task must each get their own mapped output, never each
    /// other's.
    /// </summary>
    private static void ExtractExtensionResponse(
        Extension extension,
        ScriptContext scriptContext,
        ExtensionProcessingContext context)
    {
        var variableKeyExtension = extension.Key.ToVariableName();

        if (!scriptContext.OutputResponse.TryGetValue(variableKeyExtension, out var value))
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
