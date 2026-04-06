using System.Collections.Concurrent;
using System.Reflection;
using BBT.Aether.DependencyInjection;
using BBT.Aether.Events;
using BBT.Workflow.Events.Hooks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Infrastructure.EventBus;

/// <summary>
/// Decorator implementation of <see cref="IDistributedEventBus"/> that executes hooks
/// before publishing events. Hooks are discovered via DI using <see cref="IEventHookInvoker"/>.
/// </summary>
/// <remarks>
/// <para>
/// This decorator wraps the Aether SDK's event bus implementation and intercepts
/// event publishing to execute registered hooks. Hooks can perform side effects,
/// validate data, or enrich event metadata before the event is published.
/// </para>
/// <para>
/// <b>Hook Success Behavior:</b> If all hooks succeed, the event is considered 
/// "handled" by the hooks and will NOT be published to the inner bus.
/// If any hook fails, the event will be published to the inner bus as a fallback.
/// </para>
/// <para>
/// Only events decorated with <see cref="EventHookAttribute"/> can have hooks.
/// The attribute check is cached for performance.
/// </para>
/// </remarks>
public sealed class HookedDistributedEventBus : IDistributedEventBus
{
    private readonly IDistributedEventBus _inner;
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<HookedDistributedEventBus> _logger;

    /// <summary>
    /// Cache for checking if event types have the EventHookAttribute.
    /// </summary>
    private static readonly ConcurrentDictionary<Type, bool> EventHookAttributeCache = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="HookedDistributedEventBus"/> class.
    /// </summary>
    public HookedDistributedEventBus(
        IDistributedEventBus inner,
        IServiceProvider serviceProvider,
        ILogger<HookedDistributedEventBus> logger)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Publishes an event with outbox enabled (IEventBus implementation).
    /// </summary>
    public Task PublishAsync<TEvent>(
        TEvent payload,
        string? subject = null,
        CancellationToken cancellationToken = default)
        where TEvent : class
    {
        return PublishAsync(payload, subject, useOutbox: true, cancellationToken);
    }

    /// <summary>
    /// Publishes an event with outbox support after executing any registered hooks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// If all hooks succeed, the event is considered handled and won't be published.
    /// If any hook fails, the event will be published to the inner bus as fallback.
    /// </para>
    /// <para>
    /// Events without <see cref="EventHookAttribute"/> or without registered hooks
    /// are always published to the inner bus.
    /// </para>
    /// </remarks>
    public async Task PublishAsync<TEvent>(
        TEvent payload,
        string? subject = null,
        bool useOutbox = true,
        CancellationToken cancellationToken = default)
        where TEvent : class
    {
        if (payload == null)
        {
            throw new ArgumentNullException(nameof(payload));
        }

        // Execute hooks and get result
        var hookResult = await ExecuteHooksAsync(payload, cancellationToken);

        // If all hooks succeeded, event is handled - don't publish to inner bus
        if (hookResult.HooksExecuted && hookResult.AllSucceeded)
        {
            return;
        }

        // If no hooks or any hook failed, publish to inner bus as fallback
        if (hookResult.HasFailures)
        {
            _logger.LogWarning(
                "Hook(s) failed for event {EventType}, publishing to inner bus as fallback",
                payload.GetType().Name);
        }

        await _inner.PublishAsync(payload, subject, useOutbox, cancellationToken);
    }

    /// <summary>
    /// Publishes an event using pre-extracted metadata after executing any registered hooks.
    /// </summary>
    public async Task PublishAsync(
        IDistributedEvent @event,
        EventMetadata metadata,
        string? subject = null,
        bool useOutbox = true,
        CancellationToken cancellationToken = default)
    {
        if (@event == null)
        {
            throw new ArgumentNullException(nameof(@event));
        }

        // Execute hooks and get result
        var hookResult = await ExecuteHooksAsync(@event, cancellationToken);

        // If all hooks succeeded, event is handled - don't publish to inner bus
        if (hookResult is { HooksExecuted: true, AllSucceeded: true })
        {
            return;
        }

        // If no hooks or any hook failed, publish to inner bus as fallback
        if (hookResult.HasFailures)
        {
            _logger.LogWarning(
                "Hook(s) failed for event {EventType}, publishing to inner bus as fallback",
                @event.GetType().Name);
        }

        await _inner.PublishAsync(@event, metadata, subject, useOutbox, cancellationToken);
    }

    /// <summary>
    /// Publishes a pre-serialized CloudEventEnvelope directly to the broker.
    /// </summary>
    /// <remarks>
    /// This method bypasses hook execution as it's used for replaying already-processed events from the outbox.
    /// </remarks>
    public Task PublishEnvelopeAsync(
        byte[] serializedEnvelope,
        string topicName,
        string pubSubName,
        CancellationToken cancellationToken = default)
    {
        // No hooks for envelope publishing - this is used by outbox processor
        return _inner.PublishEnvelopeAsync(serializedEnvelope, topicName, pubSubName, cancellationToken);
    }

    /// <summary>
    /// Result of hook execution.
    /// </summary>
    private readonly record struct HookExecutionResult(
        bool HooksExecuted,
        int TotalHooks,
        int SuccessCount,
        int FailureCount)
    {
        /// <summary>
        /// True if all hooks succeeded.
        /// </summary>
        public bool AllSucceeded => HooksExecuted && FailureCount == 0 && SuccessCount == TotalHooks;

        /// <summary>
        /// True if any hook failed.
        /// </summary>
        public bool HasFailures => FailureCount > 0;

        /// <summary>
        /// Returns a result indicating no hooks were executed.
        /// </summary>
        public static HookExecutionResult NoHooks => new(false, 0, 0, 0);
    }

    /// <summary>
    /// Executes all registered hooks for the given event using the invoker pattern.
    /// </summary>
    /// <returns>Result indicating whether all hooks succeeded.</returns>
    private async Task<HookExecutionResult> ExecuteHooksAsync<TEvent>(TEvent @event, CancellationToken cancellationToken)
        where TEvent : class
    {
        var eventType = @event.GetType();

        // Quick check: does this event type support hooks?
        if (!HasEventHookAttribute(eventType))
        {
            return HookExecutionResult.NoHooks;
        }

        // Get all invokers for this event type.
        // Wrap in try/catch: if the ambient SP is not set (e.g. during startup or background work
        // that bypasses ExecuteInScopeAsync), scope-validation may throw when the bus is a
        // singleton and invokers are scoped. In that case fall back to no-hooks so the event
        // is still published to the inner bus rather than silently lost.
        List<IEventHookInvoker> invokers;
        try
        {
            invokers = GetInvokersForEventType(eventType);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Failed to resolve hook invokers for {EventType} — skipping hooks and publishing to inner bus",
                eventType.Name);
            return HookExecutionResult.NoHooks;
        }

        if (invokers.Count == 0)
        {
            return HookExecutionResult.NoHooks;
        }

        var eventName = eventType.FullName ?? eventType.Name;
        var topic = eventType.Name;

        // Create metadata dictionary for hooks
        var metadata = new Dictionary<string, string>();

        // Create hook context
        var context = new EventHookContext(
            EventData: @event,
            EventType: eventName,
            Topic: topic,
            Metadata: metadata);

        var successCount = 0;
        var failureCount = 0;

        // Execute each hook using the invoker (no reflection!)
        foreach (var invoker in invokers)
        {
            var hookName = invoker.HookName;

            try
            {
                // Direct invocation via invoker - no reflection!
                var result = await invoker.InvokeAsync(@event, context, cancellationToken);

                // Add extra metadata from hook result
                if (result.ExtraMetadata != null)
                {
                    foreach (var kvp in result.ExtraMetadata)
                    {
                        metadata[kvp.Key] = kvp.Value;
                    }
                }

                // Handle hook failures
                if (!result.IsSuccess)
                {
                    failureCount++;
                    _logger.LogWarning(
                        result.Exception,
                        "Hook {HookName} failed for event {EventType}",
                        hookName,
                        eventName);

                    metadata[$"hook_error_{hookName}"] = result.Exception?.GetType().Name ?? "Unknown";
                    metadata[$"hook_error_{hookName}_message"] = result.Exception?.Message ?? "No message";
                }
                else
                {
                    successCount++;
                }
            }
            catch (Exception ex)
            {
                failureCount++;
                _logger.LogError(
                    ex,
                    "Unexpected exception executing hook {HookName} for event {EventType}",
                    hookName,
                    eventName);

                metadata[$"hook_error_{hookName}"] = ex.GetType().Name;
                metadata[$"hook_error_{hookName}_message"] = ex.Message;
            }
        }

        // Try to enrich the event with metadata if it supports it
        TryEnrichEventMetadata(@event, metadata);

        var output = new HookExecutionResult(
            HooksExecuted: true,
            TotalHooks: invokers.Count,
            SuccessCount: successCount,
            FailureCount: failureCount);

        _logger.LogDebug(
            "Hook execution completed for {EventType}: Total={Total}, Success={Success}, Failed={Failed}",
            eventName,
            output.TotalHooks,
            output.SuccessCount,
            output.FailureCount);

        return output;
    }

    /// <summary>
    /// Checks if the event type has the <see cref="EventHookAttribute"/>.
    /// Result is cached for performance.
    /// </summary>
    private static bool HasEventHookAttribute(Type eventType)
    {
        return EventHookAttributeCache.GetOrAdd(
            eventType,
            static type => Attribute.IsDefined(type, typeof(EventHookAttribute)));
    }

    /// <summary>
    /// Gets all hook invokers that can handle the specified event type.
    /// </summary>
    /// <remarks>
    /// Resolves invokers from <see cref="AmbientServiceProvider.Current"/> when available so that
    /// scoped hooks are resolved from the correct request scope even when this bus instance is a
    /// singleton (inheriting the lifetime of the inner <see cref="IDistributedEventBus"/>).
    /// Falls back to the construction-time <see cref="_serviceProvider"/> when no ambient scope is set.
    /// </remarks>
    private List<IEventHookInvoker> GetInvokersForEventType(Type eventType)
    {
        var sp = AmbientServiceProvider.Current ?? _serviceProvider;
        var allInvokers = sp.GetServices<IEventHookInvoker>();
        
        return allInvokers
            .Where(invoker => invoker.EventType == eventType)
            .ToList();
    }

    /// <summary>
    /// Attempts to add hook metadata to the event if the event type supports it.
    /// </summary>
    private void TryEnrichEventMetadata<TEvent>(TEvent @event, IDictionary<string, string> metadata)
        where TEvent : class
    {
        if (metadata.Count == 0)
        {
            return;
        }

        var eventType = @event.GetType();

        // Try to find Extensions property (CloudEvents style)
        var extensionsProperty = eventType.GetProperty("Extensions", BindingFlags.Public | BindingFlags.Instance);
        if (extensionsProperty != null && 
            (extensionsProperty.PropertyType == typeof(IDictionary<string, string>) ||
             extensionsProperty.PropertyType.IsAssignableFrom(typeof(Dictionary<string, string>))))
        {
            var extensions = extensionsProperty.GetValue(@event) as IDictionary<string, string>;
            if (extensions != null)
            {
                foreach (var kvp in metadata)
                {
                    extensions[kvp.Key] = kvp.Value;
                }
                return;
            }
        }

        // Try to find Metadata property
        var metadataProperty = eventType.GetProperty("Metadata", BindingFlags.Public | BindingFlags.Instance);
        if (metadataProperty != null && 
            (metadataProperty.PropertyType == typeof(IDictionary<string, string>) ||
             metadataProperty.PropertyType.IsAssignableFrom(typeof(Dictionary<string, string>))))
        {
            var eventMetadata = metadataProperty.GetValue(@event) as IDictionary<string, string>;
            if (eventMetadata != null)
            {
                foreach (var kvp in metadata)
                {
                    eventMetadata[kvp.Key] = kvp.Value;
                } 
                return;
            }
        }

        // If we can't enrich the event, just log the metadata
        _logger.LogDebug(
            "Unable to enrich event {EventType} with hook metadata. " +
            "Event does not have a suitable Extensions or Metadata property. " +
            "Metadata: {@Metadata}",
            eventType.FullName,
            metadata);
    }
}
