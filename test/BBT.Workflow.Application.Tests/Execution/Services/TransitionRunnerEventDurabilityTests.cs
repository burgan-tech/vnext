using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Events;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Aether.Users;
using BBT.Workflow.Caching;
using BBT.Workflow.DefinitionContext;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Instances;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;
using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.Application.Tests.Execution.Services;

public sealed class TransitionRunnerEventDurabilityTests
{
    [Fact]
    public async Task RunAsync_WhenEventStagingFails_ThrowsAndDoesNotCommit()
    {
        var eventBus = Substitute.For<IDistributedEventBus>();
        eventBus.PublishAsync(
                Arg.Any<IDistributedEvent>(),
                Arg.Any<EventMetadata>(),
                Arg.Any<string?>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("outbox staging failed")));

        var (runner, uow, _) = CreateRunner(eventBus);

        var exception = await Should.ThrowAsync<InvalidOperationException>(() => runner.RunAsync(CreateContext()));

        exception.Message.ShouldBe("outbox staging failed");
        await uow.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_WhenEventStagingSucceeds_PublishesBeforeCommit()
    {
        var calls = new List<string>();
        var eventBus = Substitute.For<IDistributedEventBus>();
        eventBus.PublishAsync(
                Arg.Any<IDistributedEvent>(),
                Arg.Any<EventMetadata>(),
                Arg.Any<string?>(),
                Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls.Add("publish");
                return Task.CompletedTask;
            });

        var (runner, _, configureCommit) = CreateRunner(eventBus);
        configureCommit(() => calls.Add("commit"));

        var result = await runner.RunAsync(CreateContext());

        result.IsSuccess.ShouldBeTrue();
        calls.ShouldBe(["publish", "commit"]);
    }

    private static (
        TransitionRunner Runner,
        IUnitOfWork Uow,
        Action<Action> ConfigureCommit) CreateRunner(IDistributedEventBus eventBus)
    {
        var uow = Substitute.For<IUnitOfWork>();
        var commitAction = () => { };
        uow.CommitAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            commitAction();
            return Task.CompletedTask;
        });

        var uowManager = Substitute.For<IUnitOfWorkManager>();
        uowManager.Begin(Arg.Any<UnitOfWorkOptions>()).Returns(uow);

        var core = Substitute.For<IWorkflowExecutionCore>();
        var envelope = new DomainEventEnvelope(
            new TestEvent(),
            new EventMetadata(typeof(TestEvent), "test.event", 1, "pubsub", "topic", "source"));
        var coreOutput = new TransitionCoreOutput(
            new TransitionOutput { Id = Guid.NewGuid(), Status = InstanceStatus.Active },
            [envelope],
            ContinuationSet.Empty,
            new TransitionExecutionContext());
        core.ExecuteTransitionCoreAsync(
                Arg.Any<WorkflowExecutionContext>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<TransitionCoreOutput>.Ok(coreOutput));

        var currentSchema = Substitute.For<ICurrentSchema>();
        currentSchema.Change(Arg.Any<string>()).Returns(Substitute.For<IDisposable>());

        var cacheStore = Substitute.For<IComponentCacheStore>();
        cacheStore.GetFlowAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowDefinition>.Ok(CreateWorkflow()));

        var services = new ServiceCollection();
        services.AddSingleton(uowManager);
        services.AddSingleton(core);
        services.AddSingleton(eventBus);
        services.AddSingleton(currentSchema);
        services.AddSingleton(cacheStore);
        services.AddSingleton(Substitute.For<IWorkflowContext>());
        services.AddSingleton(Substitute.For<ICurrentUser>());
        services.AddScoped<ITransitionCommitLeaseManager, TransitionCommitLeaseManager>();

        var provider = services.BuildServiceProvider();
        var runner = new TransitionRunner(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<ILogger<TransitionRunner>>());

        return (runner, uow, action => commitAction = action);
    }

    private static WorkflowExecutionContext CreateContext() => new()
    {
        Domain = "test-domain",
        WorkflowKey = "test-flow",
        WorkflowVersion = "1.0.0",
        InstanceId = Guid.NewGuid().ToString(),
        TransitionKey = "start"
    };

    private static WorkflowDefinition CreateWorkflow()
    {
        const string json = """
            {
              "type": "F",
              "labels": [],
              "functions": [],
              "features": [],
              "states": [
                { "key": "state1", "type": "P", "transitions": [] }
              ],
              "sharedTransitions": [],
              "extensions": [],
              "startTransition": {
                "key": "start",
                "target": "state1",
                "triggerType": "Manual",
                "versionStrategy": "Patch",
                "labels": [],
                "onExecutionTasks": []
              }
            }
            """;

        return JsonSerializer.Deserialize<WorkflowDefinition>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        })!;
    }

    private sealed class TestEvent : IDistributedEvent;
}
