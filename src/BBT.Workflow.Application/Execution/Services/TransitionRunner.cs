using BBT.Aether.Events;
using BBT.Aether.Results;
using BBT.Aether.Uow;
using BBT.Aether.Users;
using BBT.Workflow.CurrentUser;
using BBT.Workflow.Instances;
using BBT.Workflow.Resilience;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace BBT.Workflow.Execution.Services;

/// <summary>
/// Orchestrates transition execution with isolated DI scope and UoW.
/// Transition chaining (auto/scheduled) is now handled by TransitionPipeline via sync dispatch.
/// This runner focuses on UoW lifecycle management for a single transition execution.
/// Uses ExecuteWithWorkflowAsync extension for automatic workflow loading and context management.
/// After UoW commit, publishes deferred domain events via IDistributedEventBus.
/// Registered as a singleton: the Polly ResiliencePipeline is built once in the constructor and
/// reused across all requests, avoiding per-scope rebuild overhead.
/// </summary>
public sealed class TransitionRunner : ITransitionRunner
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TransitionRunner> _logger;
    private readonly ResiliencePipeline<Result<TransitionCoreOutput>> _pipeline;

    /// <summary>
    /// Test seam: overrides the default scope-based execution delegate.
    /// Assigned via object initializer or constructor in production; set to
    /// <see cref="ExecuteWithScopeAsync"/> by default.
    /// </summary>
    internal Func<WorkflowExecutionContext, CancellationToken, Task<Result<TransitionCoreOutput>>> ScopeDelegate
    {
        get; init;
    }

    /// <summary>
    /// Initializes a new instance of <see cref="TransitionRunner"/>.
    /// Builds the Polly retry pipeline once; safe to use as a singleton because all
    /// constructor dependencies are singleton-safe and no scoped services are captured.
    /// </summary>
    public TransitionRunner(
        IServiceScopeFactory scopeFactory,
        ILogger<TransitionRunner> logger,
        IOptions<DbRetryOptions> retryOptions,
        IDbTransientErrorClassifier classifier)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;

        var opts = retryOptions.Value;

        _pipeline = new ResiliencePipelineBuilder<Result<TransitionCoreOutput>>()
            .AddRetry(new RetryStrategyOptions<Result<TransitionCoreOutput>>
            {
                MaxRetryAttempts = opts.MaxRetryAttempts,
                Delay = TimeSpan.FromMilliseconds(opts.BaseDelayMilliseconds),
                MaxDelay = TimeSpan.FromMilliseconds(opts.MaxDelayMilliseconds),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = opts.UseJitter,
                ShouldHandle = new PredicateBuilder<Result<TransitionCoreOutput>>()
                    .Handle<Exception>(classifier.IsRetriableTransient)
            })
            .Build();

        ScopeDelegate = ExecuteWithScopeAsync;
    }

    /// <inheritdoc />
    /// <summary>
    /// Runs a transition in its own DI scope + RequiresNew UoW, wrapped in a Polly retry
    /// pipeline that retries only genuinely transient DB faults (not pool exhaustion).
    /// Sync dispatch chain for auto transitions is managed by TransitionPipeline.
    /// </summary>
    public async Task<Result<TransitionOutput>> RunAsync(
        WorkflowExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var hopResult = await _pipeline.ExecuteAsync(
            async ct => await ScopeDelegate(context, ct),
            cancellationToken);

        if (!hopResult.IsSuccess)
            return Result<TransitionOutput>.Fail(hopResult.Error);

        var coreOutput = hopResult.Value!;
        return Result<TransitionOutput>.Ok(coreOutput.Output);
    }

    /// <summary>
    /// Executes the transition in a new DI scope with RequiresNew UoW.
    /// This ensures complete isolation from any ambient UoW.
    /// After commit, publishes deferred domain events collected during pipeline execution.
    /// Uses ExecuteWithWorkflowAsync extension for automatic workflow loading and IWorkflowContext setup.
    /// </summary>
    private Task<Result<TransitionCoreOutput>> ExecuteWithScopeAsync(
        WorkflowExecutionContext context,
        CancellationToken cancellationToken)
    {
        return _scopeFactory.ExecuteWithWorkflowAsync(context.Domain, context.WorkflowKey, context.WorkflowVersion,
            async (sp, ct) =>
            {
                var uowManager = sp.GetRequiredService<IUnitOfWorkManager>();
                var core = sp.GetRequiredService<IWorkflowExecutionCore>();
                var currentUser = sp.GetRequiredService<ICurrentUser>();

                using (currentUser.ChangeFromHeaders(context.Headers))
                {
                    await using var uow = await uowManager.BeginAsync(
                        new UnitOfWorkOptions { Scope = UnitOfWorkScopeOption.RequiresNew },
                        ct);

                    var coreResult = await core.ExecuteTransitionCoreAsync(context, ct);
                    if (!coreResult.IsSuccess)
                        return Result<TransitionCoreOutput>.Fail(coreResult.Error);

                    await uow.CommitAsync(ct);

                    await PublishDeferredEventsAsync(sp, coreResult.Value!, ct);

                    return coreResult;
                }
            }, cancellationToken);
    }

    /// <summary>
    /// Publishes deferred domain events via IDistributedEventBus after UoW commit.
    /// Each event passes through HookedDistributedEventBus, preserving hook behavior.
    /// Events include pre-extracted metadata from AddDistributedEvent time.
    /// </summary>
    private async Task PublishDeferredEventsAsync(
        IServiceProvider sp,
        TransitionCoreOutput coreOutput,
        CancellationToken ct)
    {
        if (coreOutput.DeferredEvents.Count == 0)
            return;

        var eventBus = sp.GetRequiredService<IDistributedEventBus>();

        foreach (var envelope in coreOutput.DeferredEvents)
        {
            try
            {
                await eventBus.PublishAsync(envelope.Event, envelope.Metadata, cancellationToken: ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to publish deferred event {EventType} for transition",
                    envelope.Event.GetType().Name);
            }
        }
    }
}