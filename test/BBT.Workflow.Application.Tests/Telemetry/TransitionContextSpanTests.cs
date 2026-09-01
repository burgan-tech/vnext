using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Policies;
using BBT.Workflow.Definitions.Specifications;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Transitions.Factory;
using BBT.Workflow.Execution.Validation;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Shared;
using BBT.Workflow.Validation;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Telemetry;

/// <summary>
/// Pins the Task 8 spans: <c>Transition.LoadContext</c> wraps the full railway chain in
/// <see cref="TransitionContextFactory.CreateAsync"/>, <c>Instance.Load</c> wraps the instance
/// rehydration hop inside that same chain, and <c>Transition.Validate</c> wraps
/// <see cref="TransitionValidationService.ValidateAsync"/>. All three must be emitted on both
/// the success and the failure path (the failure path is what proves the span still closes when
/// the wrapped operation fails), and only the failure path may set an Error status.
/// </summary>
[Collection(TracingDetailLevelCollection.Name)]
public sealed class TransitionContextSpanTests : IDisposable
{
    private readonly List<ActivityListener> _listeners = new();

    public void Dispose()
    {
        foreach (var listener in _listeners)
        {
            listener.Dispose();
        }

        Activity.Current = null;
    }

    private ActivityListener CreateListener(string sourceName, List<Activity> collected)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = collected.Add
        };
        ActivitySource.AddActivityListener(listener);
        _listeners.Add(listener);
        return listener;
    }

    #region Transition.LoadContext (TransitionContextFactory.CreateAsync)

    [Fact]
    public async Task CreateAsync_EmitsLoadContextSpan_EvenOnDomainValidationFailure()
    {
        var collected = new List<Activity>();
        using var listener = CreateListener("BBT.Workflow.Pipeline", collected);

        var runtimeInfo = Substitute.For<IRuntimeInfoProvider>();
        runtimeInfo.When(r => r.Check("bad")).Throw(new InvalidOperationException("wrong domain"));

        var sut = new TransitionContextFactory(
            Substitute.For<IInstanceRepository>(),
            Substitute.For<IComponentCacheStore>(),
            runtimeInfo);

        var input = new WorkflowExecutionContext
        {
            Domain = "bad",
            InstanceId = Guid.NewGuid().ToString(),
            WorkflowKey = "wf",
            TransitionKey = "go",
            TriggerType = TriggerType.Manual,
            Mode = ExecMode.Sync
        };

        var result = await sut.CreateAsync(input, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        var span = collected.Single(a => a.DisplayName == "Transition.LoadContext");
        span.Status.ShouldBe(ActivityStatusCode.Error);
        span.StatusDescription.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task CreateAsync_EmitsLoadContextSpan_OnSuccess_WithoutErrorStatus()
    {
        var collected = new List<Activity>();
        using var listener = CreateListener("BBT.Workflow.Pipeline", collected);

        const string domain = "test-domain";
        const string workflowKey = "test-workflow";
        var workflow = CreateWorkflow(workflowKey, domain);
        var instance = Instance.Create(Guid.NewGuid(), workflowKey, workflow.Version);
        instance.ChangeState(workflow.GetState("state1").Value!);

        var input = new WorkflowExecutionContext
        {
            Domain = domain,
            InstanceId = instance.Id.ToString(),
            WorkflowKey = workflowKey,
            WorkflowVersion = workflow.Version,
            TransitionKey = "resume",
            TriggerType = TriggerType.Manual,
            Mode = ExecMode.Resume
        };

        var instanceRepository = Substitute.For<IInstanceRepository>();
        instanceRepository.GetActiveAsync(input.InstanceId, Arg.Any<CancellationToken>())
            .Returns(Result<Instance>.Ok(instance));

        var componentCacheStore = Substitute.For<IComponentCacheStore>();
        componentCacheStore.GetFlowAsync(domain, workflowKey, workflow.Version, Arg.Any<CancellationToken>())
            .Returns(Result<Definitions.Workflow>.Ok(workflow));

        var sut = new TransitionContextFactory(
            instanceRepository,
            componentCacheStore,
            Substitute.For<IRuntimeInfoProvider>());

        var result = await sut.CreateAsync(input, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        var span = collected.Single(a => a.DisplayName == "Transition.LoadContext");
        span.Status.ShouldBe(ActivityStatusCode.Unset);
    }

    #endregion

    #region Instance.Load (rehydration hop inside CreateAsync)

    [Fact]
    public async Task CreateAsync_EmitsInstanceLoadSpan_OnSuccess_WithoutErrorStatus()
    {
        var collected = new List<Activity>();
        using var listener = CreateListener("BBT.Workflow.Pipeline", collected);

        const string domain = "test-domain";
        const string workflowKey = "test-workflow";
        var workflow = CreateWorkflow(workflowKey, domain);
        var instance = Instance.Create(Guid.NewGuid(), workflowKey, workflow.Version);
        instance.ChangeState(workflow.GetState("state1").Value!);

        var input = new WorkflowExecutionContext
        {
            Domain = domain,
            InstanceId = instance.Id.ToString(),
            WorkflowKey = workflowKey,
            WorkflowVersion = workflow.Version,
            TransitionKey = "resume",
            TriggerType = TriggerType.Manual,
            Mode = ExecMode.Resume
        };

        var instanceRepository = Substitute.For<IInstanceRepository>();
        instanceRepository.GetActiveAsync(input.InstanceId, Arg.Any<CancellationToken>())
            .Returns(Result<Instance>.Ok(instance));

        var componentCacheStore = Substitute.For<IComponentCacheStore>();
        componentCacheStore.GetFlowAsync(domain, workflowKey, workflow.Version, Arg.Any<CancellationToken>())
            .Returns(Result<Definitions.Workflow>.Ok(workflow));

        var sut = new TransitionContextFactory(
            instanceRepository,
            componentCacheStore,
            Substitute.For<IRuntimeInfoProvider>());

        var result = await sut.CreateAsync(input, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        var span = collected.Single(a => a.DisplayName == "Instance.Load");
        span.Status.ShouldBe(ActivityStatusCode.Unset);
    }

    [Fact]
    public async Task CreateAsync_EmitsInstanceLoadSpan_WithErrorStatus_WhenInstanceMissing()
    {
        var collected = new List<Activity>();
        using var listener = CreateListener("BBT.Workflow.Pipeline", collected);

        const string domain = "test-domain";
        const string workflowKey = "test-workflow";
        var workflow = CreateWorkflow(workflowKey, domain);
        var instanceId = Guid.NewGuid().ToString();

        var input = new WorkflowExecutionContext
        {
            Domain = domain,
            InstanceId = instanceId,
            WorkflowKey = workflowKey,
            WorkflowVersion = workflow.Version,
            TransitionKey = "resume",
            TriggerType = TriggerType.Manual,
            Mode = ExecMode.Resume
        };

        var instanceRepository = Substitute.For<IInstanceRepository>();
        instanceRepository.GetActiveAsync(instanceId, Arg.Any<CancellationToken>())
            .Returns(Result<Instance>.Fail(WorkflowErrors.InstanceNotFound(instanceId)));

        var componentCacheStore = Substitute.For<IComponentCacheStore>();
        componentCacheStore.GetFlowAsync(domain, workflowKey, workflow.Version, Arg.Any<CancellationToken>())
            .Returns(Result<Definitions.Workflow>.Ok(workflow));

        var sut = new TransitionContextFactory(
            instanceRepository,
            componentCacheStore,
            Substitute.For<IRuntimeInfoProvider>());

        var result = await sut.CreateAsync(input, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();

        // The inner span reports the failure...
        var instanceLoadSpan = collected.Single(a => a.DisplayName == "Instance.Load");
        instanceLoadSpan.Status.ShouldBe(ActivityStatusCode.Error);

        // ...and so does the outer span wrapping the whole railway chain — same failure,
        // observed at both levels of the trace.
        var loadContextSpan = collected.Single(a => a.DisplayName == "Transition.LoadContext");
        loadContextSpan.Status.ShouldBe(ActivityStatusCode.Error);
    }

    #endregion

    #region Transition.Validate (TransitionValidationService.ValidateAsync)

    [Fact]
    public async Task ValidateAsync_EmitsValidateSpan_OnSuccess_WithoutErrorStatus()
    {
        var collected = new List<Activity>();
        using var listener = CreateListener("BBT.Workflow.Pipeline", collected);

        var sut = new TransitionValidationService(
            CreatePolicyWithNoSpecifications(),
            Substitute.For<IJsonSchemaValidator>(),
            Substitute.For<IComponentCacheStore>());

        var context = CreateValidTransitionContext();

        var result = await sut.ValidateAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        var span = collected.Single(a => a.DisplayName == "Transition.Validate");
        span.Status.ShouldBe(ActivityStatusCode.Unset);
    }

    [Fact]
    public async Task ValidateAsync_EmitsValidateSpan_WithErrorStatus_OnSchemaValidationFailure()
    {
        var collected = new List<Activity>();
        using var listener = CreateListener("BBT.Workflow.Pipeline", collected);

        var schemaRef = new Reference("test-schema", "test-domain", "sys-schemas", "1.0.0");
        var schemaDefinition = CreateSchemaDefinition("test-schema");

        var componentCacheStore = Substitute.For<IComponentCacheStore>();
        componentCacheStore
            .GetSchemaAsync(schemaRef.Domain, schemaRef.Key, schemaRef.Version, Arg.Any<CancellationToken>())
            .Returns(Result<SchemaDefinition>.Ok(schemaDefinition));

        var validationError = Error.Validation(
            code: "SCHEMA_ERROR",
            message: "Schema validation failed",
            validationErrors: new List<ValidationResult> { new("Invalid schema definition", new[] { "field1" }) });

        var schemaValidator = Substitute.For<IJsonSchemaValidator>();
        schemaValidator
            .Validate(schemaDefinition.Schema, Arg.Any<JsonElement?>(), Arg.Any<SchemaValidationOptions>())
            .Returns(Result.Fail(validationError));

        var sut = new TransitionValidationService(
            CreatePolicyWithNoSpecifications(),
            schemaValidator,
            componentCacheStore);

        var context = CreateValidTransitionContext();
        typeof(Transition).GetProperty(nameof(Transition.Schema))!.SetValue(context.Transition, schemaRef);

        var result = await sut.ValidateAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        var span = collected.Single(a => a.DisplayName == "Transition.Validate");
        span.Status.ShouldBe(ActivityStatusCode.Error);
        span.StatusDescription.ShouldBe(validationError.Message);
    }

    #endregion

    private static TransitionExecutionPolicy CreatePolicyWithNoSpecifications()
    {
        var emptySpecs = Enumerable.Empty<ITransitionSpecification>();
        var logger = Substitute.For<ILogger<CompositeTransitionSpecification>>();
        var composite = new CompositeTransitionSpecification(emptySpecs, logger);
        return new TransitionExecutionPolicy(composite);
    }

    private static TransitionExecutionContext CreateValidTransitionContext()
    {
        var instanceId = Guid.NewGuid();
        const string workflowKey = "test-workflow";
        const string domain = "test-domain";
        const string transitionKey = "test-transition";

        var workflow = CreateWorkflow(workflowKey, domain);
        var instance = Instance.Create(instanceId, workflowKey, "1.0.0", workflowKey);
        var state = workflow.GetState("state1").Value!;
        var transition = Transition.Create(
            transitionKey, null, "state1", TriggerType.Manual, VersionStrategy.IncreasePatch.Code);

        return new TransitionExecutionContext
        {
            InstanceId = instanceId,
            Domain = domain,
            WorkflowKey = workflowKey,
            TransitionKey = transitionKey,
            Trigger = TriggerType.Manual,
            Actor = ExecutionActor.User,
            CorrelationId = Guid.NewGuid().ToString("N"),
            ExecutionChainId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Workflow = workflow,
            Current = state,
            Transition = transition,
            Instance = instance,
            Data = new { test = "data" },
            TraceId = Guid.NewGuid().ToString("N"),
            SpanId = Guid.NewGuid().ToString("N")[..16],
            Headers = new Dictionary<string, string?>()
        };
    }

    private static Definitions.Workflow CreateWorkflow(string key, string domain)
    {
        const string json = """
        {
            "type": "F",
            "timeout": null,
            "labels": [],
            "functions": [],
            "features": [],
            "states": [
                {
                    "key": "state1",
                    "type": "P",
                    "transitions": []
                }
            ],
            "sharedTransitions": [],
            "extensions": [],
            "startTransition": {"key": "start", "from": null, "target": "state1", "triggerType": "Manual", "versionStrategy": "Patch", "labels": [], "onExecutionTasks": [], "view": null}
        }
        """;

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        var workflow = JsonSerializer.Deserialize<Definitions.Workflow>(json, options)!;
        workflow.SetReference(new Reference(key, domain, "sys-flows", "1.0.0"));
        return workflow;
    }

    private static SchemaDefinition CreateSchemaDefinition(string key)
    {
        const string json = """
        {
            "type": "workflow",
            "schema": {
                "type": "object",
                "properties": {
                    "field1": {"type": "string"}
                },
                "required": ["field1"]
            }
        }
        """;

        var schema = JsonSerializer.Deserialize<SchemaDefinition>(
            json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        schema.SetReference(new Reference(key, "test-domain", "sys-schemas", "1.0.0"));
        return schema;
    }
}
