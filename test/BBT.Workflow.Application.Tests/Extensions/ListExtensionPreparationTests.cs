using System;
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

/// <summary>Page-local extension preparation must reuse definitions, never execution results.</summary>
public sealed class ListExtensionPreparationTests
{
    [Fact]
    public async Task ListFactory_ReusesCoreDefinitions_ButExecutesEveryItemAndStartsFreshOnNextPage()
    {
        var extension = CreateExtension("core-info", "task", 1, "core");
        var cache = CreateComponentCacheStore(extension);
        var engine = Substitute.For<ITaskExecutionEngine>();
        var executions = new List<string>();
        ConfigureEngine(engine, executions);
        var factory = CreateService(cache, CreateTaskCoordinator(engine));
        var page = factory.CreateForList();
        var workflow = WorkflowFactory.CreateDefault();
        using var first = CreateScriptContext();
        using var second = CreateScriptContext();
        using var third = CreateScriptContext();
        var firstResult = await page.ProcessExtensionsAsync(null, first, workflow, ExtensionScope.Everywhere);
        var secondResult = await page.ProcessExtensionsAsync(null, second, workflow, ExtensionScope.Everywhere);
        firstResult.IsSuccess.ShouldBeTrue();
        secondResult.IsSuccess.ShouldBeTrue();
        firstResult.Value!["coreInfo"].ToString().ShouldBe("core:1");
        secondResult.Value!["coreInfo"].ToString().ShouldBe("core:2");
        await cache.Received(1).GetAllExtensionsAsync("bank", Arg.Any<CancellationToken>());
        var thirdResult = await factory.CreateForList().ProcessExtensionsAsync(null, third, workflow, ExtensionScope.Everywhere);
        thirdResult.Value!["coreInfo"].ToString().ShouldBe("core:3");
        await cache.Received(2).GetAllExtensionsAsync("bank", Arg.Any<CancellationToken>());
        executions.Count.ShouldBe(3);
    }

    [Fact]
    public async Task ReusedDefinitions_CoreFailureOnLaterItemStillStopsWorkflowExtensions()
    {
        var core = CreateExtension("core-info", "core-task", 1, "core");
        var workflowExtension = CreateExtension("workflow-info", "workflow-task", 1, "workflow");
        var cache = CreateComponentCacheStore(core);
        cache.GetExtensionAsync("bank", "workflow-info", "1.0.0", Arg.Any<CancellationToken>())
            .Returns(Result<Extension>.Ok(workflowExtension));
        var workflow = CreateWorkflow(workflowExtension);
        var engine = Substitute.For<ITaskExecutionEngine>();
        var executions = new List<string>();
        ConfigureEngine(engine, executions, failOnCall: 3);
        var page = CreateService(cache, CreateTaskCoordinator(engine)).CreateForList();
        using var first = CreateScriptContext();
        using var second = CreateScriptContext();
        (await page.ProcessExtensionsAsync(null, first, workflow, ExtensionScope.Everywhere)).IsSuccess.ShouldBeTrue();
        var failed = await page.ProcessExtensionsAsync(null, second, workflow, ExtensionScope.Everywhere);
        failed.IsSuccess.ShouldBeFalse();
        failed.Error.Target.ShouldBe("core-info");
        executions.ShouldBe(new[] { "core", "workflow", "core" });
        await cache.Received(1).GetAllExtensionsAsync("bank", Arg.Any<CancellationToken>());
        await cache.Received(1).GetExtensionAsync("bank", "workflow-info", "1.0.0", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WorkflowDefinitionCache_KeepsVersionsSeparate_WithoutResultReuse()
    {
        var firstDefinition = CreateExtension("details", "task", 1, "v1");
        var nextDefinition = CreateExtension("details", "task", 1, "v2");
        nextDefinition.SetReference(new Reference("details", "bank", "sys-extensions", "2.0.0"));
        var cache = CreateComponentCacheStore();
        cache.GetExtensionAsync("bank", "details", "1.0.0", Arg.Any<CancellationToken>()).Returns(Result<Extension>.Ok(firstDefinition));
        cache.GetExtensionAsync("bank", "details", "2.0.0", Arg.Any<CancellationToken>()).Returns(Result<Extension>.Ok(nextDefinition));
        var engine = Substitute.For<ITaskExecutionEngine>();
        var executions = new List<string>();
        ConfigureEngine(engine, executions);
        var page = CreateService(cache, CreateTaskCoordinator(engine)).CreateForList();
        foreach (var definition in new[] { firstDefinition, nextDefinition, firstDefinition })
        {
            using var context = CreateScriptContext();
            var result = await page.ProcessExtensionsAsync(null, context, CreateWorkflow(definition), ExtensionScope.Everywhere);
            result.IsSuccess.ShouldBeTrue();
            result.Value!["details"].ToString().ShouldStartWith(definition.Task.Mapping.Code + ":");
        }
        executions.ShouldBe(new[] { "v1", "v2", "v1" });
        await cache.Received(1).GetExtensionAsync("bank", "details", "1.0.0", Arg.Any<CancellationToken>());
        await cache.Received(1).GetExtensionAsync("bank", "details", "2.0.0", Arg.Any<CancellationToken>());
    }

    private static Definitions.Workflow CreateWorkflow(Extension extension)
    {
        var reference = new Reference(extension.Key, extension.Domain, extension.Flow, extension.Version);
        var workflow = JsonSerializer.Deserialize<Definitions.Workflow>(
            "{\"type\":\"F\",\"states\":[],\"extensions\":[" + JsonSerializer.Serialize(reference) + "]}",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        workflow.SetReference(new Reference("workflow", "bank", "sys-flows", "1.0.0"));
        return workflow;
    }

    private static void ConfigureEngine(ITaskExecutionEngine engine, List<string> executions, int? failOnCall = null)
    {
        engine.ExecuteAsync(Arg.Any<OnExecuteTask>(), Arg.Any<Guid?>(), Arg.Any<TaskTrigger>(), Arg.Any<TaskExecutionOrigin>(),
            Arg.Any<ScriptContext>(), Arg.Any<TaskEngineExecutionOptions>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var task = call.Arg<OnExecuteTask>();
                var context = call.Arg<ScriptContext>();
                var options = call.Arg<TaskEngineExecutionOptions>();
                executions.Add(task.Mapping.Code);
                if (executions.Count == failOnCall)
                    return Task.FromResult(Result<TasksExecutionResult>.Fail(Error.Failure("extension-failed", "Extension execution failed")));
                context.SetOutputResponse(task.Mapping.Code + ":" + executions.Count, options.ResponseVariableKey!);
                return Task.FromResult(Result<TasksExecutionResult>.Ok(TasksExecutionResult.Success([TaskExecutionSummary.Success(task.Task.Key, "Http")])));
            });
    }

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

        var scopedProvider = Substitute.For<IServiceProvider>();
        scopedProvider.GetService(typeof(IComponentCacheStore)).Returns(componentCacheStore);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(scopedProvider);
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(scope);
        return new InstanceExtensionService(
            componentCacheStore,
            taskCoordinator,
            runtimeInfoProvider,
            Substitute.For<ICurrentSchema>(),
            scopeFactory,
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
