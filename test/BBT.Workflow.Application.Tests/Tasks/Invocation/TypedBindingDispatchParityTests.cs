using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks;
using BBT.Workflow.Tasks.Executors;
using BBT.Workflow.Tasks.Invocation;
using BBT.Workflow.Tasks.Invocation.Local;
using BBT.Workflow.Tasks.Mapping;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;
using TaskEnvelope = BBT.Workflow.Tasks.TaskEnvelope;
using TaskInvocationResult = BBT.Workflow.Tasks.TaskInvocationResult;
using TaskTraceContext = BBT.Workflow.Tasks.TaskTraceContext;

namespace BBT.Workflow.Application.Tests.Tasks.Invocation;

/// <summary>
/// The local path now hands the mapped binding object to the invoker instead of serializing it to a
/// <see cref="JsonElement"/>, re-parsing it for the caller credential and deserializing it again. These
/// tests pin that the typed route produces exactly the HTTP request the JSON route produced (method, URL,
/// headers, content type, body, credentials), and that the Remote envelope is unchanged.
/// </summary>
public sealed class TypedBindingDispatchParityTests
{
    private static readonly Dictionary<string, string> Caller = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sub"] = "alice",
        ["act_sub"] = "alice-actor",
        ["position"] = "HQ",
        ["client_id"] = "web-client",
        ["role"] = "morph-idm.maker"
    };

    public static TheoryData<string, bool> HttpTaskConfigs => new()
    {
        // GET, no body, a plain header; with and without a caller credential.
        { """{ "url": "https://api.local/items?q=ü&x=<1>", "method": "GET", "headers": { "User-Agent": "vNext/1.0" }, "timeoutSeconds": 12 }""", true },
        { """{ "url": "https://api.local/items", "method": "GET", "headers": { "User-Agent": "vNext/1.0" } }""", false },
        // POST JSON body; mapping already sets one credential header (mapping wins).
        { """{ "url": "https://api.local/orders", "method": "POST", "headers": { "role": "svc.writer", "X-Trace": "a+b" }, "body": { "id": 42, "name": "çağrı \"q\"", "tags": ["a","b"] } }""", true },
        // Form body with Content-Type header.
        { """{ "url": "https://api.local/form", "method": "POST", "headers": { "Content-Type": "application/x-www-form-urlencoded" }, "body": "a=1&b=2" }""", true },
        // Explicit contentType, no headers at all, SSL validation off, accepted codes.
        { """{ "url": "https://api.local/raw", "method": "PUT", "contentType": "text/plain", "body": "hello", "validateSSL": false, "acceptedStatusCodes": ["404", "5xx"] }""", true },
        // Every credential header already set by the mapping: nothing to add.
        { """{ "url": "https://api.local/all", "method": "DELETE", "headers": { "sub": "m", "act_sub": "m2", "position": "p", "client_id": "c", "role": "r" } }""", true },
    };

    [Theory]
    [MemberData(nameof(HttpTaskConfigs))]
    public async Task LocalHttp_TypedRoute_SendsTheSameRequestAsTheJsonRoute(string config, bool withCaller)
    {
        var task = HttpTask.Create(config.ToJsonElement());
        task.SetReference(new Reference("parity", "test", "sys-tasks", "1.0.0"));
        var caller = withCaller ? Caller : null;

        // Before: CreateEnvelope → JsonNode credential rewrite → JsonElement → Deserialize<HttpTaskBinding>.
        var legacyRequest = await CaptureHttpAsync(invoker =>
            invoker.InvokeAsync("parity", LegacyEnvelope(task, caller).Binding, traceContext: null));

        // After: mapped object → typed credential → typed invoker.
        var typedBinding = TaskBindingCredentials.WithCallerCredential(
            (HttpTaskBinding)TaskBindingMapper.CreateBinding(task).Value.Binding, caller);
        var typedRequest = await CaptureHttpAsync(invoker =>
            ((ITypedLocalTaskInvoker<HttpTaskBinding>)invoker).InvokeAsync("parity", typedBinding, traceContext: null));

        typedRequest.ShouldBe(legacyRequest);
    }

    [Theory]
    [MemberData(nameof(HttpTaskConfigs))]
    public async Task RemoteHttp_TypedDispatch_SendsTheSameEnvelopeAsTheJsonRoute(string config, bool withCaller)
    {
        var task = HttpTask.Create(config.ToJsonElement());
        task.SetReference(new Reference("parity", "test", "sys-tasks", "1.0.0"));
        var caller = withCaller ? Caller : null;

        var remote = Substitute.For<IRemoteInvokerService>();
        TaskEnvelope? sent = null;
        remote.InvokeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TaskEnvelope>(),
                Arg.Any<TaskTraceContext>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                sent = call.ArgAt<TaskEnvelope>(2);
                return Result<TaskInvocationResult>.Ok(TaskInvocationResult.Success());
            });
        var dispatcher = Dispatcher(ExecutionMode.Remote, remote, localInvoker: null);

        var typedBinding = TaskBindingCredentials.WithCallerCredential(
            (HttpTaskBinding)TaskBindingMapper.CreateBinding(task).Value.Binding, caller);
        await dispatcher.DispatchAsync(task, TaskTypes.Http, typedBinding, new TaskTraceContext());

        var legacy = LegacyEnvelope(task, caller);
        sent.ShouldNotBeNull();
        sent.TaskType.ShouldBe(legacy.TaskType);
        sent.TaskKey.ShouldBe(legacy.TaskKey);
        sent.Version.ShouldBe(legacy.Version);
        // Field-for-field what the Execution host deserializes.
        Describe(sent.Binding.Deserialize<HttpTaskBinding>()!)
            .ShouldBe(Describe(legacy.Binding.Deserialize<HttpTaskBinding>()!));
    }

    [Fact]
    public async Task LocalDispatch_UsesTheTypedOverload_AndNeverSerializesTheBinding()
    {
        var typed = Substitute.For<ITypedLocalTaskInvoker<HttpTaskBinding>>();
        typed.TaskType.Returns(TaskTypes.Http);
        typed.InvokeAsync(Arg.Any<string?>(), Arg.Any<HttpTaskBinding>(), Arg.Any<TaskTraceContext?>(), Arg.Any<CancellationToken>())
            .Returns(TaskInvocationResult.Success());
        var remote = Substitute.For<IRemoteInvokerService>();
        var dispatcher = Dispatcher(ExecutionMode.Local, remote, typed);
        var binding = new HttpTaskBinding { Url = "https://x.local" };

        var result = await dispatcher.DispatchAsync(
            WorkflowTaskFactory.CreateHttpTask("probe"), TaskTypes.Http, binding, new TaskTraceContext());

        result.IsSuccess.ShouldBeTrue();
        await typed.Received(1).InvokeAsync("probe", binding, Arg.Any<TaskTraceContext?>(), Arg.Any<CancellationToken>());
        await typed.DidNotReceive().InvokeAsync(Arg.Any<string?>(), Arg.Any<JsonElement>(), Arg.Any<TaskTraceContext?>(), Arg.Any<CancellationToken>());
        await remote.DidNotReceiveWithAnyArgs().InvokeAsync(default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task LocalDispatch_UntypedInvoker_GetsTheSameElementTheEnvelopeWouldCarry()
    {
        var untyped = Substitute.For<ILocalTaskInvoker>();
        untyped.TaskType.Returns(TaskTypes.Http);
        JsonElement received = default;
        untyped.InvokeAsync(Arg.Any<string?>(), Arg.Any<JsonElement>(), Arg.Any<TaskTraceContext?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                received = call.ArgAt<JsonElement>(1).Clone();
                return TaskInvocationResult.Success();
            });
        var dispatcher = Dispatcher(ExecutionMode.Local, Substitute.For<IRemoteInvokerService>(), untyped);
        var binding = new HttpTaskBinding { Url = "https://x.local", Method = "POST", Body = "{}" };

        await dispatcher.DispatchAsync(
            WorkflowTaskFactory.CreateHttpTask("probe"), TaskTypes.Http, binding, new TaskTraceContext());

        received.GetRawText().ShouldBe(JsonSerializer.SerializeToElement(binding, binding.GetType()).GetRawText());
    }

    [Fact]
    public async Task LocalSoap_TypedRoute_SendsTheSameRequestAsTheJsonRoute()
    {
        var task = WorkflowTaskFactory.CreateSoapTask("soap-parity");
        var envelope = TaskBindingMapper.CreateEnvelope(task).Value!;
        var legacy = WithLegacyCredential(envelope, Caller);
        var typedBinding = TaskBindingCredentials.WithCallerCredential(
            (SoapTaskBinding)TaskBindingMapper.CreateBinding(task).Value.Binding, Caller);

        var legacyRequest = await CaptureAsync(handler =>
            new LocalSoapTaskInvoker(new CapturingHttpClientFactory(handler), NullLogger<LocalSoapTaskInvoker>.Instance)
                .InvokeAsync("soap-parity", legacy.Binding, traceContext: null));
        var typedRequest = await CaptureAsync(handler =>
            new LocalSoapTaskInvoker(new CapturingHttpClientFactory(handler), NullLogger<LocalSoapTaskInvoker>.Instance)
                .InvokeAsync("soap-parity", typedBinding, traceContext: null));

        typedRequest.ShouldBe(legacyRequest);
    }

    [Fact]
    public void DaprService_TypedCredential_MatchesTheEnvelopeRewrite()
    {
        var task = DaprServiceTask.Create("""
            { "appId": "partner-api", "methodName": "/api/v1/things", "httpVerb": "POST",
              "queryString": "a=1", "headers": { "role": "svc.reader" }, "body": { "x": 1 } }
            """.ToJsonElement());
        task.SetReference(new Reference("call-partner", "test-domain", "sys-tasks", "1.0.0"));

        var legacy = WithLegacyCredential(TaskBindingMapper.CreateEnvelope(task).Value!, Caller)
            .Binding.Deserialize<DaprServiceBinding>()!;
        var typed = TaskBindingCredentials.WithCallerCredential(
            (DaprServiceBinding)TaskBindingMapper.CreateBinding(task).Value.Binding, Caller);

        Describe(typed).ShouldBe(Describe(legacy));
    }

    [Fact]
    public void TypedCredential_NothingToAdd_ReturnsTheSameInstance()
    {
        var http = new HttpTaskBinding { Url = "https://x.local", Headers = """{"a":"b"}""" };
        TaskBindingCredentials.WithCallerCredential(http, callerHeaders: null).ShouldBeSameAs(http);

        var soap = new SoapTaskBinding { Url = "https://x.local" };
        TaskBindingCredentials.WithCallerCredential(soap, callerHeaders: null).ShouldBeSameAs(soap);

        var dapr = new DaprServiceBinding { AppId = "a", MethodName = "m" };
        TaskBindingCredentials.WithCallerCredential(dapr, callerHeaders: null).ShouldBeSameAs(dapr);
    }

    /// <summary>
    /// Guards the member-by-member copies in <see cref="TaskBindingCredentials"/>: a member added to a
    /// binding without being copied would silently drop it on the credential path.
    /// </summary>
    [Fact]
    public void TypedCredential_Copy_KeepsEveryMemberButHeaders()
    {
        AssertCopiesEveryMember(
            Populate(new HttpTaskBinding { Url = "https://x.local" }),
            b => TaskBindingCredentials.WithCallerCredential(b, Caller));
        AssertCopiesEveryMember(
            Populate(new DaprServiceBinding { AppId = "a", MethodName = "m" }),
            b => TaskBindingCredentials.WithCallerCredential(b, Caller));
        AssertCopiesEveryMember(
            Populate(new SoapTaskBinding { Url = "https://x.local" }),
            b => TaskBindingCredentials.WithCallerCredential(b, Caller));
    }

    [Fact]
    public void EnvelopeCredential_NothingToAdd_ReturnsTheSameEnvelope_AndAddsLikeBefore()
    {
        var envelope = TaskBindingMapper.CreateEnvelope(WorkflowTaskFactory.CreateHttpTask("probe")).Value!;

        CredentialProbe.Apply(envelope, scriptContext: null).ShouldBeSameAs(envelope);

        var scriptContext = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetRuntime(Substitute.For<BBT.Workflow.Runtime.IRuntimeInfoProvider>())
            .SetHeaders(Caller)
            .Build();
        var rewritten = CredentialProbe.Apply(envelope, scriptContext);

        rewritten.ShouldNotBeSameAs(envelope);
        Describe(rewritten.Binding.Deserialize<HttpTaskBinding>()!)
            .ShouldBe(Describe(WithLegacyCredential(envelope, scriptContext.GetHeadersAsDictionary()).Binding.Deserialize<HttpTaskBinding>()!));
    }

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>The pre-change credential rewrite, verbatim: parse the whole binding, merge, reserialize.</summary>
    private static TaskEnvelope WithLegacyCredential(TaskEnvelope envelope, IReadOnlyDictionary<string, string>? caller)
    {
        var binding = System.Text.Json.Nodes.JsonNode.Parse(envelope.Binding.GetRawText())!.AsObject();
        var current = binding["Headers"]?.GetValue<string?>();
        var merged = HttpTaskInvocation.WithCallerCredential(current, caller);
        if (merged == current)
            return envelope;

        binding["Headers"] = merged;
        return new TaskEnvelope
        {
            TaskType = envelope.TaskType,
            Version = envelope.Version,
            TaskKey = envelope.TaskKey,
            Binding = JsonSerializer.SerializeToElement(binding)
        };
    }

    private static TaskEnvelope LegacyEnvelope(WorkflowTask task, IReadOnlyDictionary<string, string>? caller) =>
        WithLegacyCredential(TaskBindingMapper.CreateEnvelope(task).Value!, caller);

    private static Task<string> CaptureHttpAsync(Func<LocalHttpTaskInvoker, Task<TaskInvocationResult>> send) =>
        CaptureAsync(handler => send(new LocalHttpTaskInvoker(
            new CapturingHttpClientFactory(handler), NullLogger<LocalHttpTaskInvoker>.Instance)));

    /// <summary>Sends through a capturing handler and renders the outgoing request as comparable text.</summary>
    private static async Task<string> CaptureAsync(Func<HttpMessageHandler, Task<TaskInvocationResult>> send)
    {
        string? rendered = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            var headers = request.Headers
                .Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                .Select(h => $"{h.Key.ToLowerInvariant()}={string.Join(",", h.Value)}")
                .OrderBy(h => h, StringComparer.Ordinal);
            var body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            rendered = $"{request.Method} {request.RequestUri}\n{string.Join("\n", headers)}\n\n{body}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        });

        var result = await send(handler);
        result.IsSuccess.ShouldBeTrue(result.ErrorMessage);
        rendered.ShouldNotBeNull();
        return rendered;
    }

    private static TaskInvocationDispatcher Dispatcher(
        ExecutionMode mode, IRemoteInvokerService remote, ILocalTaskInvoker? localInvoker)
    {
        var router = Substitute.For<ITaskInvocationRouter>();
        router.Resolve(Arg.Any<WorkflowTask?>(), Arg.Any<string>()).Returns(new TaskInvocationDecision(mode, "test"));
        var registry = Substitute.For<ILocalTaskInvokerRegistry>();
        registry.Get(Arg.Any<string>()).Returns(localInvoker);
        registry.Has(Arg.Any<string>()).Returns(localInvoker is not null);
        return new TaskInvocationDispatcher(
            router, registry, remote, Options.Create(new TaskInvocationOptions()),
            NullLogger<TaskInvocationDispatcher>.Instance);
    }

    private static string Describe(object binding) =>
        string.Join("\n", binding.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .Select(p => $"{p.Name}={Render(p.GetValue(binding))}"));

    private static string Render(object? value) => value switch
    {
        null => "<null>",
        string s => s,
        System.Collections.IEnumerable e => "[" + string.Join(",", e.Cast<object?>().Select(Render)) + "]",
        _ => value.ToString() ?? ""
    };

    private static T Populate<T>(T binding) where T : class
    {
        foreach (var property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.CanWrite && p.Name != "Headers" && p.Name != "EqualityContract"))
        {
            object? value = property.PropertyType switch
            {
                var t when t == typeof(string) => $"{property.Name}-value",
                var t when t == typeof(int) => 77,
                var t when t == typeof(bool) => !(bool)property.GetValue(binding)!,
                var t when t == typeof(IReadOnlyList<string>) => new List<string> { "418" },
                _ => throw new InvalidOperationException(
                    $"Populate does not know how to fill {typeof(T).Name}.{property.Name} ({property.PropertyType}); extend it.")
            };
            property.SetValue(binding, value);
        }

        return binding;
    }

    private static void AssertCopiesEveryMember<T>(T original, Func<T, T> apply) where T : class
    {
        var copy = apply(original);
        copy.ShouldNotBeSameAs(original); // the caller credential forces a Headers change
        foreach (var property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.Name != "Headers" && p.Name != "EqualityContract"))
        {
            property.GetValue(copy).ShouldBe(property.GetValue(original), $"{typeof(T).Name}.{property.Name} not copied");
        }
    }

    /// <summary>Exposes the protected envelope credential helper of <see cref="TaskExecutorBase{TTask}"/>.</summary>
    private sealed class CredentialProbe() : TaskExecutorBase<HttpTask>(NullLogger.Instance)
    {
        public override TaskType TaskType => TaskType.Http;

        public static TaskEnvelope Apply(TaskEnvelope envelope, ScriptContext? scriptContext) =>
            WithCallerCredential(envelope, scriptContext);

        protected override Task<Result<TaskInvocationResult>> InvokeAsync(
            HttpTask task, TaskExecutorContext context, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
