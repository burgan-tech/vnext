using System.Diagnostics;
using System.Text.Json;
using BBT.Aether;
using BBT.Workflow.Authorization;
using BBT.Workflow.RepresentationEtag;
using BBT.Aether.Application.Services;
using BBT.Aether.BackgroundJob;
using BBT.Aether.Guids;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.BackgroundJobs.Handlers;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Caching;
using BBT.Workflow.DefinitionContext;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Logging;
using BBT.Workflow.Execution.Transitions.Services;
using BBT.Workflow.Execution.Validation;
using BBT.Workflow.Extentions;
using BBT.Workflow.Headers;
using BBT.Workflow.Runtime;
using BBT.Workflow.Definitions.Timer;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Evaluation;
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
    ITransitionContextFactory transitionContextFactory,
    IWorkflowContext workflowContext,
    IRepresentationEtagService representationEtagService,
    ISchemaFieldFilterService schemaFieldFilterService,
    IInstanceExtensionService instanceExtensionService,
    IScriptContextFactory scriptContextFactory,
    ITimerEvaluator timerEvaluator,
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

        // Step 2: Check existing instance
        var existingInstanceResult = await CheckExistingInstanceAsync(input, cancellationToken);
        if (existingInstanceResult.HasValue)
        {
            if (input.Sync && existingInstanceResult.Value.IsSuccess)
                return await EnrichSyncOutputAsync(existingInstanceResult.Value.Value!, existingInstanceResult.Value.Value!.Id, workflow, input.Extensions, cancellationToken);
            return existingInstanceResult.Value;
        }

        // Step 3-6: Continue with normal railway flow for NEW instances.
        // Order matters: PrepareInstanceAsync runs schema validation before persisting the instance,
        // ExecuteStartTransitionAsync dispatches the (sync or async) execution, and only after a
        // successful dispatch do we schedule the workflow timeout — otherwise a failed enqueue or
        // a 409 lock conflict would leave a dangling timeout job for an instance that never started.
        return await PrepareInstanceAsync(workflow, input, cancellationToken)
            .ThenAsync(data => ExecuteStartTransitionAsync(data, input, cancellationToken)
                .ThenAsync(async output =>
                {
                    await ScheduleWorkflowTimeoutIfConfiguredAsync(
                        data.Workflow, data.Instance, input.Instance.ExtraProperties, cancellationToken);
                    return Result<StartInstanceOutput>.Ok(output);
                }))
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
            Key = existingInstance.Key,
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
                Key = data.Instance.Key,
                Status = transitionOutput.Status
            })
            .ThenAsync(output => input.Sync
                ? EnrichSyncOutputAsync(output, output.Id, data.Workflow, input.Extensions, cancellationToken)
                : Task.FromResult(Result<StartInstanceOutput>.Ok(output)));
    }

    /// <summary>
    /// Schedules a workflow timeout job if the workflow has a timeout configuration.
    /// When extraProperties contains a timeout override (set by SubflowStarter), it takes precedence over the workflow's own timeout.
    /// When a mapping script is configured, it is evaluated to determine the timeout schedule dynamically.
    /// If mapping fails, the static Timer.Duration is used as fallback.
    /// </summary>
    private async Task ScheduleWorkflowTimeoutIfConfiguredAsync(
        Definitions.Workflow workflow,
        Instance instance,
        ExtraPropertyDictionary extraProperties,
        CancellationToken cancellationToken)
    {
        // Check for SubFlow timeout override in ExtraProperties
        WorkflowTimeout? timeoutOverride = null;
        if (extraProperties.TryGetValue(DomainConsts.MetaDataKeys.TimeoutOverride, out var overrideJson)
            && !string.IsNullOrWhiteSpace(overrideJson?.ToString()))
        {
            timeoutOverride = JsonSerializer.Deserialize<WorkflowTimeout>(overrideJson!.ToString()!);
        }

        var effectiveTimeout = timeoutOverride ?? workflow.Timeout;

        // Check if there is any timeout configuration to schedule
        if (effectiveTimeout == null)
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

            var resolvedSchedule = await ResolveTimeoutScheduleAsync(
                effectiveTimeout, workflow, instance, cancellationToken);

            var schedule = resolvedSchedule.ToDaprJobSchedule().ExpressionValue;

            var timeoutAt = resolvedSchedule.ScheduleType == TimerScheduleType.DateTime
                ? resolvedSchedule.ScheduledDateTime!.Value
                : resolvedSchedule.Duration.HasValue
                    ? DateTime.UtcNow.Add(resolvedSchedule.Duration.Value)
                    : DateTime.UtcNow;

            var metadata = new Dictionary<string, object>
            {
                ["domain"] = workflow.Domain,
                ["flowName"] = workflow.Key,
                ["instanceId"] = instance.Id.ToString(),
                ["timeoutAt"] = timeoutAt.ToString("O")
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

            logger.WorkflowTimeoutScheduled(instance.Id, effectiveTimeout.Timer.Duration, timeoutAt);
        }
        catch (Exception ex)
        {
            logger.WorkflowTimeoutSchedulingFailed(ex, instance.Id);
            // Don't throw - timeout scheduling failure should not prevent workflow start
        }
    }

    /// <summary>
    /// Resolves the timeout schedule, trying mapping first (if configured) with static duration as fallback.
    /// </summary>
    private async Task<TimerSchedule> ResolveTimeoutScheduleAsync(
        WorkflowTimeout effectiveTimeout,
        Definitions.Workflow workflow,
        Instance instance,
        CancellationToken cancellationToken)
    {
        if (effectiveTimeout.Mapping != null)
        {
            var scriptContext = await scriptContextFactory.NewBuilder(instanceRepository)
                .WithWorkflow(workflow)
                .WithInstance(instance)
                .WithRuntime(runtimeInfoProvider)
                .WithTransition(effectiveTimeout.Key)
                .WithBody(instance.LatestData?.Data ?? new JsonData("{}"))
                .BuildAsync(cancellationToken);

            var mappingResult = await timerEvaluator.EvaluateAsync(
                effectiveTimeout.Mapping, scriptContext, cancellationToken);

            if (mappingResult.IsSuccess)
            {
                logger.TimeoutMappingResolved(instance.Id, mappingResult.Value!.ScheduleType.ToString());
                return mappingResult.Value!;
            }

            logger.TimeoutMappingFallback(
                instance.Id,
                effectiveTimeout.Timer.Duration,
                mappingResult.Error.Message ?? mappingResult.Error.Code);
        }

        // Static fallback: parse ISO 8601 duration
        var timeoutDuration = System.Xml.XmlConvert.ToTimeSpan(effectiveTimeout.Timer.Duration);
        var timeoutDateTime = DateTime.UtcNow.Add(timeoutDuration);
        return TimerSchedule.FromDateTime(timeoutDateTime);
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
        runtimeInfoProvider.Check(input.Domain);

        // Resolve instance first; workflow is loaded from instance's Flow and FlowVersion (not from request)
        var instanceResult = await instanceRepository.GetActiveAsync(instance, cancellationToken);
        if (!instanceResult.IsSuccess)
            return Result<TransitionOutput>.Fail(instanceResult.Error);

        var resolvedInstance = instanceResult.Value!;

        var workflowResult = await LoadWorkflowAsync(
            input.Domain,
            resolvedInstance.Flow,
            resolvedInstance.FlowVersion,
            cancellationToken);
        if (!workflowResult.IsSuccess)
            return Result<TransitionOutput>.Fail(workflowResult.Error);

        var context = BuildTransitionContext(resolvedInstance, transitionKey, input);

        var workflowDefinition = workflowResult.Value;

        // Pre-dispatch validation guard: validate schema + state-machine policy BEFORE
        // dispatching to the execution service. This guarantees consistent 400 Bad Request
        // behaviour for both sync=true and sync=false callers — the async path would otherwise
        // accept the request, flip the instance to Busy and discover the schema violation
        // later in the background job (leaving the instance Faulted). The same check is also
        // performed inside AsyncTransitionStrategy as defense in depth for callers that
        // bypass the AppService and invoke WorkflowExecutionService directly.
        var preValidation = await ValidateTransitionRequestAsync(context, cancellationToken);
        if (!preValidation.IsSuccess)
        {
            logger.TransitionValidationFailed(resolvedInstance.Id, transitionKey, preValidation.Error.Code);
            return Result<TransitionOutput>.Fail(preValidation.Error);
        }

        return await workflowExecutionService
            .ExecuteTransitionAsync(context, cancellationToken)
            .OnSuccess(output => AddTransitionHeader(output, resolvedInstance.Flow, resolvedInstance.FlowVersion))
            .ThenAsync(output =>
            {
                if (input.Sync)
                    return EnrichSyncOutputAsync(output, output.Id, workflowDefinition, input.Extensions, cancellationToken);
                output.Key = resolvedInstance.Key;
                return Task.FromResult(Result<TransitionOutput>.Ok(output));
            });
    }

    /// <summary>
    /// Pre-dispatch schema + state-machine validation for a transition request.
    /// Builds the execution context via <see cref="ITransitionContextFactory"/> (read-only,
    /// no side effects) and runs the same <see cref="ITransitionValidationService.ValidateAsync"/>
    /// that the sync pipeline uses, so both sync=true and sync=false callers get the same
    /// validation error contract before any state mutation or background-job enqueue.
    /// </summary>
    private async Task<Result> ValidateTransitionRequestAsync(
        WorkflowExecutionContext context,
        CancellationToken cancellationToken)
    {
        var contextResult = await transitionContextFactory.CreateAsync(context, cancellationToken);
        if (!contextResult.IsSuccess)
            return Result.Fail(contextResult.Error);

        return await transitionValidationService.ValidateAsync(contextResult.Value!, cancellationToken);
    }

    /// <summary>
    /// Builds execution context for transition using instance's Flow and FlowVersion (not from request).
    /// </summary>
    private static WorkflowExecutionContext BuildTransitionContext(
        Instance resolvedInstance,
        string transitionKey,
        TransitionInput input)
    {
        return new WorkflowExecutionContext
        {
            Domain = input.Domain,
            InstanceId = resolvedInstance.Id.ToString(),
            WorkflowKey = resolvedInstance.Flow,
            WorkflowVersion = resolvedInstance.FlowVersion,
            TransitionKey = transitionKey,
            TriggerType = TriggerType.Manual,
            Mode = input.Sync ? ExecMode.Sync : ExecMode.Async,
            CorrelationId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Headers = input.Headers,
            RouteValues = input.RouteValues,
            Data = new TransitionDataInfo(input.Data?.Key, input.Data?.Attributes)
            {
                Tags = input.Data?.Tags,
            },
            IsReentry = false,
        };
    }

    /// <summary>
    /// Adds workflow header to the transition response using instance flow and version.
    /// </summary>
    private void AddTransitionHeader(TransitionOutput output, string flow, string? version)
    {
        headerService.AddHeader(
            WorkflowInfo.Name,
            WorkflowInfo.Generate(runtimeInfoProvider.Domain, flow, version, output.Id)
        );
    }

    /// <summary>
    /// Enriches a sync=true StartInstanceOutput with attributes (schema-filtered), etag, entityEtag, key and extensions.
    /// The instance is reloaded from the repository to ensure post-execution state is reflected.
    /// </summary>
    private Task<Result<StartInstanceOutput>> EnrichSyncOutputAsync(
        StartInstanceOutput output,
        Guid instanceId,
        Definitions.Workflow? workflow,
        string[]? extensionRequested,
        CancellationToken cancellationToken)
        => EnrichOutputCoreAsync(output, instanceId, workflow, extensionRequested, cancellationToken);

    /// <summary>
    /// Enriches a sync=true TransitionOutput with attributes (schema-filtered), etag, entityEtag, key and extensions.
    /// The instance is reloaded from the repository to ensure post-execution state is reflected.
    /// </summary>
    private Task<Result<TransitionOutput>> EnrichSyncOutputAsync(
        TransitionOutput output,
        Guid instanceId,
        Definitions.Workflow? workflow,
        string[]? extensionRequested,
        CancellationToken cancellationToken)
        => EnrichOutputCoreAsync(output, instanceId, workflow, extensionRequested, cancellationToken);

    private async Task<Result<TOutput>> EnrichOutputCoreAsync<TOutput>(
        TOutput output,
        Guid instanceId,
        Definitions.Workflow? workflow,
        string[]? extensionRequested,
        CancellationToken cancellationToken)
        where TOutput : class
    {
        var freshInstance = await instanceRepository.FindByIdentifierAsync(instanceId.ToString(), cancellationToken);
        if (freshInstance is null)
            return Result<TOutput>.Ok(output);

        var latestData = freshInstance.LatestData;
        var rawAttributes = latestData?.Data.JsonElement;
        var filteredAttributes = await schemaFieldFilterService.ApplyAsync(workflow, rawAttributes, freshInstance, cancellationToken);

        var key = freshInstance.Key;
        var entityEtag = latestData?.ETag;
        var attributes = filteredAttributes ?? rawAttributes;

        Dictionary<string, object> extensions = new();
        if (workflow is not null)
        {
            var scriptContext = await scriptContextFactory.NewBuilder(instanceRepository)
                .WithWorkflow(workflow)
                .WithInstance(freshInstance)
                .WithRuntime(runtimeInfoProvider)
                .WithTransition(string.Empty)
                .WithBody(latestData?.Data ?? new JsonData("{}"))
                .BuildAsync(cancellationToken);

            var extensionsResult = await instanceExtensionService.ProcessExtensionsAsync(
                extensionRequested,
                scriptContext,
                workflow,
                ExtensionScope.GetInstance,
                cancellationToken);

            if (!extensionsResult.IsSuccess)
                logger.ExtensionProcessingFailedNonBlocking(extensionsResult.Error.Code);
            else
                extensions = extensionsResult.Value!;
        }

        if (output is StartInstanceOutput start)
        {
            start.Key = key;
            start.Attributes = attributes;
            start.EntityEtag = entityEtag;
            start.Extensions = extensions;
            // ETag must be generated after all other fields are set (ETag itself is null at generation time)
            start.ETag = representationEtagService.Generate(start);
        }
        else if (output is TransitionOutput transition)
        {
            transition.Key = key;
            transition.Attributes = attributes;
            transition.EntityEtag = entityEtag;
            transition.Extensions = extensions;
            // ETag must be generated after all other fields are set (ETag itself is null at generation time)
            transition.ETag = representationEtagService.Generate(transition);
        }

        return Result<TOutput>.Ok(output);
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
                var instance = Instance.Create(instanceId, workflow.Key, workflow.Version, instanceKey);
                instance.SetInfoMetadata(isSync, callback, workflow.Type.Code, metadata);
                instance.ChangeState(initialState);

                if (tags?.Any() == true)
                    instance.AddTags(tags.ToArray());

                return instance;
            }));
    }
}