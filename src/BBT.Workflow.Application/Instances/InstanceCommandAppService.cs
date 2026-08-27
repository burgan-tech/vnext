using System.Diagnostics;
using System.Text.Json;
using BBT.Aether;
using BBT.Aether.Users;
using BBT.Workflow.Authorization;
using BBT.Workflow.Execution.LongPoll;
using BBT.Workflow.Gateway;
using BBT.Workflow.RepresentationEtag;
using BBT.Aether.Application.Services;
using BBT.Aether.BackgroundJob;
using BBT.Aether.Guids;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Workflow.BackgroundJobs.Handlers;
using BBT.Workflow.BackgroundJobs.Payloads;
using BBT.Workflow.Caching;
using BBT.Workflow.CurrentUser;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Pipeline;
using BBT.Workflow.Execution.Services;
using BBT.Workflow.Logging;
using BBT.Workflow.Execution.Transitions.Services;
using BBT.Workflow.Execution.Validation;
using BBT.Workflow.Extentions;
using BBT.Workflow.Headers;
using BBT.Workflow.Runtime;
using BBT.Workflow.Definitions.Timer;
using Dapr.Jobs.Models;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Evaluation;
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
    IInstanceDataWriteService instanceDataWriteService,
    IInstanceJobRepository instanceJobRepository,
    IBackgroundJobService backgroundJobService,
    IGuidGenerator guidGenerator,
    IHeaderService headerService,
    ITransitionDataMapper transitionDataMapper,
    ITransitionValidationService transitionValidationService,
    ITransitionAdmissionService transitionAdmissionService,
    IRepresentationEtagService representationEtagService,
    ISchemaFieldFilterService schemaFieldFilterService,
    IInstanceExtensionService instanceExtensionService,
    IScriptContextFactory scriptContextFactory,
    ITimerEvaluator timerEvaluator,
    ITransitionAuthorizationManager transitionAuthorizationManager,
    IInstanceCancellationService cancellationService,
    ILongPollAckResumeService longPollAckResumeService,
    IInstanceCommandGateway instanceCommandGateway,
    IWorkflowOutputMappingService workflowOutputMappingService,
    ICurrentUser currentUser,
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
                return await EnrichSyncOutputAsync(existingInstanceResult.Value.Value!, existingInstanceResult.Value.Value!.Id, workflow, input.Extensions, new AuthorizationRequestContext(input.Headers), cancellationToken);
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
            .OnSuccess(output =>
            {
                // Emitted while the start HTTP request is live: the log-record enrichers attach
                // the request id, closing the X-Request-Id ↔ instance-id join for the client.
                logger.InstanceStarted(output.Id, output.Key, input.Domain, input.Workflow, workflow.Version);
                AddWorkflowHeader(output, input);
            });
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

        // Key path: only a non-terminal (Active/Busy) row occupies the key. A plain
        // FindByIdentifierAsync(key) uses FirstOrDefault with no status filter, so when terminal
        // history rows share the key it can return an arbitrary (possibly completed) row and let a
        // second active instance be created. FindActiveByKeyLeanAsync is the deterministic check.
        // Id path: the PK is unique, so the lean identifier lookup is already deterministic.
        // Both probes are include-free: only Id/Key/Status are read below.
        var existingInstance = !instanceKey.IsNullOrWhiteSpace()
            ? await instanceRepository.FindActiveByKeyLeanAsync(instanceKey, cancellationToken)
            : instanceId.HasValue
                ? (await instanceRepository.GetResultAsync(
                    instanceId.Value.ToString(), includeDetails: false, cancellationToken)) is { IsSuccess: true } leanResult
                    ? leanResult.Value
                    : null
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

    /// <inheritdoc />
    public async Task<Result> AcknowledgeLongPollAsync(
        AcknowledgeLongPollInput input,
        CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(input.Domain);

        var instanceResult = await instanceRepository.GetActiveAsync(input.Instance, cancellationToken);
        if (!instanceResult.IsSuccess)
            return Result.Fail(instanceResult.Error);

        var instance = instanceResult.Value!;

        // Not the awaiting instance — descend the active SubFlow chain (local or cross-domain via the
        // gateway) to the instance that actually paused. The State function follows the same SubFlow
        // chain downward, so the awaiting instance is the deepest active subflow child.
        if (!instance.IsAwaitingLongPollAck)
        {
            var sub = instance.Subflow;
            if (sub is not null)
            {
                return await instanceCommandGateway.AcknowledgeLongPollAsync(new AcknowledgeLongPollInput
                {
                    Domain = sub.SubFlowDomain,
                    Workflow = sub.SubFlowName,
                    Instance = sub.SubFlowInstanceId.ToString(),
                    Version = sub.SubFlowVersion,
                    Role = input.Role,
                    Headers = input.Headers
                }, cancellationToken);
            }

            // Idempotent: nothing awaiting anywhere in the chain (already resumed or fallback fired).
            return Result.Ok();
        }

        var workflowResult = await LoadWorkflowAsync(input.Domain, input.Workflow, input.Version, cancellationToken);
        if (!workflowResult.IsSuccess)
            return Result.Fail(workflowResult.Error);

        var workflow = workflowResult.Value!;

        // Role check against the entered state's interaction.longPoll.roles (default-allow when none).
        var state = workflow.FindState(instance.GetCurrentState);
        var ackRoles = state?.LongPollAckRoles;
        if (ackRoles is { Count: > 0 })
        {
            var callerRoles = BuildCallerRoles(input.Role, input.Headers);
            var allowed = await transitionAuthorizationManager.IsAnyRoleAllowedForGrantsAsync(
                callerRoles, ackRoles, instance,
                new AuthorizationRequestContext(input.Headers), cancellationToken);
            if (!allowed)
                return Result.Fail(WorkflowErrors.LongPollAckAccessDenied(instance.Id));
        }

        // Best-effort cancel the fallback timeout job; the token guard in the resume path keeps the
        // operation safe even if cancellation is missed.
        await cancellationService.ProcessStateTransitionsCancellationAsync(
            instance.Id, sourceState: null, [LongPollAckConstants.JobKey], cancellationToken);

        return await longPollAckResumeService.ResumeAsync(
            workflow.Domain, workflow.Key, workflow.Version, instance.Id, cancellationToken);
    }

    /// <summary>
    /// Builds the caller role set for the long-poll acknowledge check: the explicit <c>role</c> parameter
    /// plus the caller's resolved roles. Resolution honors the legacy <c>role</c> header, because
    /// <c>ChangeFromHeaders</c> does not run on the HTTP path and a header-only caller would otherwise be
    /// rejected with 403 by an allowlist grant set.
    /// </summary>
    private IReadOnlyCollection<string> BuildCallerRoles(
        string? explicitRole,
        IReadOnlyDictionary<string, string?>? headers)
    {
        var roles = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitRole))
            roles.Add(explicitRole.Trim());
        var resolved = currentUser.ResolveCallerRoles(headers);
        if (resolved is { Length: > 0 })
            roles.AddRange(resolved);
        return roles;
    }

    /// <summary>
    /// Step 2: Loads the workflow definition from the component cache.
    /// </summary>
    /// <remarks>
    /// This used to short-circuit on a scope-held definition (the retired <c>IWorkflowContext</c>),
    /// but that memo compared the KEY only — a request pinning a version silently got whatever
    /// version the scope happened to hold. The component cache already answers a repeat read from
    /// its in-process layer, so the memo bought little and risked the wrong definition.
    /// </remarks>
    private async Task<Result<Definitions.Workflow>> LoadWorkflowAsync(
        string domain,
        string workflow,
        string? version,
        CancellationToken cancellationToken)
    {
        var workflowResult = await componentCacheStore.GetFlowAsync(
            domain, workflow, version, cancellationToken);

        return workflowResult.IsSuccess
            ? Result<Definitions.Workflow>.Ok(workflowResult.Value!)
            : Result<Definitions.Workflow>.Fail(workflowResult.Error);
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
        await using var uow = UnitOfWorkManager.Begin(
            new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew, IsTransactional = true });
        var result = await CreateAndPrepareInstanceAsync(
                workflow,
                input.Instance.Id ?? guidGenerator.Create(),
                input.Instance.Key,
                input.Instance.Tags?.ToList(),
                input.Instance.Stage,
                input.Instance.ExtraProperties,
                input.Sync,
                input.Instance.Callback,
                cancellationToken)
            .ThenAsync(instance => ValidateStartTransitionAsync(workflow, instance, input, cancellationToken))
            .ThenAsync(instance => PersistInstanceAsync(workflow, instance, cancellationToken))
            .ThenAsync(data => MapAndAppendInstanceDataAsync(data, input, cancellationToken));

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
    /// Persists the instance to the repository (data-less — the initial data version is
    /// appended AFTERWARDS through the write service, so its row lock has a row to grab).
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
    /// Maps the start payload and appends the initial data version IMMEDIATELY through the
    /// InstanceData write service (identity computed under the per-instance row lock).
    /// </summary>
    private async Task<Result<(Definitions.Workflow Workflow, Instance Instance)>> MapAndAppendInstanceDataAsync(
        (Definitions.Workflow Workflow, Instance Instance) data,
        StartInstanceInput input,
        CancellationToken cancellationToken)
    {
        if (input.Instance.Attributes == null)
            return Result<(Definitions.Workflow, Instance)>.Ok(data);

        return await transitionDataMapper.MapTransitionDataAsync(
                input.Instance.Attributes,
                data.Workflow.StartTransition,
                data.Workflow,
                data.Instance,
                runtimeInfoProvider,
                input.Headers,
                cancellationToken)
            .TapAsync(async mappedData =>
            {
                if (mappedData != null)
                {
                    await instanceDataWriteService.AppendAsync(
                        data.Instance,
                        new JsonData(mappedData),
                        data.Workflow.StartTransition.VersionStrategy,
                        cancellationToken,
                        data.Workflow);
                }
            })
            .MapAsync(_ => data);
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

        // The definition and the payload verdict both travel with the request: the start already
        // resolved this flow, and it already validated the payload against the start transition's
        // schema — necessarily so, because that check has to happen before the instance row is
        // persisted, which is earlier than any execution entry runs.
        context.ResolvedWorkflow = data.Workflow;
        context.PayloadSchemaValidated = true;

        // Creation is the reservation: a sub-item is persisted Busy at creation (IsSubItem seed
        // in CreateAndPrepareInstanceAsync), and this request — the one that just created the
        // row — owns it. Without this, the child's own start transition classifies as Normal
        // and rejects itself with 409 against its birth-Busy status. Regular instances are
        // created Active and keep the normal reserve path. Applies uniformly to sync/async and
        // local/cross-domain subflow starts, since every start funnels through here.
        context.IsPreReserved = data.Instance.IsBusy;

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
                ? EnrichSyncOutputAsync(output, output.Id, data.Workflow, input.Extensions, new AuthorizationRequestContext(input.Headers), cancellationToken)
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
            var jobName = JobName.ForTimeout(instance.Id);
            var activity = Activity.Current;
            var payload = new WorkflowTimeoutPayload
            {
                JobName = jobName.Value,
                Domain = workflow.Domain,
                InstanceId = instance.Id,
                FlowName = workflow.Key,
                Version = workflow.Version,
                TraceParent = activity?.Id,
                TraceState = activity?.TraceStateString
            };

            var resolvedSchedule = await ResolveTimeoutScheduleAsync(
                effectiveTimeout, workflow, instance, cancellationToken);

            // One instant feeds the scheduler arming, the timeoutAt metadata and the persisted
            // ExecuteAt — separate clock reads would make the three disagree for Duration schedules.
            var timeoutAt = resolvedSchedule.ResolveExecuteAt(DateTimeOffset.UtcNow);
            var schedule = DaprJobSchedule.FromDateTime(timeoutAt).ExpressionValue;

            var metadata = new Dictionary<string, object>
            {
                ["domain"] = workflow.Domain,
                ["flowName"] = workflow.Key,
                ["instanceId"] = instance.Id.ToString(),
                ["timeoutAt"] = timeoutAt.UtcDateTime.ToString("O")
            };

            // Enqueue the timeout job
            // directly: true arms the scheduler as part of the enqueue, like every other job type in
            // this codebase. Omitting it persisted the row as Pending and called nobody, leaving the
            // timeout to the background-job arming poller — which is disabled
            // (BackgroundJob:WithHostedService = false), so workflow timeouts were never armed and
            // never fired: no error, no log, the row just sat there.
            var jobId = await backgroundJobService.EnqueueAsync(
                FlowTimeoutJobHandler.HandlerName,
                jobName.Value,
                payload,
                schedule,
                metadata,
                directly: true,
                cancellationToken: cancellationToken);

            // Track the job in the database
            await instanceJobRepository.InsertAsync(
                InstanceJob.Create(
                    jobId,
                    jobName,
                    jobId,
                    workflow.Domain,
                    workflow.Key,
                    instance.Id,
                    timeoutAt
                ),
                true,
                cancellationToken);

            logger.WorkflowTimeoutScheduled(instance.Id, effectiveTimeout.Timer.Duration, timeoutAt.UtcDateTime);
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

        // 1) Light Busy check FIRST — a single-row projection (no DataList, no includes) so a
        //    Busy rejection never pays the full aggregate load. The snapshot also carries the
        //    bound Flow/FlowVersion, so the workflow definition is resolved ONCE here (from the
        //    component cache) and reused for the rest of the request. Classification decides the
        //    fast-fail: cancel/exit/updateData are exempt, and a Busy parent with an active
        //    SubFlow is admitted so the pipeline can forward the request to the subflow. This is
        //    a fast-fail only — the authoritative decision is the reserve under the status lock.
        var snapshot = await instanceRepository.GetExecutionSnapshotAsync(instance, cancellationToken);

        Definitions.Workflow? workflowDefinition = null;
        if (snapshot is not null)
        {
            var snapshotWorkflow = await LoadWorkflowAsync(
                input.Domain, snapshot.Flow!, snapshot.FlowVersion, cancellationToken);
            if (!snapshotWorkflow.IsSuccess)
                return Result<TransitionOutput>.Fail(snapshotWorkflow.Error);

            workflowDefinition = snapshotWorkflow.Value!;

            // input.ChainReserved exempts the relay: the accept that admitted the request already
            // reserved this instance's Busy flag as part of its SubFlow chain reserve, so the Busy
            // it finds here is its own. Server-internal — see TransitionInput.ChainReserved.
            if (snapshot is { IsBusy: true, HasActiveSubFlow: false }
                && !input.ChainReserved
                && transitionAdmissionService.ClassifyKey(workflowDefinition, transitionKey)
                    == AdmissionKind.Normal)
            {
                logger.TransitionRejectedInstanceBusy(snapshot.Id, transitionKey);
                return Result<TransitionOutput>.Fail(
                    WorkflowErrors.InstanceBusy(snapshot.Id, transitionKey));
            }
        }

        // 2) Admitted. The intake does NOT materialize the aggregate: everything left to do here —
        //    build the dispatch context, stamp the response headers — reads Id/Flow/FlowVersion/Key,
        //    all of which the projection above already carries. The execution entry loads the
        //    aggregate for real, in its own scope and DbContext; loading it here as well was a
        //    second ~20 ms round trip for a copy that could not be handed across that boundary.
        // Reproduced verbatim from the aggregate load this replaces (EfCoreInstanceRepository's
        // GetResultAsync): same code, same message. WorkflowErrors.InstanceNotFound is a DIFFERENT
        // code (NotFoundInstanceData) despite the name, and using it here would silently change the
        // error a client sees for an unknown instance.
        if (snapshot is null)
        {
            return Result<TransitionOutput>.Fail(Error.NotFound(
                WorkflowErrorCodes.InstanceNotFound,
                $"Instance with ID {instance} not found",
                instance));
        }

        // Terminal check the aggregate load used to perform (GetActiveAsync). IsTerminal, not
        // IsCompleted: Faulted and Passive are terminal for admission too.
        if (snapshot.IsTerminal)
        {
            return Result<TransitionOutput>.Fail(Error.Validation(
                WorkflowErrorCodes.InstanceCompleted,
                $"Instance {instance} is already completed with status: {snapshot.Status.Code}",
                instance));
        }

        var context = BuildTransitionContext(snapshot, transitionKey, input, workflowDefinition!);

        // No validation here. Validation belongs to the execution entry, and both entries run it
        // before any side effect: the async strategy validates before it flips Busy and enqueues,
        // the sync pipeline before it admits. Validating here as well meant every request resolved
        // its schema twice and built a whole execution context that was then thrown away — and it
        // bought nothing, because the 400-before-side-effect guarantee it was written for is the
        // execution entry's guarantee too. The Busy fast-fail above is a different check and stays.

        return await workflowExecutionService
            .ExecuteTransitionAsync(context, cancellationToken)
            .OnSuccess(output => AddTransitionHeader(output, snapshot.Flow!, snapshot.FlowVersion))
            .ThenAsync(output =>
            {
                if (input.Sync)
                    return EnrichSyncOutputAsync(output, output.Id, workflowDefinition, input.Extensions, new AuthorizationRequestContext(input.Headers), cancellationToken);
                output.Key = snapshot.Key;
                return Task.FromResult(Result<TransitionOutput>.Ok(output));
            });
    }


    /// <summary>
    /// Builds execution context for transition using instance's Flow and FlowVersion (not from request).
    /// </summary>
    private static WorkflowExecutionContext BuildTransitionContext(
        InstanceExecutionSnapshot snapshot,
        string transitionKey,
        TransitionInput input,
        Definitions.Workflow workflow)
    {
        return new WorkflowExecutionContext
        {
            Domain = input.Domain,
            InstanceId = snapshot.Id.ToString(),
            // The instance's OWN bound flow, never the request's — same rule as before, now read
            // from the projection that resolved the identifier.
            WorkflowKey = snapshot.Flow!,
            WorkflowVersion = snapshot.FlowVersion,
            // Carried, not re-resolved downstream: this is the very definition the layers below
            // would look up with the three coordinates above.
            ResolvedWorkflow = workflow,
            TransitionKey = transitionKey,
            TriggerType = TriggerType.Manual,
            Actor = input.Actor,
            Mode = input.Sync ? ExecMode.Sync : ExecMode.Async,
            CallerMode = input.Sync ? ExecMode.Sync : ExecMode.Async,
            CorrelationId = Guid.NewGuid().ToString("N"),
            RequestedAt = DateTimeOffset.UtcNow,
            Headers = input.Headers,
            RouteValues = input.RouteValues,
            Data = new TransitionDataInfo(input.Data?.Key, input.Data?.Attributes)
            {
                Stage = input.Data?.Stage,
                Tags = input.Data?.Tags,
            },
            IsReentry = false,

            // A relayed subflow transition whose chain the accept already reserved re-enters as
            // the owner: admission classifies it OwnerReentry, so it neither 409s on the Busy the
            // accept pre-set nor reserves a second time — and it still settles the status at the
            // end, because OwnerReentry sets OwnsStatus.
            IsPreReserved = input.ChainReserved,
            SubflowChainReserved = input.ChainReserved
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
        AuthorizationRequestContext? requestContext,
        CancellationToken cancellationToken)
        => EnrichOutputCoreAsync(output, instanceId, workflow, extensionRequested, requestContext, cancellationToken);

    /// <summary>
    /// Enriches a sync=true TransitionOutput with attributes (schema-filtered), etag, entityEtag, key and extensions.
    /// The instance is reloaded from the repository to ensure post-execution state is reflected.
    /// </summary>
    private Task<Result<TransitionOutput>> EnrichSyncOutputAsync(
        TransitionOutput output,
        Guid instanceId,
        Definitions.Workflow? workflow,
        string[]? extensionRequested,
        AuthorizationRequestContext? requestContext,
        CancellationToken cancellationToken)
        => EnrichOutputCoreAsync(output, instanceId, workflow, extensionRequested, requestContext, cancellationToken);

    private async Task<Result<TOutput>> EnrichOutputCoreAsync<TOutput>(
        TOutput output,
        Guid instanceId,
        Definitions.Workflow? workflow,
        string[]? extensionRequested,
        AuthorizationRequestContext? requestContext,
        CancellationToken cancellationToken)
        where TOutput : class
    {
        // Use pipeline instance if available (already committed, avoids redundant DB read).
        // A Busy snapshot may be stale — a sync subflow completion resumes and finalizes the
        // parent in its own scope — so re-read as no-tracking (this scope may already track the
        // row with pre-resume values) to reflect the settled state. Also falls back to the DB
        // read when PipelineInstance is not set (e.g. idempotent existing-instance path).
        var pipelineInstance = (output as InstanceOutputBase)?.PipelineInstance;
        var instance = pipelineInstance is not null && !pipelineInstance.Status.Equals(InstanceStatus.Busy)
            ? pipelineInstance
            : await instanceRepository.FindByIdentifierAsReadOnlyAsync(instanceId.ToString(), cancellationToken)
              ?? pipelineInstance;

        if (instance is null)
            return Result<TOutput>.Ok(output);

        // The pipeline reported Busy but the chain has settled to a terminal status meanwhile
        // (subflow resume finalized the parent in another scope) — surface the settled status.
        if (output is InstanceOutputBase outputBase
            && outputBase.Status?.Equals(InstanceStatus.Busy) == true
            && instance.IsCompleted)
        {
            outputBase.Status = instance.Status;
        }

        var latestData = instance.LatestData;
        var rawAttributes = latestData?.Data.JsonElement;
        var filteredAttributes = await schemaFieldFilterService.ApplyAsync(workflow, rawAttributes, instance, requestContext, cancellationToken);

        var key = instance.Key;
        var entityEtag = latestData?.ETag;
        var attributes = filteredAttributes ?? rawAttributes;

        Dictionary<string, object> extensions = new();
        WorkflowOutputResult? outputResponse = null;
        if (workflow is not null)
        {
            var scriptContext = await scriptContextFactory.NewBuilder(instanceRepository)
                .WithWorkflow(workflow)
                .WithInstance(instance)
                .WithRuntime(runtimeInfoProvider)
                .WithTransition(string.Empty)
                .WithBody(latestData?.Data ?? new JsonData("{}"))
                .BuildAsync(cancellationToken);

            // Workflow output mapping bypasses the standard envelope, but NEVER for subflow
            // instances: correlation forward (sub start) and subflow transitions rely on the
            // standard model, so output is ignored even when configured.
            if (!instance.IsSubFlow)
            {
                var outputResult = await workflowOutputMappingService.ApplyAsync(workflow, scriptContext, cancellationToken);
                if (outputResult.IsSuccess && outputResult.Value is { } wo)
                    outputResponse = wo;
            }

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
            start.PipelineInstance = null;
            start.ETag = representationEtagService.Generate(start);
            ApplyOutputResponse(start, outputResponse);
        }
        else if (output is TransitionOutput transition)
        {
            transition.Key = key;
            transition.Attributes = attributes;
            transition.EntityEtag = entityEtag;
            transition.Extensions = extensions;
            transition.PipelineInstance = null;
            transition.ETag = representationEtagService.Generate(transition);
            ApplyOutputResponse(transition, outputResponse);
        }

        return Result<TOutput>.Ok(output);
    }

    /// <summary>
    /// Copies the workflow output mapping result onto the output DTO. When set, the controller
    /// mapper returns the payload directly (bypassing the standard envelope), even if Data is null.
    /// </summary>
    private static void ApplyOutputResponse(InstanceOutputBase output, WorkflowOutputResult? outputResponse)
    {
        if (outputResponse is null)
            return;

        output.HasOutputResponse = true;
        output.OutputData = outputResponse.Data;
        output.OutputStatusCode = outputResponse.StatusCode;
        output.OutputHeaders = outputResponse.Headers;
    }

    /// <summary>
    /// Creates and prepares a new instance with the provided parameters.
    /// Note: Existing instance check is done in CheckExistingInstanceAsync (idempotent behavior).
    /// </summary>
    private static Task<Result<Instance>> CreateAndPrepareInstanceAsync(
        Definitions.Workflow workflow,
        Guid instanceId,
        string? instanceKey,
        List<string>? tags,
        string? stage,
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

                if (instance.IsSubItem)
                    instance.Busy();

                if (tags?.Any() == true)
                    instance.AddTags(tags.ToArray());

                if (!string.IsNullOrWhiteSpace(stage))
                    instance.SetStage(stage);

                return instance;
            }));
    }
}