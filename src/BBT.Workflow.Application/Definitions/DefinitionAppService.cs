using System.Text.Json;
using BBT.Aether.Application.Services;
using BBT.Aether.Events;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions.CastHandlers;
using BBT.Workflow.Definitions.Events;
using BBT.Workflow.Definitions.Validators;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Monitoring;
using BBT.Workflow.Runtime;
using BBT.Workflow.Schemas;
using Dapr.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nito.Disposables;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Administrative service implementation for workflow management operations.
/// </summary>
public sealed class DefinitionAppService(
    ICurrentSchema currentSchema,
    IRuntimeInfoProvider runtimeInfoProvider,
    IInstanceRepository instanceRepository,
    ComponentValidatorProcessor componentValidatorProcessor,
    WorkflowCastProcessor castProcessor,
    IWorkflowMetrics workflowMetrics,
    IRuntimeCacheInitializer runtimeCacheInitializer,
    DaprClient daprClient,
    IConfiguration configuration,
    IServiceScopeFactory  scopeFactory,
    IServiceProvider serviceProvider)
    : ApplicationService(serviceProvider), IDefinitionAppService
{
    /// <inheritdoc />
    public async Task<Result> PublishAsync(PublishInput input, CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(input.Domain);
        using (currentSchema.Use(input.Flow))
        {
            // Only migrate schema for sys-flows component type
            if (input.Flow == RuntimeSysSchemaInfo.Flows)
            {
                var migrationResult = await MigrateSchemaAsync(input.Key, cancellationToken);
                if (!migrationResult.IsSuccess)
                    return migrationResult;
            }
            
            var instance = await instanceRepository.FindByIdentifierAsync(input.Key, cancellationToken)
                           ?? Instance.Create(GuidGenerator.Create(), input.Flow, input.FlowVersion, input.Key);

            instance.AddTags(input.Tags.ToArray());

            return instance.IsTransient
                ? await SaveNewInstanceAsync(instance, input, cancellationToken)
                : await HandleExistingInstanceAsync(instance, input, cancellationToken);
        }
    }

    private async Task<Result> MigrateSchemaAsync(string flow, CancellationToken cancellationToken =  default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var schema = scope.ServiceProvider.GetRequiredService<ICurrentSchema>();
        using (schema.Use(flow))
        {
            var orchestrator = scope.ServiceProvider.GetRequiredService<ISchemaMigrationOrchestrator>();
            try
            {
                await orchestrator.MigrateSchemaWithLockAsync(flow, cancellationToken);
                return Result.Ok();
            }
            catch (Exception ex)
            {
                Logger.LogError(
                    ex,
                    "Migration failed for schema {Schema}. Continuing with remaining schemas",
                    flow);
                return Result.Fail(Error.Failure(WorkflowErrorCodes.MigrationFailed, "Migration failed", ex.Message));
            }
        }
    }

    /// <summary>
    /// Validates a component using the appropriate validator for the component type.
    /// </summary>
    private Result ValidateComponent(PublishInput input)
    {
        var validationResult = componentValidatorProcessor.Validate(input.Flow, input.Attributes);

        if (!validationResult.IsValid)
        {
            // Record validation failure metrics
            foreach (var error in validationResult.ValidationErrors)
            {
                var memberName = error.MemberNames.FirstOrDefault() ?? "unknown";
                workflowMetrics.RecordValidationFailure(input.Flow, "AdminAppService", memberName);
            }

            return Result.Fail(
                Error.Validation(
                    WorkflowErrorCodes.InvalidWorkflow,
                    $"Component validation failed for type '{input.Flow}'",
                    validationResult.ValidationErrors));
        }

        return Result.Ok();
    }

    /// <summary>
    /// Saves a new component instance to the repository.
    /// </summary>
    private async Task<Result> SaveNewInstanceAsync(
        Instance instance,
        PublishInput input,
        CancellationToken cancellationToken)
    {
        var validationResult = ValidateComponent(input);
        if (!validationResult.IsSuccess)
            return validationResult;

        instance.AddDataWithVersion(
            GuidGenerator.Create(),
            new JsonData(input.Attributes),
            input.Version
        );

        await instanceRepository.InsertAsync(instance, true, cancellationToken);

        // Cast to cache via CastProcessor
        await castProcessor.ProcessAsync(
            input.Flow,
            new Reference(input.Key, input.Domain, input.Flow, input.Version),
            input.Attributes,
            cancellationToken
        );

        if (input.Data?.Any() == true)
        {
            var seedDataResult = await HandleAdditionalDataVersionsAsync(input, cancellationToken);
            if (!seedDataResult.IsSuccess)
                return seedDataResult;
        }

        return Result.Ok();
    }

    /// <summary>
    /// Handles updating an existing component instance.
    /// </summary>
    private async Task<Result> HandleExistingInstanceAsync(
        Instance instance,
        PublishInput input,
        CancellationToken cancellationToken)
    {
        var existingInstanceData = instance.FindData(input.Version);
        if (existingInstanceData != null)
        {
            if (input.Data?.Any() == true)
            {
                Logger.LogWarning(
                    "Instance {InstanceKey} already has data version {Version}",
                    instance.Key, input.Version);
                var seedDataResult = await HandleAdditionalDataVersionsAsync(input, cancellationToken);
                if (!seedDataResult.IsSuccess)
                    return seedDataResult;
            }

            return Result.Fail(WorkflowErrors.WorkflowVersionConflict());
        }

        var validationResult = ValidateComponent(input);
        if (!validationResult.IsSuccess)
            return validationResult;

        instance.AddDataWithVersion(
            GuidGenerator.Create(),
            new JsonData(input.Attributes),
            input.Version,
            false
        );

        await instanceRepository.UpdateAsync(instance, true, cancellationToken);

        // Cast to cache via CastProcessor
        await castProcessor.ProcessAsync(
            input.Flow,
            new Reference(input.Key, input.Domain, input.Flow, input.Version),
            input.Attributes,
            cancellationToken
        );

        if (input.Data?.Any() == true)
        {
            var additionalDataResult = await HandleAdditionalDataVersionsAsync(input, cancellationToken);
            if (!additionalDataResult.IsSuccess)
                return additionalDataResult;
        }

        return Result.Ok();
    }

    /// <summary>
    /// Handles processing additional data versions from the publish input.
    /// </summary>
    /// <returns>A result containing any validation errors from seed data processing.</returns>
    private async Task<Result> HandleAdditionalDataVersionsAsync(
        PublishInput input,
        CancellationToken cancellationToken)
    {
        using (currentSchema.Use(input.Key))
        {
            await using var scopeProvider = ServiceProvider.CreateAsyncScope();
            var instanceRepo = scopeProvider.ServiceProvider.GetRequiredService<IInstanceRepository>();

            foreach (var dataItem in input.Data!)
            {
                var result = await ProcessDataItemAsync(instanceRepo, input, dataItem, cancellationToken);
                if (!result.IsSuccess)
                {
                    return result;
                }
            }

            scopeProvider.ToAsyncDisposable();

            return Result.Ok();
        }
    }

    /// <summary>
    /// Processes a single data item for additional data versions.
    /// </summary>
    private async Task<Result> ProcessDataItemAsync(
        IInstanceRepository instanceRepo,
        PublishInput input,
        PublishDataInput dataItem,
        CancellationToken cancellationToken)
    {
        var instance = await instanceRepo.FindByIdentifierAsync(dataItem.Key, cancellationToken)
                       ?? Instance.Create(GuidGenerator.Create(), input.Key, input.FlowVersion, dataItem.Key);

        if (instance.FindData(dataItem.Version) != null)
        {
            Logger.LogWarning(
                "Instance {InstanceKey} already has data version {Version}",
                dataItem.Key, dataItem.Version);
            return Result.Ok();
        }

        // Validate seed data using the same validator as the parent component type
        var validationResult = ValidateSeedData(input.Key, dataItem);
        if (!validationResult.IsSuccess)
            return validationResult;

        instance.AddTags(dataItem.Tags.ToArray());
        instance.AddDataWithVersion(
            GuidGenerator.Create(),
            new JsonData(dataItem.Attributes),
            dataItem.Version
        );

        if (instance.IsTransient)
        {
            await instanceRepo.InsertAsync(instance, true, cancellationToken);
        }
        else
        {
            await instanceRepo.UpdateAsync(instance, true, cancellationToken);
        }

        await castProcessor.ProcessAsync(
            input.Key,
            new Reference(dataItem.Key, input.Domain, input.Key, dataItem.Version),
            dataItem.Attributes,
            cancellationToken
        );

        return Result.Ok();
    }

    /// <summary>
    /// Validates seed data using the appropriate validator for the component type.
    /// Uses TryValidate to gracefully handle component types without dedicated validators.
    /// </summary>
    /// <param name="componentType">The component type (e.g., sys-flows, sys-tasks).</param>
    /// <param name="dataItem">The seed data item to validate.</param>
    /// <returns>A result indicating validation success or failure.</returns>
    private Result ValidateSeedData(string componentType, PublishDataInput dataItem)
    {
        if (!componentValidatorProcessor.TryValidate(componentType, dataItem.Attributes, out var validationResult))
        {
            // No validator found for this component type, skip validation
            return Result.Ok();
        }

        if (!validationResult.IsValid)
        {
            foreach (var error in validationResult.ValidationErrors)
            {
                var memberName = error.MemberNames.FirstOrDefault() ?? "unknown";
                workflowMetrics.RecordValidationFailure(componentType, "AdminAppService.SeedData", memberName);
            }

            return Result.Fail(
                Error.Validation(
                    WorkflowErrorCodes.InvalidWorkflow,
                    $"Seed data validation failed for key '{dataItem.Key}' in component type '{componentType}'",
                    validationResult.ValidationErrors));
        }

        return Result.Ok();
    }

    /// <inheritdoc />
    public async Task<Result> InvalidateCacheAsync(
        InvalidateCacheInput input,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(input.Domain);

        using (currentSchema.Use(input.Flow))
        {
            var instance = await instanceRepository.FindByIdentifierAsync(input.Key, cancellationToken);
            if (instance == null)
            {
                return Result.Fail(WorkflowErrors.InstanceNotFound(input.Key));
            }

            var instanceData = instance.FindData(input.Version);
            if (instanceData == null)
            {
                return Result.Fail(WorkflowErrors.InstanceDataNotFound(input.Key, input.Version));
            }

            if (instanceData.Data.JsonElement.ValueKind != JsonValueKind.Null)
            {
                await castProcessor.ProcessAsync(
                    input.Flow,
                    new Reference(input.Key, input.Domain, input.Flow, input.Version),
                    instanceData.Data.JsonElement,
                    cancellationToken
                );
            }

            return Result.Ok();
        }
    }

    /// <inheritdoc />
    public async Task<Result> ReInitializeAsync(CancellationToken cancellationToken = default)
    {
        // First, update both in-memory and distributed cache on this initiating pod
        await runtimeCacheInitializer.InitializeWithDistributedCacheAsync(cancellationToken);
        
        // Then, publish broadcast event to all pods using Dapr client directly
        var cacheInvalidationEvent = new DefinitionCacheInvalidationEvent
        {
            Domain = runtimeInfoProvider.Domain,
            RequestedBy = "System",
            RequestedAt = DateTime.UtcNow,
            Environment = configuration["ASPNETCORE_ENVIRONMENT"]!
        };
        
        await daprClient.PublishEventAsync(
            pubsubName: configuration["DAPR_PUBSUB_BROADCAST_STORE_NAME"]!,
            topicName: DefinitionCacheInvalidationEvent.TopicName,
            data: cacheInvalidationEvent,
            cancellationToken: cancellationToken);
        
        return Result.Ok();
    }
}
