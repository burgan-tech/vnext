using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Metrics;
using Dapr.AI.Conversation;
using Dapr.AI.Conversation.ConversationRoles;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Execution.Invokers;

/// <summary>
/// Dapr Conversation (AI/LLM) task invoker - stateless execution with strongly-typed binding.
/// Maps the prepared binding onto the Dapr.AI conversation model and calls the configured provider
/// component through the Dapr sidecar via <see cref="DaprConversationClient"/>.
/// </summary>
public sealed class DaprConversationTaskInvoker(
    DaprConversationClient conversationClient,
    ILogger<DaprConversationTaskInvoker> logger,
    ITaskMetrics? metrics = null)
    : ITaskInvoker<DaprConversationBinding>
{
    private readonly ITaskMetrics _metrics = metrics ?? NullTaskMetrics.Instance;

    /// <inheritdoc />
    public string TaskType => TaskTypes.DaprConversation;

    /// <inheritdoc />
    public System.Type BindingType => typeof(DaprConversationBinding);

    /// <inheritdoc />
    public async Task<TaskInvocationResult> InvokeAsync(
        TaskDescriptor<DaprConversationBinding> descriptor,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync(descriptor.TaskKey, descriptor.Binding, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<TaskInvocationResult> InvokeAsync(
        string? taskKey,
        JsonElement binding,
        CancellationToken cancellationToken = default)
    {
        var typedBinding = binding.Deserialize<DaprConversationBinding>()
            ?? throw new InvalidOperationException("Failed to deserialize DaprConversationBinding");

        return await ExecuteAsync(taskKey, typedBinding, cancellationToken);
    }

    private async Task<TaskInvocationResult> ExecuteAsync(
        string? taskKey,
        DaprConversationBinding binding,
        CancellationToken cancellationToken)
    {
        var startTimestamp = Stopwatch.GetTimestamp();

        if (string.IsNullOrWhiteSpace(binding.ComponentName))
        {
            return TaskInvocationResult.Failure(
                error: "Dapr conversation task requires a 'componentName'",
                executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                taskType: TaskType);
        }

        if (binding.Inputs is null || binding.Inputs.Count == 0)
        {
            return TaskInvocationResult.Failure(
                error: "Dapr conversation task requires at least one input message",
                executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                taskType: TaskType);
        }

        try
        {
            var inputs = binding.Inputs
                .Select(message => new ConversationInput(
                    [BuildMessage(message.Role, message.Content, message.Name)],
                    message.ScrubPII))
                .ToList();

            var options = new ConversationOptions(binding.ComponentName)
            {
                ContextId = binding.ContextId,
                Temperature = binding.Temperature,
                ScrubPII = binding.ScrubPII,
                Metadata = binding.Metadata ?? new Dictionary<string, string>(),
                Parameters = ToAnyParameters(binding.Parameters)
            };

            var response = await conversationClient.ConverseAsync(inputs, options, cancellationToken);


            var payload = new
            {
                contextId = response.ConversationId,
                text = response.Outputs?
                    .FirstOrDefault()?.Choices?
                    .FirstOrDefault()?.Message?.Content,
                outputs = response.Outputs?.Select(output => new
                {
                    model = output.Model,
                    usage = output.Usage is null
                        ? null
                        : new
                        {
                            promptTokens = output.Usage.PromptTokens,
                            completionTokens = output.Usage.CompletionTokens,
                            totalTokens = output.Usage.TotalTokens
                        },
                    choices = output.Choices?.Select(choice => new
                    {
                        index = choice.Index,
                        finishReason = choice.FinishReason?.ToString(),
                        content = choice.Message?.Content
                    })
                })
            };

            var body = JsonSerializer.Serialize(payload);
            var responseData = InvokerHelpers.TryParseJson(body);

            _metrics.RecordDaprConversationInvocation(binding.ComponentName, "success");

            return TaskInvocationResult.Success(
                data: responseData,
                body: body,
                executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                taskType: TaskType,
                metadata: new Dictionary<string, object>
                {
                    ["ComponentName"] = binding.ComponentName,
                    ["ContextId"] = response.ConversationId ?? string.Empty,
                    ["OutputCount"] = response.Outputs?.Count ?? 0
                });
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            _metrics.RecordDaprConversationInvocation(binding.ComponentName, "cancelled");
            logger.LogWarning("Dapr conversation invocation was cancelled: {ComponentName}",
                binding.ComponentName);

            return TaskInvocationResult.Failure(
                error: "Dapr conversation invocation was cancelled",
                executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                taskType: TaskType,
                metadata: new Dictionary<string, object>
                {
                    ["ComponentName"] = binding.ComponentName,
                    ["Cancelled"] = true,
                    ["ExceptionType"] = ex.GetType().Name
                });
        }
        catch (Exception ex)
        {
            _metrics.RecordDaprConversationInvocation(binding.ComponentName, "failure");
            logger.LogError(ex, "Unexpected error during Dapr conversation invocation: {ComponentName}",
                binding.ComponentName);

            return TaskInvocationResult.Failure(
                error: ex.Message,
                executionDurationMs: (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds,
                taskType: TaskType,
                metadata: new Dictionary<string, object>
                {
                    ["ComponentName"] = binding.ComponentName,
                    ["ExceptionType"] = ex.GetType().Name,
                    ["StackTrace"] = ex.StackTrace ?? string.Empty
                });
        }
    }

    /// <summary>
    /// Builds a role-typed conversation message from a plain role string and content.
    /// Unknown roles default to a user message.
    /// </summary>
    private static IConversationMessage BuildMessage(string role, string content, string? name)
    {
        var messageContent = new List<MessageContent> { new(content) };

        var author = name ?? string.Empty;

        return role?.Trim().ToLowerInvariant() switch
        {
            "system" => new SystemMessage { Name = author, Content = messageContent },
            "assistant" => new AssistantMessage { Name = author, Content = messageContent },
            "developer" => new DeveloperMessage { Name = author, Content = messageContent },
            "tool" => new ToolMessage { Name = author, Content = messageContent },
            _ => new UserMessage { Name = author, Content = messageContent }
        };
    }

    /// <summary>
    /// Packs provider parameters into the protobuf <c>Any</c> map expected by the Conversation API,
    /// wrapping each value as a <see cref="Value"/> (numbers and booleans are preserved; everything else
    /// is sent as a string).
    /// </summary>
    private static IReadOnlyDictionary<string, Any>? ToAnyParameters(
        IReadOnlyDictionary<string, string>? parameters)
    {
        if (parameters is null || parameters.Count == 0)
        {
            return null;
        }

        var result = new Dictionary<string, Any>(parameters.Count);
        foreach (var parameter in parameters)
        {
            result[parameter.Key] = Any.Pack(ToProtoValue(parameter.Value));
        }

        return result;
    }

    private static Value ToProtoValue(string raw)
    {
        if (bool.TryParse(raw, out var boolean))
        {
            return Value.ForBool(boolean);
        }

        if (double.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var number))
        {
            return Value.ForNumber(number);
        }

        return Value.ForString(raw);
    }
}
