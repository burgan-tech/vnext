using System.Text.Json;
using Microsoft.Extensions.AI;

namespace BBT.Workflow.Agents;

/// <summary>
/// Adapts an <see cref="AgentToolDefinition"/> to an MEAI <see cref="AIFunction"/>. The call id and the
/// round trip come from <see cref="FunctionInvokingChatClient.CurrentContext"/>; the arguments are passed
/// through as a JSON object.
/// </summary>
internal sealed class AgentToolFunction(AgentToolDefinition definition) : AIFunction
{
    private int _fallbackCallId;

    /// <inheritdoc />
    public override string Name => definition.Name;

    /// <inheritdoc />
    public override string Description => definition.Description;

    /// <inheritdoc />
    public override JsonElement JsonSchema => definition.ParametersSchema;

    /// <inheritdoc />
    protected override async ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        var context = FunctionInvokingChatClient.CurrentContext;
        var callId = context?.CallContent.CallId ?? $"call_{Interlocked.Increment(ref _fallbackCallId)}";
        var iteration = context?.Iteration ?? 0;

        var args = JsonSerializer.SerializeToElement(
            arguments.ToDictionary(kv => kv.Key, kv => kv.Value),
            AIJsonUtilities.DefaultOptions);

        var result = await definition.InvokeAsync(new AgentToolInvocation(callId, definition.Name, args, iteration), cancellationToken);
        return result.IsSuccess
            ? result.Content
            : JsonSerializer.Serialize(new { error = result.Error ?? result.Status.ToString(), status = result.Status.ToString() });
    }
}
