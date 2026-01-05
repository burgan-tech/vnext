using BBT.Aether.DependencyInjection;
using BBT.Aether.Events;
using BBT.Aether.MultiSchema;
using BBT.Aether.Uow;
using BBT.Workflow.Instances.Events;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.SubFlow;

namespace BBT.Workflow.Workers.Inbox.Handlers;

/// <summary>
/// Handles the InstanceSubCompletedEvent to process SubFlow/SubProcess completion.
/// This handler delegates the actual completion logic to ISubflowCompletionService.
/// </summary>
internal sealed class InstanceSubCompletedEventHandler(
    ICurrentSchema currentSchema,
    IRuntimeInfoProvider runtimeInfoProvider,
    IServiceScopeFactory scopeFactory,
    ILogger<InstanceSubCompletedEventHandler> logger) : IEventHandler<InstanceSubCompletedEvent>
{
    /// <summary>
    /// Handles the InstanceSubCompletedEvent by delegating to the subflow completion service.
    /// </summary>
    public async Task HandleAsync(CloudEventEnvelope<InstanceSubCompletedEvent> envelope,
        CancellationToken cancellationToken)
    {
        var eventData = envelope.Data;

        if (!runtimeInfoProvider.IsDomainMatch(eventData.Domain))
        {
            logger.SubFlowEventIgnoredDomainMismatch(
                eventData.Domain,
                runtimeInfoProvider.Domain,
                eventData.SubInstanceId,
                eventData.InstanceId);
            return;
        }

        logger.SubFlowEventReceived(
            eventData.SubInstanceId,
            eventData.InstanceId,
            eventData.Domain,
            eventData.Flow);

        using (currentSchema.Use(eventData.Flow))
        {
            var completedData = new FlowCompletedInput
            {
                SubInstanceId = eventData.SubInstanceId,
                InstanceId = eventData.InstanceId,
                Domain = eventData.Domain,
                Flow = eventData.Flow,
                Version = eventData.Version,
                CompletedState = eventData.CompletedState,
                InstanceData = eventData.InstanceData,
                CompletedAt = eventData.CompletedAt,
                Duration = eventData.Duration
            };

            await using var scope = scopeFactory.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            AmbientServiceProvider.Root = sp; // TODO: WARN: This configuration will be configured within Aether.
            var uowManager = sp.GetRequiredService<IUnitOfWorkManager>();
            var subflowCompletionService = sp.GetRequiredService<ISubflowCompletionService>();
            
            await using var uow = await uowManager.BeginAsync(new UnitOfWorkOptions
            {
                Scope = UnitOfWorkScopeOption.RequiresNew
            }, cancellationToken);
            await subflowCompletionService.CompletionAsync(completedData, cancellationToken);
            await uow.CommitAsync(cancellationToken);
        }
    }
}