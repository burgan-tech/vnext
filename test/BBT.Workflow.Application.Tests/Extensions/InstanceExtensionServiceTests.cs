using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.ErrorHandling;
using BBT.Workflow.Extentions;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Coordinator;
using BBT.Workflow.Tasks.Evaluation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Extensions;

/// <summary>
/// Unit tests for fail-fast extension execution behavior.
/// Tests verify error codes, error factory methods, and target tracking.
/// </summary>
/// <remarks>
/// Also pins the fix for the Preprod fault (trace <c>7873ad4e6c7a9db31c3b6401cd2c54fc</c>): two
/// extensions on one workflow referencing the SAME task filed their result under the same
/// task-derived variable name. Sequentially this silently overwrote one extension's output with
/// the other's; run in parallel (same <c>Order</c>) it threw
/// <c>InvalidOperationException: Parallel tasks produced conflicting output for key '...'</c> out
/// of <c>ScriptContext.MergeDictionary</c>. The fix keys each execution's response by the
/// EXTENSION (<see cref="TaskEngineExecutionOptions.ResponseVariableKey"/>), not the task — see
/// <c>InstanceExtensionService.ExecuteExtensionsInternalAsync</c>.
/// </remarks>
public class InstanceExtensionServiceTests
{
    [Fact]
    public void WorkflowErrors_ExtensionExecutionFailed_ShouldIncludeTarget()
    {
        // Arrange
        var extensionKey = "test-extension";
        var message = "Connection timeout";

        // Act
        var error = WorkflowErrors.ExtensionExecutionFailed(extensionKey, message);

        // Assert
        error.Code.ShouldBe(WorkflowErrorCodes.ExtensionExecutionFailed);
        error.Target.ShouldBe(extensionKey);
        error.Message.ShouldNotBeNull();
        error.Message!.ShouldContain(extensionKey);
        error.Message.ShouldContain(message);
    }

    [Fact]
    public void WorkflowErrorCodes_ShouldHaveExtensionExecutionFailedCode()
    {
        // Assert
        WorkflowErrorCodes.ExtensionExecutionFailed.ShouldBe("Extension:600001");
    }

    [Fact]
    public void WorkflowErrors_ExtensionExecutionFailed_ShouldFormatMessageCorrectly()
    {
        // Arrange
        var extensionKey = "my-data-extension";
        var message = "HTTP 500 - Internal Server Error";

        // Act
        var error = WorkflowErrors.ExtensionExecutionFailed(extensionKey, message);

        // Assert
        error.Message.ShouldNotBeNull();
        error.Message!.ShouldBe($"Extension '{extensionKey}' execution failed: {message}");
    }

    [Fact]
    public void WorkflowErrors_ExtensionExecutionFailed_ShouldBeValidationError()
    {
        // Arrange
        var extensionKey = "test-extension";
        var message = "Test error";

        // Act
        var error = WorkflowErrors.ExtensionExecutionFailed(extensionKey, message);

        // Assert - Error.Validation creates an error with Type = Validation
        // This ensures proper HTTP status code mapping (400 for validation errors)
        error.Code.ShouldStartWith("Extension:");
    }

    /// <summary>
    /// Contract 1 (the silent path): two extensions reference the SAME task at DIFFERENT
    /// <c>Order</c> values with DIFFERENT <c>Mapping</c>s. Each runs in its own single-task group
    /// (no parallel merge involved), so before the fix the second extension's write to the shared
    /// task-derived key silently overwrote the first's — both extensions' entries in the response
    /// end up holding the SECOND extension's mapped value. After the fix each extension's own
    /// value survives under its own key.
    /// </summary>
    [Fact]
    public async Task ProcessExtensionsAsync_TwoExtensionsSameTaskDifferentMappingDifferentOrder_EachGetsOwnMappedOutput()
    {
        var engine = Substitute.For<ITaskExecutionEngine>();
        var errorFactory = new ExecutionErrorFactory(new ErrorNormalizer());
        StubEngineWithMarkerOutputs(engine, errorFactory);

        var extensionA = CreateExtension("extension-a", taskKey: "shared-task", order: 1, mappingMarker: "mapped-by-a");
        var extensionB = CreateExtension("extension-b", taskKey: "shared-task", order: 2, mappingMarker: "mapped-by-b");

        var componentCacheStore = CreateComponentCacheStore(extensionA, extensionB);
        var service = CreateService(componentCacheStore, CreateTaskCoordinator(engine));
        using var scriptContext = CreateScriptContext();
        var workflow = WorkflowFactory.CreateDefault();

        var result = await service.ProcessExtensionsAsync(
            null, scriptContext, workflow, ExtensionScope.Everywhere, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        result.Value!["extensionA"].ToString().ShouldBe("mapped-by-a");
        result.Value!["extensionB"].ToString().ShouldBe("mapped-by-b");
    }

    /// <summary>
    /// Contract 2 (the fault this fixes): two extensions reference the SAME task at the SAME
    /// <c>Order</c> — the parallel path (<c>TaskCoordinator.ExecuteTaskGroupInParallelAsync</c>).
    /// Before the fix both wrote their (different) mapped output under the identical task-derived
    /// key; merging the second parallel branch into the first threw
    /// <c>InvalidOperationException: "Parallel tasks produced conflicting output for key '...'"</c>
    /// out of <c>ScriptContext.MergeDictionary</c> (Preprod trace
    /// <c>7873ad4e6c7a9db31c3b6401cd2c54fc</c>). <c>TaskCoordinator</c>'s parallel path catches
    /// that exception and returns it as a failed <see cref="Result"/> rather than letting it
    /// escape as a raw throw, so the assertion below names the fault by asserting SUCCESS with the
    /// underlying exception text surfaced as the failure message if this regresses — the custom
    /// message on <c>ShouldBeTrue</c> is what shows the exact production exception text when this
    /// test fails pre-fix.
    /// </summary>
    [Fact]
    public async Task ProcessExtensionsAsync_TwoExtensionsSameTaskSameOrder_ParallelExecutionSucceedsWithoutConflictingOutputException()
    {
        var engine = Substitute.For<ITaskExecutionEngine>();
        var errorFactory = new ExecutionErrorFactory(new ErrorNormalizer());
        StubEngineWithMarkerOutputs(engine, errorFactory);

        var extensionA = CreateExtension("extension-a", taskKey: "shared-task", order: 1, mappingMarker: "mapped-by-a");
        var extensionB = CreateExtension("extension-b", taskKey: "shared-task", order: 1, mappingMarker: "mapped-by-b");

        var componentCacheStore = CreateComponentCacheStore(extensionA, extensionB);
        var service = CreateService(componentCacheStore, CreateTaskCoordinator(engine));
        using var scriptContext = CreateScriptContext();
        var workflow = WorkflowFactory.CreateDefault();

        var result = await service.ProcessExtensionsAsync(
            null, scriptContext, workflow, ExtensionScope.Everywhere, CancellationToken.None);

        // Absence of the production throw, asserted explicitly: a failure here means the parallel
        // merge collided again, and the custom message surfaces the exact
        // "Parallel tasks produced conflicting output for key '...'" text from the underlying
        // InvalidOperationException.
        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : $"{result.Error.Code}: {result.Error.Message}");
        result.Value!["extensionA"].ToString().ShouldBe("mapped-by-a");
        result.Value!["extensionB"].ToString().ShouldBe("mapped-by-b");
    }

    /// <summary>
    /// Contract 3: <c>InstanceExtensionService.FindFailedExtensionKey</c>-equivalent
    /// behavior must name the EXTENSION that actually failed. Two extensions share a task; the
    /// first (order 1) succeeds, the second (order 2) fails. Before the fix, failure attribution
    /// checked the shared TASK's output key — which the first (successful) extension had already
    /// written — so the check found "not missing" for both entries and fell through to
    /// misattribute the failure to the first (successful) extension.
    /// </summary>
    [Fact]
    public async Task ProcessExtensionsAsync_TwoExtensionsShareTaskAndOneFails_NamesTheFailingExtension()
    {
        var engine = Substitute.For<ITaskExecutionEngine>();
        var errorFactory = new ExecutionErrorFactory(new ErrorNormalizer());
        StubEngineWithMarkerOutputs(engine, errorFactory, failMarkers: ["mapped-by-b"]);

        var extensionA = CreateExtension("extension-a", taskKey: "shared-task", order: 1, mappingMarker: "mapped-by-a");
        var extensionB = CreateExtension("extension-b", taskKey: "shared-task", order: 2, mappingMarker: "mapped-by-b");

        var componentCacheStore = CreateComponentCacheStore(extensionA, extensionB);
        var service = CreateService(componentCacheStore, CreateTaskCoordinator(engine));
        using var scriptContext = CreateScriptContext();
        var workflow = WorkflowFactory.CreateDefault();

        var result = await service.ProcessExtensionsAsync(
            null, scriptContext, workflow, ExtensionScope.Everywhere, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Target.ShouldBe("extension-b");
    }

    /// <summary>
    /// Contract 4 (regression guard): a single extension referencing a task it does not share with
    /// anyone must keep working exactly as before — the common case the fix must not disturb.
    /// </summary>
    [Fact]
    public async Task ProcessExtensionsAsync_OneExtensionOneTask_UnchangedBehavior()
    {
        var engine = Substitute.For<ITaskExecutionEngine>();
        var errorFactory = new ExecutionErrorFactory(new ErrorNormalizer());
        StubEngineWithMarkerOutputs(engine, errorFactory);

        var extensionOnly = CreateExtension("extension-only", taskKey: "solo-task", order: 1, mappingMarker: "solo-value");

        var componentCacheStore = CreateComponentCacheStore(extensionOnly);
        var service = CreateService(componentCacheStore, CreateTaskCoordinator(engine));
        using var scriptContext = CreateScriptContext();
        var workflow = WorkflowFactory.CreateDefault();

        var result = await service.ProcessExtensionsAsync(
            null, scriptContext, workflow, ExtensionScope.Everywhere, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        result.Value!.Count.ShouldBe(1);
        result.Value!["extensionOnly"].ToString().ShouldBe("solo-value");
    }

    /// <summary>
    /// Regression guard for the fix-round-1 finding: <c>WorkflowValidator</c> has no uniqueness
    /// check on a workflow's <c>Extensions</c>, so the SAME extension reference can legally be
    /// listed twice. <c>FetchExtensionsFromReferencesAsync</c> resolves references in parallel, and
    /// <c>CacheSet._inFlightResolutions</c> coalesces two concurrent identical resolutions into ONE
    /// <c>Lazy&lt;Task&lt;Result&lt;T&gt;&gt;&gt;</c> — both fetches hand back the SAME
    /// <c>Extension</c> instance, hence the SAME <c>OnExecuteTask</c> instance (no equality
    /// override), i.e. a genuine duplicate KEY. This test simulates that by handing the SAME
    /// <see cref="Extension"/> object to the resolved list twice. A <c>Dictionary.ToDictionary</c>
    /// build of the extension/task map throws <c>ArgumentException</c> on that duplicate key,
    /// which would break every read of such a workflow — a regression versus pre-fix behavior
    /// (both executions produced identical values, so the merge's equivalence check silently
    /// accepted them). The fix's last-wins loop must not throw and must still resolve correctly.
    /// </summary>
    [Fact]
    public async Task ProcessExtensionsAsync_SameExtensionReferenceListedTwice_ResolvesToSameInstanceAndSucceeds()
    {
        var engine = Substitute.For<ITaskExecutionEngine>();
        var errorFactory = new ExecutionErrorFactory(new ErrorNormalizer());
        StubEngineWithMarkerOutputs(engine, errorFactory);

        var extension = CreateExtension("extension-a", taskKey: "solo-task", order: 1, mappingMarker: "solo-value");

        // The SAME instance twice — this is what CacheSet coalescing hands back, not two extensions
        // that merely look alike.
        var componentCacheStore = CreateComponentCacheStore(extension, extension);
        var service = CreateService(componentCacheStore, CreateTaskCoordinator(engine));
        using var scriptContext = CreateScriptContext();
        var workflow = WorkflowFactory.CreateDefault();

        var result = await service.ProcessExtensionsAsync(
            null, scriptContext, workflow, ExtensionScope.Everywhere, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);
        result.Value!["extensionA"].ToString().ShouldBe("solo-value");
    }

    /// <summary>
    /// Fix-round-2 finding: the last-wins <c>responseKeyByTask</c> loop detects a duplicated
    /// extension reference "for free" (the key it is about to write is already present) — this pins
    /// that it now also logs <c>WorkflowLogs.DuplicateExtensionReference</c> (EventId 20102) naming
    /// the extension and the workflow, rather than silently tolerating the duplicate with no
    /// diagnostic at all. This is a DIFFERENT shape from two distinct extensions sharing a task
    /// (which must NOT log this warning — see the next test): here it is the SAME extension listed
    /// twice, so the task still runs once per occurrence for one output slot and can still throw the
    /// parallel-merge conflict — "give them distinct orders" is not a valid remedy for this shape.
    /// </summary>
    [Fact]
    public async Task ProcessExtensionsAsync_SameExtensionReferenceListedTwice_LogsDuplicateExtensionReferenceWarning()
    {
        var engine = Substitute.For<ITaskExecutionEngine>();
        var errorFactory = new ExecutionErrorFactory(new ErrorNormalizer());
        StubEngineWithMarkerOutputs(engine, errorFactory);

        var extension = CreateExtension("extension-a", taskKey: "solo-task", order: 1, mappingMarker: "solo-value");

        var componentCacheStore = CreateComponentCacheStore(extension, extension);
        var logger = Substitute.For<ILogger<InstanceExtensionService>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var service = CreateService(componentCacheStore, CreateTaskCoordinator(engine), logger);
        using var scriptContext = CreateScriptContext();
        var workflow = WorkflowFactory.CreateDefault(key: "duplicate-ref-flow");

        var result = await service.ProcessExtensionsAsync(
            null, scriptContext, workflow, ExtensionScope.Everywhere, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);

        var fields = LoggedFields(logger, 20102);
        fields["ExtensionKey"].ShouldBe("extension-a");
        fields["WorkflowKey"].ShouldBe("duplicate-ref-flow");
    }

    /// <summary>
    /// Regression guard: two DIFFERENT extensions legitimately sharing one task Reference (the
    /// documented, supported pattern this whole fix protects) must NOT trip the duplicate-extension
    /// warning — only the SAME extension listed twice should.
    /// </summary>
    [Fact]
    public async Task ProcessExtensionsAsync_TwoExtensionsSameTaskDifferentOrder_DoesNotLogDuplicateExtensionReferenceWarning()
    {
        var engine = Substitute.For<ITaskExecutionEngine>();
        var errorFactory = new ExecutionErrorFactory(new ErrorNormalizer());
        StubEngineWithMarkerOutputs(engine, errorFactory);

        var extensionA = CreateExtension("extension-a", taskKey: "shared-task", order: 1, mappingMarker: "mapped-by-a");
        var extensionB = CreateExtension("extension-b", taskKey: "shared-task", order: 2, mappingMarker: "mapped-by-b");

        var componentCacheStore = CreateComponentCacheStore(extensionA, extensionB);
        var logger = Substitute.For<ILogger<InstanceExtensionService>>();
        logger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var service = CreateService(componentCacheStore, CreateTaskCoordinator(engine), logger);
        using var scriptContext = CreateScriptContext();
        var workflow = WorkflowFactory.CreateDefault();

        var result = await service.ProcessExtensionsAsync(
            null, scriptContext, workflow, ExtensionScope.Everywhere, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue(result.IsSuccess ? null : result.Error.Message);

        logger.ReceivedCalls()
            .Any(call => call.GetMethodInfo().Name == nameof(ILogger.Log)
                         && call.GetArguments()[1] is EventId id
                         && id.Id == 20102)
            .ShouldBeFalse();
    }

    /// <summary>
    /// Reads the structured fields of the single logged entry carrying <paramref name="eventId"/>,
    /// through the state object every <c>LoggerMessage</c>-generated entry exposes (same approach as
    /// <c>TaskCoordinatorDuplicateTaskKeyTests.LoggedFields</c>).
    /// </summary>
    private static IReadOnlyDictionary<string, object?> LoggedFields(ILogger logger, int eventId)
    {
        var matches = logger.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(ILogger.Log)
                           && call.GetArguments()[1] is EventId id
                           && id.Id == eventId)
            .ToList();

        matches.Count.ShouldBe(1, $"expected exactly one log entry with EventId {eventId}");

        var state = (IReadOnlyList<KeyValuePair<string, object?>>)matches[0].GetArguments()[2]!;
        return state
            .Where(field => field.Key != "{OriginalFormat}")
            .ToDictionary(field => field.Key, field => field.Value);
    }

    /// <summary>
    /// Stubs <see cref="ITaskExecutionEngine.ExecuteAsync(OnExecuteTask, System.Guid?, TaskTrigger, TaskExecutionOrigin, ScriptContext, TaskEngineExecutionOptions, CancellationToken)"/>
    /// to mirror what <c>TaskExecutorBase.ExecuteAsync</c> does for an Extension-triggered task:
    /// file the response under <c>options.ResponseVariableKey ?? task.Task.Key.ToVariableName()</c>
    /// via <c>ScriptContext.SetOutputResponse</c>. The value written is the task's own
    /// <c>Mapping.Code</c> (used purely as a per-extension marker string here, standing in for
    /// "whatever that extension's own Mapping produced"), so two extensions sharing a task but
    /// authoring different Mappings are asserted to keep different outputs — deduplication would
    /// collapse them onto one value, which the tests must catch.
    /// </summary>
    private static void StubEngineWithMarkerOutputs(
        ITaskExecutionEngine engine,
        IExecutionErrorFactory errorFactory,
        HashSet<string>? failMarkers = null)
    {
        engine.ExecuteAsync(
                Arg.Any<OnExecuteTask>(),
                Arg.Any<System.Guid?>(),
                Arg.Any<TaskTrigger>(),
                Arg.Any<TaskExecutionOrigin>(),
                Arg.Any<ScriptContext>(),
                Arg.Any<TaskEngineExecutionOptions>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var task = call.Arg<OnExecuteTask>();
                var ctx = call.Arg<ScriptContext>();
                var options = call.Arg<TaskEngineExecutionOptions>();
                var marker = task.Mapping.Code;

                if (failMarkers is not null && failMarkers.Contains(marker))
                {
                    var error = errorFactory.CreateFromException(
                        new System.InvalidOperationException($"Task '{task.Task.Key}' failed for marker '{marker}'"),
                        task.Task.Key,
                        "Http",
                        0);

                    return Task.FromResult(Result<TasksExecutionResult>.Ok(
                        TasksExecutionResult.Failure(task, error)));
                }

                var key = options.ResponseVariableKey ?? task.Task.Key.ToVariableName();
                ctx.SetOutputResponse(marker, key);

                return Task.FromResult(Result<TasksExecutionResult>.Ok(
                    TasksExecutionResult.Success([TaskExecutionSummary.Success(task.Task.Key, "Http")])));
            });
    }

    /// <summary>
    /// Builds a REAL <see cref="TaskCoordinator"/> (not a substitute) over the given stub engine,
    /// so the tests exercise the actual grouping/parallel-merge/optionsRefiner-composition logic
    /// that produced the Preprod fault, not a re-implementation of it.
    /// </summary>
    private static TaskCoordinator CreateTaskCoordinator(ITaskExecutionEngine engine)
    {
        var services = new ServiceCollection().AddSingleton(engine).BuildServiceProvider();
        return new TaskCoordinator(
            engine,
            services.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<IConditionEvaluator>(),
            Substitute.For<ITimerEvaluator>(),
            new ExecutionErrorFactory(new ErrorNormalizer()),
            NullLogger<TaskCoordinator>.Instance);
    }

    private static IComponentCacheStore CreateComponentCacheStore(params Extension[] extensions)
    {
        var componentCacheStore = Substitute.For<IComponentCacheStore>();
        componentCacheStore
            .GetAllExtensionsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result<IEnumerable<Extension>>.Ok(extensions.ToList()));
        return componentCacheStore;
    }

    private static InstanceExtensionService CreateService(
        IComponentCacheStore componentCacheStore,
        ITaskCoordinatorExtended taskCoordinator,
        ILogger<InstanceExtensionService>? logger = null)
    {
        var runtimeInfoProvider = Substitute.For<IRuntimeInfoProvider>();
        runtimeInfoProvider.Domain.Returns("bank");

        return new InstanceExtensionService(
            componentCacheStore,
            taskCoordinator,
            runtimeInfoProvider,
            Substitute.For<ICurrentSchema>(),
            Substitute.For<IServiceScopeFactory>(),
            logger ?? NullLogger<InstanceExtensionService>.Instance);
    }

    private static ScriptContext CreateScriptContext() =>
        new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
            .Build();

    /// <summary>
    /// Builds an <see cref="Extension"/> via JSON deserialization — its constructor is private, so
    /// this is the only construction route available to tests (same approach as
    /// <c>ComponentCacheStoreTests.CreateMockExtension</c>). <paramref name="mappingMarker"/> is
    /// stored verbatim as the OnExecuteTask's <c>Mapping.Code</c>; it is never decoded/compiled by
    /// these tests, only read back by the stub engine as a per-extension identity marker.
    /// </summary>
    private static Extension CreateExtension(
        string extensionKey,
        string taskKey,
        int order,
        string mappingMarker,
        string taskDomain = "bank",
        string taskVersion = "1.0.0")
    {
        var json = $$"""
        {
            "type": 1,
            "scope": 3,
            "task": {
                "order": {{order}},
                "task": {
                    "key": "{{taskKey}}",
                    "domain": "{{taskDomain}}",
                    "version": "{{taskVersion}}",
                    "flow": "sys-tasks"
                },
                "mapping": {
                    "location": "inline",
                    "code": "{{mappingMarker}}"
                }
            }
        }
        """;

        var extension = JsonSerializer.Deserialize<Extension>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        extension.SetReference(new Reference(extensionKey, "bank", "sys-extensions", "1.0.0"));
        return extension;
    }
}
