using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Events;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Shared;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Events;

/// <summary>
/// Application service that receives external workflow events, resolves the event definition
/// from the workflow cache, compiles and executes the domain-authored mapping script to obtain
/// a correlation key and body, then dispatches to start or transition an instance.
/// Registered as a transient service.
/// </summary>
/// <inheritdoc />
public sealed class EventAppService(
    IComponentCacheStore componentCacheStore,
    IScriptEngine scriptEngine,
    IScriptContextFactory scriptContextFactory,
    IInstanceRepository instanceRepository,
    IInstanceCommandAppService instanceCommandAppService,
    IInstanceSelectorResolver instanceSelectorResolver,
    IRuntimeInfoProvider runtimeInfoProvider,
    ILogger<EventAppService> logger) : IEventAppService
{
    /// <inheritdoc />
    public async Task<Result<object?>> HandleAsync(EventInput input, CancellationToken cancellationToken = default)
    {
        runtimeInfoProvider.Check(input.Domain);
        logger.EventReceived(input.Domain, input.Workflow, input.Action.ToString(), input.TransitionKey);

        // Resolve the flow definition from the component cache (sys-flows).
        var flowResult = await componentCacheStore.GetFlowAsync(input.Domain, input.Workflow, null, cancellationToken);
        if (!flowResult.IsSuccess)
            return Result<object?>.Fail(flowResult.Error);

        var workflow = flowResult.Value!;

        // Resolve the event definition: workflow-level for start, transition-level for transition.
        Transition? transition = null;
        Event? eventDefinition;

        if (input.Action == EventAction.Transition)
        {
            if (string.IsNullOrWhiteSpace(input.TransitionKey))
                return Result<object?>.Fail(Error.Validation(
                    "EventTransitionKeyRequired",
                    "transitionKey is required when action=transition."));

            transition = workflow.FindTransitionInContext(input.TransitionKey);

            // Distinguish "no such transition" from "transition has no event definition" so the
            // caller sees which side to fix (a transitionKey typo vs missing event config).
            if (transition is null)
                return Result<object?>.Fail(Error.NotFound(
                    "TransitionNotFound",
                    $"Transition '{input.TransitionKey}' not found in workflow '{input.Workflow}'."));

            // Single entry point: only a transition declared as an event transition (TriggerType.Event)
            // may be driven by an event.
            if (transition.TriggerType != TriggerType.Event)
                return Result<object?>.Fail(Error.Validation(
                    "NotAnEventTransition",
                    $"Transition '{input.TransitionKey}' is not an event transition and cannot be triggered by an event."));

            eventDefinition = transition.Event;
        }
        else
        {
            eventDefinition = workflow.Event;
        }

        if (eventDefinition?.Mapping is null)
        {
            logger.EventDefinitionMissing(input.Domain, input.Workflow, input.TransitionKey);
            return Result<object?>.Fail(Error.NotFound(
                "EventDefinitionMissing",
                $"No event definition found for workflow '{input.Workflow}'"
                + (input.TransitionKey is null ? "." : $" transition '{input.TransitionKey}'.")));
        }

        // Normalize the delivery envelope before the mapping sees the payload.
        var payload = UnwrapCloudEvent(input.Payload);

        // Compile + run the domain-authored mapping to obtain correlation key + body.
        EventMappingResult? mapping;
        try
        {
            var scriptContext = await scriptContextFactory.NewBuilder(instanceRepository)
                .WithWorkflow(workflow)
                .WithRuntime(runtimeInfoProvider)
                .WithTransition(transition)
                .WithHeaders(input.Headers)
                // Cast to object? so the dynamic from ToDynamic() doesn't turn the whole fluent
                // chain dynamic (which would make BuildAsync fail to resolve at runtime).
                // Undefined/Null payloads (empty request body) map to a null payload rather than
                // the empty string ToDynamic() would produce for them.
                .WithEventPayload(payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                    ? null
                    : (object?)payload.ToDynamic())
                .BuildAsync(cancellationToken);

            var runner = await scriptEngine.CompileToInstanceAsync<IEventMapping>(
                eventDefinition.Mapping,
                flowScripts: workflow.Scripts,
                cancellationToken: cancellationToken);

            mapping = await runner.Handler(scriptContext);
        }
        catch (Exception ex)
        {
            logger.EventMappingFailed(input.Domain, input.Workflow, input.TransitionKey, ex.Message);
            return Result<object?>.Fail(Error.Failure("EventMappingFailed", ex.Message));
        }

        // The mapping is domain-authored script code, so a `return null;` is possible despite the
        // non-nullable contract — fail cleanly instead of NRE-ing on mapping.InstanceKey below.
        if (mapping is null)
        {
            logger.EventMappingFailed(input.Domain, input.Workflow, input.TransitionKey,
                "Event mapping returned null.");
            return Result<object?>.Fail(Error.Failure(
                "EventMappingNullResult",
                "The event mapping script returned a null result."));
        }

        // Transition fallback correlation: when the payload carried no InstanceKey but the mapping
        // supplied a Selector, resolve the target instance by filtering the instance store and use the
        // matched instance's key. (Start creates a new instance, so a selector is not meaningful there.)
        if (input.Action == EventAction.Transition
            && string.IsNullOrWhiteSpace(mapping.InstanceKey)
            && mapping.Selector is not null)
        {
            mapping.InstanceKey = await instanceSelectorResolver.ResolveKeyAsync(
                input.Domain, input.Workflow, mapping.Selector, cancellationToken);
        }

        return input.Action == EventAction.Start
            ? await StartAsync(input, mapping, cancellationToken)
            : await TransitionAsync(input, mapping, cancellationToken);
    }

    private async Task<Result<object?>> StartAsync(
        EventInput input,
        EventMappingResult mapping,
        CancellationToken cancellationToken)
    {
        var startInput = new StartInstanceInput(input.Domain, input.Workflow, version: null, sync: input.Sync)
        {
            Instance = new CreateInstanceInput
            {
                Key = mapping.InstanceKey,
                Attributes = ToAttributes(mapping.Body)
            },
            Headers = input.Headers
        };

        var result = await instanceCommandAppService.StartAsync(startInput, cancellationToken);
        return result.IsSuccess
            ? Result<object?>.Ok(result.Value)
            : Result<object?>.Fail(result.Error);
    }

    private async Task<Result<object?>> TransitionAsync(
        EventInput input,
        EventMappingResult mapping,
        CancellationToken cancellationToken)
    {
        // Correlate to the active instance by business key. No match => log and ignore (success, so the
        // broker does not redeliver).
        var activeInstance = string.IsNullOrWhiteSpace(mapping.InstanceKey)
            ? null
            : await instanceRepository.FindActiveByKeyAsync(mapping.InstanceKey, cancellationToken);

        if (activeInstance is null)
        {
            logger.EventInstanceNotFoundIgnored(input.Domain, input.Workflow, mapping.InstanceKey, input.TransitionKey);
            return Result<object?>.Ok(null);
        }

        var transitionInput = new TransitionInput(input.Domain, input.Workflow,
            new TransitionDataInput(ToAttributes(mapping.Body)), input.Sync)
        {
            Actor = ExecutionActor.System,
            Headers = input.Headers
        };

        var result = await instanceCommandAppService.TransitionAsync(
            activeInstance.Id.ToString(),
            input.TransitionKey!,
            transitionInput,
            cancellationToken);

        return result.IsSuccess
            ? Result<object?>.Ok(result.Value)
            : Result<object?>.Fail(result.Error);
    }

    private static JsonElement? ToAttributes(object? body)
        => body is null ? null : JsonSerializer.SerializeToElement(body, body.GetType());

    /// <summary>
    /// When the event is fed by a Dapr pub/sub subscription, the message arrives as a structured
    /// CloudEvent envelope (<c>{ specversion, id, source, type, data, ... }</c>) rather than the raw
    /// domain payload. Unwrap the inner <c>data</c> so the event mapping sees the same shape it would
    /// for a direct caller. Bodies that are not CloudEvents are passed through unchanged. Lives here —
    /// not in the controller — so every entry point that hands an <see cref="EventInput"/> to this
    /// service gets identical payload normalization.
    /// </summary>
    private static JsonElement UnwrapCloudEvent(JsonElement payload)
        => payload.ValueKind == JsonValueKind.Object
           && payload.TryGetProperty("specversion", out _)
           && payload.TryGetProperty("data", out var data)
            ? data.Clone()
            : payload;
}
