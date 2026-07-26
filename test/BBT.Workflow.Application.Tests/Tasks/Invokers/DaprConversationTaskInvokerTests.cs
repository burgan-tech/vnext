using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Invokers;
using Dapr.AI.Conversation;
using Dapr.AI.Conversation.ConversationRoles;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Tasks.Invokers;

public sealed class DaprConversationTaskInvokerTests
{
    [Fact]
    public async Task InvokeAsync_Success_ReturnsResponseText()
    {
        IReadOnlyList<ConversationInput>? capturedInputs = null;
        ConversationOptions? capturedOptions = null;

        var invoker = CreateInvoker((inputs, options) =>
        {
            capturedInputs = inputs;
            capturedOptions = options;
            var choice = new ConversationResultChoice(null, 0, new ResultMessage("Hello from AI"));
            var output = new ConversationResponseResult([choice]);
            return Task.FromResult(new ConversationResponse([output], "ctx-1"));
        });

        var result = await invoker.InvokeAsync(Descriptor(new DaprConversationBinding
        {
            ComponentName = "openai",
            Inputs =
            [
                new ConversationMessageBinding { Role = "system", Content = "You are helpful." },
                new ConversationMessageBinding { Role = "user", Content = "Hi" }
            ]
        }));

        result.IsSuccess.ShouldBeTrue();
        result.Body.ShouldContain("Hello from AI");
        result.Metadata!["ComponentName"].ShouldBe("openai");
        result.Metadata!["ContextId"].ShouldBe("ctx-1");

        // Verify mapping onto the Dapr.AI model.
        capturedOptions!.ConversationComponentId.ShouldBe("openai");
        capturedInputs!.Count.ShouldBe(2);
        capturedInputs[0].Messages[0].Role.ShouldBe(MessageRole.System);
        capturedInputs[1].Messages[0].Role.ShouldBe(MessageRole.User);
    }

    [Fact]
    public async Task InvokeAsync_NoInputs_ReturnsFailure()
    {
        var invoker = CreateInvoker((_, _) =>
            Task.FromResult(new ConversationResponse([], null!)));

        var result = await invoker.InvokeAsync(Descriptor(new DaprConversationBinding
        {
            ComponentName = "openai",
            Inputs = []
        }));

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("at least one input message");
    }

    [Fact]
    public async Task InvokeAsync_ProviderThrows_ReturnsFailure()
    {
        var invoker = CreateInvoker((_, _) =>
            throw new InvalidOperationException("provider unavailable"));

        var result = await invoker.InvokeAsync(Descriptor(new DaprConversationBinding
        {
            ComponentName = "openai",
            Inputs = [new ConversationMessageBinding { Role = "user", Content = "Hi" }]
        }));

        result.IsSuccess.ShouldBeFalse();
        result.ErrorMessage.ShouldContain("provider unavailable");
        result.Metadata!["ComponentName"].ShouldBe("openai");
    }

    private static DaprConversationTaskInvoker CreateInvoker(
        Func<IReadOnlyList<ConversationInput>, ConversationOptions, Task<ConversationResponse>> handler)
    {
        return new DaprConversationTaskInvoker(
            new StubConversationClient(handler),
            NullLogger<DaprConversationTaskInvoker>.Instance);
    }

    private static TaskDescriptor<DaprConversationBinding> Descriptor(DaprConversationBinding binding) =>
        new()
        {
            TaskType = TaskTypes.DaprConversation,
            TaskKey = "conversation-task",
            Binding = binding
        };

    private sealed class StubConversationClient : DaprConversationClient
    {
        private readonly Func<IReadOnlyList<ConversationInput>, ConversationOptions, Task<ConversationResponse>> _handler;

        // The base fields (autogen gRPC client / HttpClient / token) are never used because
        // ConverseAsync is fully overridden, so nulls are safe here.
        public StubConversationClient(
            Func<IReadOnlyList<ConversationInput>, ConversationOptions, Task<ConversationResponse>> handler)
            : base(null!, null!, null!)
        {
            _handler = handler;
        }

        public override Task<ConversationResponse> ConverseAsync(
            IReadOnlyList<ConversationInput> inputs,
            ConversationOptions options,
            CancellationToken cancellationToken = default)
        {
            return _handler(inputs, options);
        }
    }
}
