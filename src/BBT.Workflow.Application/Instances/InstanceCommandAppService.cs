using System.Diagnostics;
using BBT.Aether;
using BBT.Aether.Application.Services;
using BBT.Aether.BackgroundJob;
using BBT.Aether.Guids;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.BackgroundJobs.Handlers;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Caching;
using BBT.Workflow.DefinitionContext;
using BBT.Workflow.Logging;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Execution.Transitions.Services;
using BBT.Workflow.Execution.Validation;
using BBT.Workflow.Headers;
using BBT.Workflow.Runtime;
using BBT.Workflow.Schemas;
using Dapr.Jobs.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Instances;

/// <summary>
/// Application service for workflow instance commands (start, transition).
/// Lock management is delegated to TransitionPipeline for proper lifecycle scoping.
/// </summary>
public sealed class InstanceCommandAppService(
    IServiceProvider serviceProvider,
    IRuntimeInfoProvider runtimeInfoProvider,
    IWorkflowExecutionService workflowExecutionService,
    IComponentCacheStore componentCacheStore,
    IInstanceRepository instanceRepository,
    IInstanceJobRepository instanceJobRepository,
    IBackgroundJobService backgroundJobService,
    IGuidGenerator guidGenerator,
    IHeaderService headerService,
    ITransitionDataMapper transitionDataMapper,
    ITransitionValidationService transitionValidationService,
    ISchemaMigrationOrchestrator schemaMigrationOrchestrator,
    IWorkflowContext workflowContext,
    ILogger<InstanceCommandAppService> logger)
    : ApplicationService(serviceProvider), IInstanceCommandAppService
{
    /// <inheritdoc />
    public async Task<Result<StartInstanceOutput>> StartAsync(
        StartInstanceInput input,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(input.Domain);
        // Step 1: Load workflow
        var workflowResult = await LoadWorkflowAsync(input.Domain, input.Workflow, input.Version, cancellationToken);
        if (!workflowResult.IsSuccess)
            return Result<StartInstanceOutput>.Fail(workflowResult.Error);

        var workflow = workflowResult.Value!;

        // Step 2: Migrate schema (MUST happen before any DB operations)
        var migrationSuccess = await schemaMigrationOrchestrator.MigrateSchemaWithLockAsync(
            input.Workflow, cancellationToken);
        if (!migrationSuccess)
        {
            return Result<StartInstanceOutput>.Fail(
                WorkflowErrors.SchemaMigrationLockFailed(input.Workflow));
        }

        // Step 3: Check existing instance (AFTER schema migration)
        var existingInstanceResult = await CheckExistingInstanceAsync(input, cancellationToken);
        if (existingInstanceResult.HasValue)
            return existingInstanceResult.Value;

        // Step 4-7: Continue with normal railway flow for NEW instances
        return await PrepareInstanceAsync(workflow, input, cancellationToken)
            .ThenAsync(async data =>
            {
                await ScheduleWorkflowTimeoutIfConfiguredAsync(data.Workflow, data.Instance, cancellationToken);
                return Result<(Definitions.Workflow Workflow, Instance Instance)>.Ok(data);
            })
            .ThenAsync(data => ExecuteStartTransitionAsync(data, input, cancellationToken))
            .OnSuccess(output => AddWorkflowHeader(output, input));
    }

    /// <summary>
    /// Checks if an active instance already exists before starting workflow creation.
    /// Implements idempotent behavior for active instances only (when StrictIdempotency is false).
    /// When StrictIdempotency is true (service-to-service calls), returns 409 Conflict.
    /// </summary>
    /// <returns>
    /// - null: No existing instance OR existing completed instance, continue with creation
    /// - Ok: Existing active instance found, return its current status (idempotent, only when StrictIdempotency is false)
    /// - Fail: Existing active instance found and StrictIdempotency is true (409 Conflict)
    /// </returns>
    private async Task<Result<StartInstanceOutput>?> CheckExistingInstanceAsync(
        StartInstanceInput input,
        CancellationToken cancellationToken)
    {
        var instanceId = input.Instance.Id;
        var instanceKey = input.Instance.Key;

        var existingInstance = !instanceKey.IsNullOrWhiteSpace()
            ? await instanceRepository.FindByIdentifierAsync(instanceKey, cancellationToken)
            : instanceId.HasValue
                ? await instanceRepository.FindByIdentifierAsync(instanceId.Value.ToString(), cancellationToken)
                : null;

        if (existingInstance is null)
            return null; // No existing instance, continue with creation

        if (existingInstance.IsCompleted)
            return null; // Completed instance, allow creating new one

        // Strict idempotency: Return 409 Conflict for service-to-service calls
        // This prevents false positive correlations in SubFlow/SubProcess scenarios
        if (input.StrictIdempotency)
        {
            logger.LogWarning(
                "Strict idempotency check failed: Active instance already exists with key '{InstanceKey}' or id '{InstanceId}'",
                instanceKey, existingInstance.Id);

            return Result<StartInstanceOutput>.Fail(
                WorkflowErrors.ActiveInstanceAlreadyExists(existingInstance.Id, instanceKey));
        }

        // Default idempotent behavior for client calls
        return Result<StartInstanceOutput>.Ok(new StartInstanceOutput
        {
            Id = existingInstance.Id,
            Status = existingInstance.Status
        });
    }

    /// <summary>
    /// Step 2: Loads the workflow definition from cache and sets it in WorkflowContext.
    /// Note: TransitionRunner will also set it in its isolated scope.
    /// </summary>
    private async Task<Result<Definitions.Workflow>> LoadWorkflowAsync(
        string domain,
        string workflow,
        string? version,
        CancellationToken cancellationToken)
    {
        var workflowInScope = workflowContext.Workflow;
        if (workflowInScope != null && workflowInScope.Key == workflow)
        {
            return Result<Definitions.Workflow>.Ok(workflowInScope);
        }
        var workflowResult = await componentCacheStore.GetFlowAsync(
            domain, workflow, version, cancellationToken);

        if (!workflowResult.IsSuccess)
            return Result<Definitions.Workflow>.Fail(workflowResult.Error);

        var workflowDefinition = workflowResult.Value!;

        // Set workflow in current scope's context
        workflowContext.SetWorkflow(workflowDefinition);

        return Result<Definitions.Workflow>.Ok(workflowDefinition);
    }

    /// <summary>
    /// Step 3: Prepares the instance (create, configure, persist).
    /// Railway chain: Create Instance → Validate → Map Data → Persist
    /// </summary>
    private async Task<Result<(Definitions.Workflow Workflow, Instance Instance)>> PrepareInstanceAsync(
        Definitions.Workflow workflow,
        StartInstanceInput input,
        CancellationToken cancellationToken)
    {
        await using var uow = await UnitOfWorkManager.BeginRequiresNew(cancellationToken);
        var result = await CreateAndPrepareInstanceAsync(
                workflow,
                input.Instance.Id ?? guidGenerator.Create(),
                input.Instance.Key,
                input.Instance.Tags?.ToList(),
                input.Instance.ExtraProperties,
                input.Sync,
                input.Instance.Callback,
                cancellationToken)
            .ThenAsync(instance => ValidateStartTransitionAsync(workflow, instance, input, cancellationToken))
            .ThenAsync(instance => MapInstanceDataAsync(workflow, instance, input, cancellationToken))
            .ThenAsync(instance => PersistInstanceAsync(workflow, instance, cancellationToken));

        await uow.CommitAsync(cancellationToken);
        return result;
    }

    /// <summary>
    /// Validates the start transition for the instance.
    /// Uses MatchAsync to convert non-generic Result to Result&lt;Instance&gt;.
    /// </summary>
    private Task<Result<Instance>> ValidateStartTransitionAsync(
        Definitions.Workflow workflow,
        Instance instance,
        StartInstanceInput input,
        CancellationToken cancellationToken)
    {
        return transitionValidationService.ValidateStartTransitionAsync(
                workflow,
                instance,
                workflow.StartTransition,
                input.Instance.Attributes,
                runtimeInfoProvider,
                input.Headers,
                cancellationToken)
            .MatchAsync(
                onSuccess: () => Result<Instance>.Ok(instance),
                onFailure: error =>
                {
                    logger.StartTransitionValidationFailed(instance.Id, error.Code);
                    return Result<Instance>.Fail(error);
                });
    }

    /// <summary>
    /// Maps and adds instance data if provided.
    /// </summary>
    private async Task<Result<Instance>> MapInstanceDataAsync(
        Definitions.Workflow workflow,
        Instance instance,
        StartInstanceInput input,
        CancellationToken cancellationToken)
    {
        if (input.Instance.Attributes == null)
            return Result<Instance>.Ok(instance);

        return await transitionDataMapper.MapTransitionDataAsync(
                input.Instance.Attributes,
                workflow.StartTransition,
                workflow,
                instance,
                runtimeInfoProvider,
                input.Headers,
                cancellationToken)
            .Tap(mappedData =>
            {
                if (mappedData != null)
                {
                    instance.AddData(
                        guidGenerator.Create(),
                        new JsonData(mappedData),
                        workflow.StartTransition.VersionStrategy);
                }
            })
            .MapAsync(_ => instance);
    }

    /// <summary>
    /// Persists the instance to the repository.
    /// Infrastructure errors (DB connection, etc.) propagate to middleware - not wrapped in Result.
    /// </summary>
    private async Task<Result<(Definitions.Workflow Workflow, Instance Instance)>> PersistInstanceAsync(
        Definitions.Workflow workflow,
        Instance instance,
        CancellationToken cancellationToken)
    {
        await instanceRepository.InsertAsync(instance, true, cancellationToken);
        return Result<(Definitions.Workflow, Instance)>.Ok((workflow, instance));
    }

    /// <summary>
    /// Step 4: Executes the start transition.
    /// Lock management is handled by TransitionPipeline for proper lifecycle scoping.
    /// Underlying service returns Result - unexpected exceptions propagate to middleware.
    /// </summary>
    private Task<Result<StartInstanceOutput>> ExecuteStartTransitionAsync(
        (Definitions.Workflow Workflow, Instance Instance) data,
        StartInstanceInput input,
        CancellationToken cancellationToken)
    {
        var context = input.ToExecutionContext(data.Instance.Id, data.Workflow.StartTransition.Key);

        // Execute transition - lock is managed by TransitionPipeline
        return workflowExecutionService
            .ExecuteTransitionAsync(context, cancellationToken)
            .MapAsync(transitionOutput => new StartInstanceOutput
            {
                Id = data.Instance.Id,
                Status = transitionOutput.Status
            });
    }

    /// <summary>
    /// Schedules a workflow timeout job if the workflow has a timeout configuration.
    /// </summary>
    private async Task ScheduleWorkflowTimeoutIfConfiguredAsync(
        Definitions.Workflow workflow,
        Instance instance,
        CancellationToken cancellationToken)
    {
        // Check if workflow has timeout configuration
        if (workflow.Timeout == null)
        {
            return;
        }

        try
        {
            var jobName = $"timeout-{instance.Id}";
            var activity = Activity.Current;
            var payload = new WorkflowTimeoutPayload
            {
                JobName = jobName,
                Domain = workflow.Domain,
                InstanceId = instance.Id,
                FlowName = workflow.Key,
                Version = workflow.Version,
                TraceParent = activity?.Id,
                TraceState = activity?.TraceStateString
            };

            // Parse ISO 8601 duration string to TimeSpan
            var timeoutDuration = System.Xml.XmlConvert.ToTimeSpan(workflow.Timeout.Timer.Duration);

            // Calculate timeout schedule - workflow timeout should be evaluated from creation time
            var timeoutDateTime = DateTime.UtcNow.Add(timeoutDuration);
            var schedule = DaprJobSchedule.FromDateTime(timeoutDateTime).ExpressionValue;

            var metadata = new Dictionary<string, object>
            {
                ["domain"] = workflow.Domain,
                ["flowName"] = workflow.Key,
                ["instanceId"] = instance.Id.ToString(),
                ["timeoutAt"] = timeoutDateTime.ToString("O")
            };

            // Enqueue the timeout job
            var jobId = await backgroundJobService.EnqueueAsync(
                FlowTimeoutJobHandler.HandlerName,
                jobName,
                payload,
                schedule,
                metadata,
                cancellationToken);

            // Track the job in the database
            await instanceJobRepository.InsertAsync(
                InstanceJob.Create(
                    jobId,
                    jobName,
                    jobId,
                    workflow.Domain,
                    workflow.Key,
                    instance.Id
                ),
                true,
                cancellationToken);

            logger.WorkflowTimeoutScheduled(instance.Id, workflow.Timeout.Timer.Duration, timeoutDateTime);
        }
        catch (Exception ex)
        {
            logger.WorkflowTimeoutSchedulingFailed(ex, instance.Id);
            // Don't throw - timeout scheduling failure should not prevent workflow start
        }
    }

    /// <summary>
    /// Step 5: Adds workflow header to response.
    /// </summary>
    private void AddWorkflowHeader(StartInstanceOutput output, StartInstanceInput input)
    {
        headerService.AddHeader(
            WorkflowInfo.Name,
            WorkflowInfo.Generate(runtimeInfoProvider.Domain, input.Workflow, input.Version, output.Id)
        );
    }

    /// <inheritdoc />
    public async Task<Result<TransitionOutput>> TransitionAsync(
        string instance,
        string transitionKey,
        TransitionInput input,
        CancellationToken cancellationToken = default)
    {
        // Validate domain first
        runtimeInfoProvider.Check(input.Domain);

        // Load workflow
        var workflowResult = await LoadWorkflowAsync(input.Domain, input.Workflow, input.Version, cancellationToken);
        if (!workflowResult.IsSuccess)
            return Result<TransitionOutput>.Fail(workflowResult.Error);

        return await ExecuteTransitionAsync(instance, transitionKey, input, cancellationToken)
            .OnSuccess(output => AddTransitionHeader(output, input));
    }

    /// <summary>
    /// Executes the transition and returns the output.
    /// Lock management is handled by TransitionPipeline for proper lifecycle scoping.
    /// </summary>
    private Task<Result<TransitionOutput>> ExecuteTransitionAsync(
        string instanceId,
        string transitionKey,
        TransitionInput input,
        CancellationToken cancellationToken)
    {
        var context = input.ToExecutionContext(instanceId, transitionKey);

        // Execute transition - lock is managed by TransitionPipeline
        return workflowExecutionService.ExecuteTransitionAsync(context, cancellationToken);
    }

    /// <summary>
    /// Adds workflow header to the transition response.
    /// </summary>
    private void AddTransitionHeader(TransitionOutput output, TransitionInput input)
    {
        headerService.AddHeader(
            WorkflowInfo.Name,
            WorkflowInfo.Generate(runtimeInfoProvider.Domain, input.Workflow, input.Version, output.Id)
        );
    }

    /// <summary>
    /// Creates and prepares a new instance with the provided parameters.
    /// Note: Existing instance check is done in CheckExistingInstanceAsync (idempotent behavior).
    /// </summary>
    private Task<Result<Instance>> CreateAndPrepareInstanceAsync(
        Definitions.Workflow workflow,
        Guid instanceId,
        string? instanceKey,
        List<string>? tags,
        ExtraPropertyDictionary metadata,
        bool isSync,
        string? callback,
        CancellationToken cancellationToken = default)
    {
        // Get initial state and create instance
        return Task.FromResult(workflow.GetInitialState()
            .Map(initialState =>
            {
                var instance = Instance.Create(instanceId, workflow.Key, instanceKey);
                instance.SetInfoMetadata(isSync, callback, workflow.Type.Code, metadata);
                instance.ChangeState(initialState);

                if (tags?.Any() == true)
                    instance.AddTags(tags.ToArray());

                return instance;
            }));
    }
}