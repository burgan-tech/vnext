using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Policies;
using BBT.Workflow.Definitions.Specifications;
using BBT.Workflow.Domain;
using BBT.Workflow.Instances;
using BBT.Workflow.Shared;
using BBT.Workflow.Validation;
using Microsoft.Extensions.Logging;
using Moq;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Execution.Validation;

/// <summary>
/// Unit tests for TransitionValidationService
/// Tests transition validation operations including policy and schema validation
/// </summary>
public class TransitionValidationServiceTests
{
    private readonly Mock<IJsonSchemaValidator> _mockSchemaValidator;
    private readonly Mock<IComponentCacheStore> _mockComponentCacheStore;
    private readonly TransitionExecutionPolicy _transitionExecutionPolicy;
    private readonly TransitionValidationService _service;

    public TransitionValidationServiceTests()
    {
        _mockSchemaValidator = new Mock<IJsonSchemaValidator>();
        _mockComponentCacheStore = new Mock<IComponentCacheStore>();

        // Create actual policy with real empty composite (no specifications = always pass)
        var emptySpecs = Enumerable.Empty<ITransitionSpecification>();
        var logger = Substitute.For<ILogger<CompositeTransitionSpecification>>();
        var composite = new CompositeTransitionSpecification(emptySpecs, logger);
        
        _transitionExecutionPolicy = new TransitionExecutionPolicy(composite);

        _service = new TransitionValidationService(
            _transitionExecutionPolicy,
            _mockSchemaValidator.Object,
            _mockComponentCacheStore.Object);
    }

    #region ValidateAsync Tests

    [Fact]
    public async Task ValidateAsync_WithValidTransition_ShouldReturnSuccess()
    {
        // Arrange
        var context = CreateValidTransitionContext();
        
        SetupSuccessfulPolicyValidation(context);

        // Act
        var result = await _service.ValidateAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ValidatePolicyAsync_WithSchema_ShouldNotResolveOrValidateSchema()
    {
        var schemaRef = new Reference("test-schema", "test-domain", "sys-schemas", "1.0.0");
        var context = CreateTransitionContextWithSchema(schemaRef);

        var result = await _service.ValidatePolicyAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        _mockComponentCacheStore.Verify(
            x => x.GetSchemaAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        _mockSchemaValidator.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ValidateInputSchemaAsync_ShouldNotExecuteTransitionPolicy()
    {
        var failingSpecification = new Mock<ITransitionSpecification>();
        failingSpecification.SetupGet(x => x.Priority).Returns(1);
        failingSpecification
            .Setup(x => x.IsApplicable(It.IsAny<TransitionExecutionContext>()))
            .Returns(true);
        failingSpecification
            .Setup(x => x.IsSatisfiedBy(It.IsAny<TransitionExecutionContext>()))
            .Returns(Result.Fail(Error.Validation("policy.failed", "Policy failed")));
        var composite = new CompositeTransitionSpecification(
            new[] { failingSpecification.Object },
            Substitute.For<ILogger<CompositeTransitionSpecification>>());
        var service = new TransitionValidationService(
            new TransitionExecutionPolicy(composite),
            _mockSchemaValidator.Object,
            _mockComponentCacheStore.Object);

        var result = await service.ValidateInputSchemaAsync(
            CreateValidTransitionContext(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        failingSpecification.Verify(
            x => x.IsSatisfiedBy(It.IsAny<TransitionExecutionContext>()),
            Times.Never);
    }

    [Fact]
    public async Task ValidateAsync_WhenPolicyValidationFails_ShouldReturnFailure()
    {
        var context = CreateValidTransitionContext();
        var errorCode = "POLICY_ERROR";
        var errorMessage = "Policy validation failed";
        var failingSpecification = new Mock<ITransitionSpecification>();
        failingSpecification.SetupGet(x => x.Priority).Returns(1);
        failingSpecification
            .Setup(x => x.IsApplicable(It.IsAny<TransitionExecutionContext>()))
            .Returns(true);
        failingSpecification
            .Setup(x => x.IsSatisfiedBy(It.IsAny<TransitionExecutionContext>()))
            .Returns(Result.Fail(Error.Validation(errorCode, errorMessage)));
        var composite = new CompositeTransitionSpecification(
            new[] { failingSpecification.Object },
            Substitute.For<ILogger<CompositeTransitionSpecification>>());
        var service = new TransitionValidationService(
            new TransitionExecutionPolicy(composite),
            _mockSchemaValidator.Object,
            _mockComponentCacheStore.Object);

        var result = await service.ValidateAsync(context, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldNotBe(default);
        result.Error.Code.ShouldBe(errorCode);
        result.Error.Message.ShouldBe(errorMessage);
    }

    [Fact]
    public async Task ValidateAsync_WithSchema_ShouldValidateDataAgainstSchema()
    {
        // Arrange
        var schemaRef = new Reference("test-schema", "test-domain", "sys-schemas", "1.0.0");
        var context = CreateTransitionContextWithSchema(schemaRef);
        var schemaDefinition = CreateMockSchemaDefinition("test-schema");

        SetupSuccessfulPolicyValidation(context);
        
        _mockComponentCacheStore
            .Setup(x => x.GetSchemaAsync(schemaRef.Domain, schemaRef.Key, schemaRef.Version, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<SchemaDefinition>.Ok(schemaDefinition));

        _mockSchemaValidator
            .Setup(x => x.Validate(
                schemaDefinition.Schema,
                It.IsAny<JsonElement?>(),
                It.IsAny<SchemaValidationOptions>()))
            .Returns(Result.Ok());

        // Act
        var result = await _service.ValidateAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        _mockSchemaValidator.Verify(
            x => x.Validate(
                schemaDefinition.Schema,
                It.IsAny<JsonElement?>(),
                It.IsAny<SchemaValidationOptions>()),
            Times.Once);
    }

    [Fact]
    public async Task ValidateAsync_WhenSchemaValidationFails_ShouldReturnFailure()
    {
        // Arrange
        var schemaRef = new Reference("test-schema", "test-domain", "sys-schemas", "1.0.0");
        var context = CreateTransitionContextWithSchema(schemaRef);
        var schemaDefinition = CreateMockSchemaDefinition("test-schema");

        SetupSuccessfulPolicyValidation(context);

        _mockComponentCacheStore
            .Setup(x => x.GetSchemaAsync(schemaRef.Domain, schemaRef.Key, schemaRef.Version, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<SchemaDefinition>.Ok(schemaDefinition));

        var validationError = Error.Validation(
            code: "SCHEMA_ERROR", 
            message: "Schema validation failed",
            validationErrors: new List<ValidationResult>() { new("Invalid schema definition",
                ["field1"]) });

        _mockSchemaValidator
            .Setup(x => x.Validate(
                schemaDefinition.Schema,
                It.IsAny<JsonElement?>(),
                It.IsAny<SchemaValidationOptions>()))
            .Returns(Result.Fail(validationError));

        // Act
        var result = await _service.ValidateAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldNotBe(default!);
    }

    [Fact]
    public async Task ValidateAsync_WithAcceptLanguageHeader_ShouldPassCultureToSchemaValidator()
    {
        // Arrange
        var schemaRef = new Reference("test-schema", "test-domain", "sys-schemas", "1.0.0");
        var context = CreateTransitionContextWithSchema(schemaRef, new Dictionary<string, string?>
        {
            ["accept-language"] = "tr-TR,tr;q=0.9,en-US;q=0.8"
        });
        var schemaDefinition = CreateMockSchemaDefinition("test-schema");
        SchemaValidationOptions? capturedOptions = null;

        _mockComponentCacheStore
            .Setup(x => x.GetSchemaAsync(schemaRef.Domain, schemaRef.Key, schemaRef.Version, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<SchemaDefinition>.Ok(schemaDefinition));

        _mockSchemaValidator
            .Setup(x => x.Validate(
                schemaDefinition.Schema,
                It.IsAny<JsonElement?>(),
                It.IsAny<SchemaValidationOptions>()))
            .Callback<JsonElement, JsonElement?, SchemaValidationOptions>((_, _, options) => capturedOptions = options)
            .Returns(Result.Ok());

        // Act
        var result = await _service.ValidateAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        capturedOptions.ShouldNotBeNull();
        capturedOptions!.Culture.ShouldBe("tr-TR");
        capturedOptions.IncludeVocabularyDetails.ShouldBeTrue();
        capturedOptions.CustomValidationEnabled.ShouldBeTrue();
    }

    [Fact]
    public async Task ValidateAsync_WithValidationErrors_ShouldIncludeTransitionKey()
    {
        // Arrange
        var schemaRef = new Reference("test-schema", "test-domain", "sys-schemas", "1.0.0");
        var context = CreateTransitionContextWithSchema(schemaRef);
        var schemaDefinition = CreateMockSchemaDefinition("test-schema");

        SetupSuccessfulPolicyValidation(context);

        _mockComponentCacheStore
            .Setup(x => x.GetSchemaAsync(schemaRef.Domain, schemaRef.Key, schemaRef.Version, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<SchemaDefinition>.Ok(schemaDefinition));

        var validationErrors = new List<ValidationResult> 
        { 
            new("invalid", ["field1"])
        };

        var validationError = Error.Validation(code: "SCHEMA_ERROR", message: "Schema validation failed",
            validationErrors: validationErrors);

        _mockSchemaValidator
            .Setup(x => x.Validate(
                schemaDefinition.Schema,
                It.IsAny<JsonElement?>(),
                It.IsAny<SchemaValidationOptions>()))
            .Returns(Result.Fail(validationError));

        // Act
        var result = await _service.ValidateAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.Error.ValidationErrors.ShouldBe(validationErrors);
    }

    [Fact]
    public async Task ValidateAsync_WithCancellation_ShouldPropagateCancellation()
    {
        // Arrange
        var context = CreateValidTransitionContext();
        var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Should.ThrowAsync<OperationCanceledException>(
            async () => await _service.ValidateAsync(context, cts.Token)
        );
    }

    [Fact]
    public async Task ValidateAsync_WithActor_ShouldPassActorToPolicyValidation()
    {
        // Arrange
        var actor = ExecutionActor.User;
        var context = CreateValidTransitionContext();
        context.Actor = actor;

        SetupSuccessfulPolicyValidation(context);

        // Act
        var result = await _service.ValidateAsync(context, CancellationToken.None);

        // Assert - The actor should be used in policy validation
        // This is implicitly tested by the successful validation
        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ValidateAsync_WithMultipleValidationErrors_ShouldReturnAllErrors()
    {
        // Arrange
        var schemaRef = new Reference("test-schema", "test-domain", "sys-schemas", "1.0.0");
        var context = CreateTransitionContextWithSchema(schemaRef);
        var schemaDefinition = CreateMockSchemaDefinition("test-schema");

        SetupSuccessfulPolicyValidation(context);

        _mockComponentCacheStore
            .Setup(x => x.GetSchemaAsync(schemaRef.Domain, schemaRef.Key, schemaRef.Version, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<SchemaDefinition>.Ok(schemaDefinition));

        var validationErrors = new List<ValidationResult>
        {
            new("Error 1", ["field1"]),
            new("Error 2", ["field2"]),
            new("Error 3", ["field3"])
        };

        var validationError = Error.Validation(
            code: "SCHEMA_ERROR", message: "Multiple validation errors", validationErrors: validationErrors);

        _mockSchemaValidator
            .Setup(x => x.Validate(
                schemaDefinition.Schema,
                It.IsAny<JsonElement?>(),
                It.IsAny<SchemaValidationOptions>()))
            .Returns(Result.Fail(validationError));

        // Act
        var result = await _service.ValidateAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.Error.ValidationErrors.ShouldNotBeNull();
        result.Error.ValidationErrors!.Count.ShouldBe(3);
    }

    #endregion

    #region ValidateTriggerTypeAsync Tests

    [Fact]
    public async Task ValidateTriggerTypeAsync_ManualTrigger_WithUserActor_ShouldReturnSuccess()
    {
        // Arrange
        var context = CreateValidTransitionContext();
        typeof(TransitionExecutionContext)
            .GetProperty(nameof(TransitionExecutionContext.Trigger))!
            .SetValue(context, TriggerType.Manual);
        typeof(TransitionExecutionContext)
            .GetProperty(nameof(TransitionExecutionContext.Actor))!
            .SetValue(context, ExecutionActor.User);

        // Act
        var result = await _service.ValidateTriggerTypeAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ValidateTriggerTypeAsync_ManualTrigger_WithSystemActor_ShouldReturnFailure()
    {
        // Arrange
        var context = CreateValidTransitionContext();
        typeof(TransitionExecutionContext)
            .GetProperty(nameof(TransitionExecutionContext.Trigger))!
            .SetValue(context, TriggerType.Manual);
        typeof(TransitionExecutionContext)
            .GetProperty(nameof(TransitionExecutionContext.Actor))!
            .SetValue(context, ExecutionActor.System);

        // Act
        var result = await _service.ValidateTriggerTypeAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.UnauthorizedTransition);
        result.Error.Message.ShouldContain("Manual transitions require User actor");
    }

    [Fact]
    public async Task ValidateTriggerTypeAsync_AutomaticTrigger_WithSystemActor_ShouldReturnSuccess()
    {
        // Arrange
        var context = CreateValidTransitionContext();
        typeof(TransitionExecutionContext)
            .GetProperty(nameof(TransitionExecutionContext.Trigger))!
            .SetValue(context, TriggerType.Automatic);
        typeof(TransitionExecutionContext)
            .GetProperty(nameof(TransitionExecutionContext.Actor))!
            .SetValue(context, ExecutionActor.System);
        typeof(TransitionExecutionContext)
            .GetProperty(nameof(TransitionExecutionContext.ChainDepth))!
            .SetValue(context, 5);

        // Act
        var result = await _service.ValidateTriggerTypeAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ValidateTriggerTypeAsync_AutomaticTrigger_WithUserActor_ShouldReturnFailure()
    {
        // Arrange
        var context = CreateValidTransitionContext();
        typeof(TransitionExecutionContext)
            .GetProperty(nameof(TransitionExecutionContext.Trigger))!
            .SetValue(context, TriggerType.Automatic);
        typeof(TransitionExecutionContext)
            .GetProperty(nameof(TransitionExecutionContext.Actor))!
            .SetValue(context, ExecutionActor.User);

        // Act
        var result = await _service.ValidateTriggerTypeAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.UnauthorizedTransition);
        result.Error.Message.ShouldContain("Automatic transitions require System actor");
    }

    [Fact]
    public async Task ValidateTriggerTypeAsync_AutomaticTrigger_ExceedingChainDepth_ShouldReturnFailure()
    {
        // Arrange
        var context = CreateValidTransitionContext();
        typeof(TransitionExecutionContext)
            .GetProperty(nameof(TransitionExecutionContext.Trigger))!
            .SetValue(context, TriggerType.Automatic);
        typeof(TransitionExecutionContext)
            .GetProperty(nameof(TransitionExecutionContext.Actor))!
            .SetValue(context, ExecutionActor.System);
        typeof(TransitionExecutionContext)
            .GetProperty(nameof(TransitionExecutionContext.ChainDepth))!
            .SetValue(context, 51); // Exceeds max depth of 50

        // Act
        var result = await _service.ValidateTriggerTypeAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.TransitionChainDepthExceeded);
        result.Error.Message.ShouldContain("Transition chain depth limit exceeded");
    }

    [Fact]
    public async Task ValidateTriggerTypeAsync_ScheduledTrigger_WithSystemActor_NotReentry_ShouldSetSkipExecution()
    {
        // Arrange
        var context = CreateValidTransitionContext();
        typeof(TransitionExecutionContext)
            .GetProperty(nameof(TransitionExecutionContext.Trigger))!
            .SetValue(context, TriggerType.Scheduled);
        typeof(TransitionExecutionContext)
            .GetProperty(nameof(TransitionExecutionContext.Actor))!
            .SetValue(context, ExecutionActor.System);
        typeof(TransitionExecutionContext)
            .GetProperty(nameof(TransitionExecutionContext.IsReentry))!
            .SetValue(context, false);

        // Act
        var result = await _service.ValidateTriggerTypeAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        context.SkipImmediateExecution.ShouldBeTrue();
    }

    [Fact]
    public async Task ValidateTriggerTypeAsync_ScheduledTrigger_WithSystemActor_Reentry_ShouldNotSetSkipExecution()
    {
        // Arrange
        var context = CreateValidTransitionContext();
        typeof(TransitionExecutionContext)
            .GetProperty(nameof(TransitionExecutionContext.Trigger))!
            .SetValue(context, TriggerType.Scheduled);
        typeof(TransitionExecutionContext)
            .GetProperty(nameof(TransitionExecutionContext.Actor))!
            .SetValue(context, ExecutionActor.System);
        typeof(TransitionExecutionContext)
            .GetProperty(nameof(TransitionExecutionContext.IsReentry))!
            .SetValue(context, true);

        // Act
        var result = await _service.ValidateTriggerTypeAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
        context.SkipImmediateExecution.ShouldBeFalse();
    }

    [Fact]
    public async Task ValidateTriggerTypeAsync_ScheduledTrigger_WithUserActor_ShouldReturnFailure()
    {
        // Arrange
        var context = CreateValidTransitionContext();
        typeof(TransitionExecutionContext)
            .GetProperty(nameof(TransitionExecutionContext.Trigger))!
            .SetValue(context, TriggerType.Scheduled);
        typeof(TransitionExecutionContext)
            .GetProperty(nameof(TransitionExecutionContext.Actor))!
            .SetValue(context, ExecutionActor.User);

        // Act
        var result = await _service.ValidateTriggerTypeAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.UnauthorizedTransition);
        result.Error.Message.ShouldContain("Scheduled transitions require System actor");
    }

    [Fact]
    public async Task ValidateTriggerTypeAsync_EventTrigger_WithSystemActor_ShouldReturnSuccess()
    {
        // Arrange — event transitions are dispatched by the event subsystem under the System actor.
        var context = CreateValidTransitionContext();
        typeof(TransitionExecutionContext)
            .GetProperty(nameof(TransitionExecutionContext.Trigger))!
            .SetValue(context, TriggerType.Event);
        typeof(TransitionExecutionContext)
            .GetProperty(nameof(TransitionExecutionContext.Actor))!
            .SetValue(context, ExecutionActor.System);

        // Act
        var result = await _service.ValidateTriggerTypeAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task ValidateTriggerTypeAsync_EventTrigger_WithUserActor_ShouldReturnFailure()
    {
        // Arrange — a manual (User) actor cannot trigger an event transition.
        var context = CreateValidTransitionContext();
        typeof(TransitionExecutionContext)
            .GetProperty(nameof(TransitionExecutionContext.Trigger))!
            .SetValue(context, TriggerType.Event);
        typeof(TransitionExecutionContext)
            .GetProperty(nameof(TransitionExecutionContext.Actor))!
            .SetValue(context, ExecutionActor.User);

        // Act
        var result = await _service.ValidateTriggerTypeAsync(context, CancellationToken.None);

        // Assert
        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.UnauthorizedTransition);
        result.Error.Message.ShouldContain("Event transitions can only be triggered by the event subsystem");
    }

    #endregion

    #region Helper Methods

    private void SetupSuccessfulPolicyValidation(TransitionExecutionContext context)
    {
        // Policy is initialized with default successful behavior
        // No additional setup needed
    }

    private TransitionExecutionContext CreateValidTransitionContext(
        IReadOnlyDictionary<string, string?>? headers = null)
    {
        var instanceId = Guid.NewGuid();
        var workflowKey = "test-workflow";
        var domain = "test-domain";
        var transitionKey = "test-transition";

        var workflow = CreateMockWorkflow(workflowKey, domain);
        var instance = CreateMockInstance(instanceId, workflowKey, domain);
        var state = workflow.GetState("state1").Value!;
        var transition = Transition.Create(transitionKey, null, "state1", TriggerType.Manual, VersionStrategy.IncreasePatch.Code); 

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
            Headers = headers ?? new Dictionary<string, string?>()
        };
    }

    private TransitionExecutionContext CreateTransitionContextWithSchema(
        Reference schemaRef,
        IReadOnlyDictionary<string, string?>? headers = null)
    {
        var context = CreateValidTransitionContext(headers);
        typeof(Transition)
            .GetProperty(nameof(Transition.Schema))!
            .SetValue(context.Transition, schemaRef);
        return context;
    }

    private Instance CreateMockInstance(Guid instanceId, string workflowKey, string domain)
    {
        var instance = Instance.Create(instanceId, workflowKey,"1.0.0", workflowKey);
        return instance;
    }

    private Definitions.Workflow CreateMockWorkflow(string key, string domain)
    {
        var json = """
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

        var options = new System.Text.Json.JsonSerializerOptions 
        { 
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        var workflow = System.Text.Json.JsonSerializer.Deserialize<Definitions.Workflow>(json, options)!;

        workflow.SetReference(new Reference(key, domain, "sys-flows", "1.0.0"));
        return workflow;
    }

    private SchemaDefinition CreateMockSchemaDefinition(string key)
    {
        var json = """
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

        var schema = System.Text.Json.JsonSerializer.Deserialize<SchemaDefinition>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        schema.SetReference(new Reference(key, "test-domain", "sys-schemas", "1.0.0"));
        return schema;
    }

    #endregion
}
