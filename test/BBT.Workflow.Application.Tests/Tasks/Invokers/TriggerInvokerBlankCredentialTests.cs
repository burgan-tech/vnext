using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Execution.Invokers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Tasks.Invokers;

/// <summary>
/// A credential the task's header mapping set — blank included — is the mapping's decision: the remote trigger invokers
/// neither refill it nor send it empty. The credential was resolved by the orchestrator, so the Execution host adds no
/// identity from the trace either — not even for an ABSENT sub / act_sub. Every invoker is driven through both of its
/// request builders (Dapr service invocation and plain HTTP).
/// </summary>
public sealed class TriggerInvokerBlankCredentialTests
{
    private const string DaprAppId = "partner-orchestration";
    private const string BaseUrl = "https://partner.local";

    public static TheoryData<string, bool> AllInvokers() => Matrix(Invokers);

    public static TheoryData<string, bool> StampedInvokers() =>
        Matrix(Invokers.Where(i => i != nameof(StartTriggerRemoteInvoker) && i != nameof(SubProcessRemoteInvoker)));

    private static readonly string[] Invokers =
    [
        nameof(StartTriggerRemoteInvoker), nameof(SubProcessRemoteInvoker), nameof(DirectTriggerRemoteInvoker),
        nameof(GetInstanceRemoteInvoker), nameof(GetInstancesRemoteInvoker), nameof(GetInstanceDataRemoteInvoker),
    ];

    private static TheoryData<string, bool> Matrix(IEnumerable<string> invokers)
    {
        var rows = new TheoryData<string, bool>();
        foreach (var invoker in invokers)
        {
            rows.Add(invoker, true);
            rows.Add(invoker, false);
        }

        return rows;
    }

    [Theory]
    [MemberData(nameof(AllInvokers))]
    public async Task BlankCredential_IsNeitherRefilledNorSent(string invoker, bool useDapr)
    {
        var handler = new CapturingHandler();

        using var activity = new Activity("execution-request").Start();
        activity.SetBaggage("sub", "caller");
        await Send(invoker, useDapr, handler, """{ "sub": "", "act_sub": "a1" }""");

        handler.Contains("sub").ShouldBeFalse();          // not refilled, not sent blank
        handler.Values("act_sub").ShouldBe(["a1"]);
    }

    [Theory]
    [MemberData(nameof(StampedInvokers))]
    public async Task AbsentSub_IsNotFilledByTheExecutionHost(string invoker, bool useDapr)
    {
        var handler = new CapturingHandler();

        using var activity = new Activity("execution-request").Start();
        activity.SetBaggage("sub", "caller");
        activity.SetBaggage("act.sub", "caller-actor");
        await Send(invoker, useDapr, handler, """{ "position": "HQ" }""");

        // The orchestrator resolved the credential; the Execution host adds no identity from the trace.
        handler.Contains("sub").ShouldBeFalse();
        handler.Contains("act_sub").ShouldBeFalse();
        handler.Values("position").ShouldBe(["HQ"]);
    }

    private static async Task Send(string invoker, bool useDapr, CapturingHandler handler, string headers)
    {
        var dapr = new DaprServiceInvocationClient(new HttpClient(handler));
        var factory = new FakeHttpClientFactory(handler);
        var config = Config();
        string? appId = useDapr ? DaprAppId : null;
        string? baseUrl = useDapr ? null : BaseUrl;

        switch (invoker)
        {
            case nameof(StartTriggerRemoteInvoker):
                await new StartTriggerRemoteInvoker(dapr, factory, config, NullLogger<StartTriggerRemoteInvoker>.Instance)
                    .InvokeAsync(new TaskDescriptor<StartTriggerBinding>
                    {
                        TaskType = TaskTypes.StartTrigger, TaskKey = "t",
                        Binding = new StartTriggerBinding
                        {
                            Domain = "partner", Workflow = "flow", Headers = headers,
                            UseDapr = useDapr, DaprAppId = appId, BaseUrl = baseUrl
                        }
                    });
                break;
            case nameof(SubProcessRemoteInvoker):
                await new SubProcessRemoteInvoker(dapr, factory, config, NullLogger<SubProcessRemoteInvoker>.Instance)
                    .InvokeAsync(new TaskDescriptor<SubProcessBinding>
                    {
                        TaskType = TaskTypes.SubProcess, TaskKey = "t",
                        Binding = new SubProcessBinding
                        {
                            Domain = "partner", Workflow = "flow", InstanceId = Guid.NewGuid(), Headers = headers,
                            UseDapr = useDapr, DaprAppId = appId, BaseUrl = baseUrl
                        }
                    });
                break;
            case nameof(DirectTriggerRemoteInvoker):
                await new DirectTriggerRemoteInvoker(dapr, factory, config, NullLogger<DirectTriggerRemoteInvoker>.Instance)
                    .InvokeAsync(new TaskDescriptor<DirectTriggerBinding>
                    {
                        TaskType = TaskTypes.DirectTrigger, TaskKey = "t",
                        Binding = new DirectTriggerBinding
                        {
                            Domain = "partner", Workflow = "flow", TransitionName = "go", InstanceId = Guid.NewGuid(),
                            Headers = headers, UseDapr = useDapr, DaprAppId = appId, BaseUrl = baseUrl
                        }
                    });
                break;
            case nameof(GetInstanceRemoteInvoker):
                await new GetInstanceRemoteInvoker(dapr, factory, config, NullLogger<GetInstanceRemoteInvoker>.Instance)
                    .InvokeAsync(new TaskDescriptor<GetInstanceBinding>
                    {
                        TaskType = TaskTypes.GetInstance, TaskKey = "t",
                        Binding = new GetInstanceBinding
                        {
                            Domain = "partner", Workflow = "flow", Instance = Guid.NewGuid().ToString(),
                            Headers = headers, UseDapr = useDapr, DaprAppId = appId, BaseUrl = baseUrl
                        }
                    });
                break;
            case nameof(GetInstancesRemoteInvoker):
                await new GetInstancesRemoteInvoker(dapr, factory, config, NullLogger<GetInstancesRemoteInvoker>.Instance)
                    .InvokeAsync(new TaskDescriptor<GetInstancesBinding>
                    {
                        TaskType = TaskTypes.GetInstances, TaskKey = "t",
                        Binding = new GetInstancesBinding
                        {
                            Domain = "partner", Workflow = "flow",
                            Headers = headers, UseDapr = useDapr, DaprAppId = appId, BaseUrl = baseUrl
                        }
                    });
                break;
            case nameof(GetInstanceDataRemoteInvoker):
                await new GetInstanceDataRemoteInvoker(dapr, factory, config, NullLogger<GetInstanceDataRemoteInvoker>.Instance)
                    .InvokeAsync(new TaskDescriptor<GetInstanceDataBinding>
                    {
                        TaskType = TaskTypes.GetInstanceData, TaskKey = "t",
                        Binding = new GetInstanceDataBinding
                        {
                            Domain = "partner", Workflow = "flow", Instance = Guid.NewGuid().ToString(),
                            Headers = headers, UseDapr = useDapr, DaprAppId = appId, BaseUrl = baseUrl
                        }
                    });
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(invoker), invoker, null);
        }
    }

    private static IConfiguration Config() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [VNextAppIds.ConfigKeys.AppDomain] = "core" })
            .Build();

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private HttpRequestHeaders? _headers;

        public bool Contains(string name) => _headers is not null && _headers.NonValidated.Contains(name);

        public string[] Values(string name) =>
            _headers is not null && _headers.NonValidated.TryGetValues(name, out var values)
                ? values.ToArray()
                : [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _headers = request.Headers;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            });
        }
    }
}
