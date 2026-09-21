using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using BBT.Workflow.Execution.Bindings;
using BBT.Workflow.Tasks.Invocation.Local;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Invocation;

/// <summary>
/// The invocation mode must not be observable through a metadata or header lookup.
/// <para>
/// The two paths cannot produce identical dictionaries and never will: the shared invocation cores
/// stamp PascalCase keys (<c>"ExceptionType"</c>), the local path hands that exact instance on, and
/// the remote path round-trips it through <c>System.Text.Json</c> with
/// <c>JsonSerializerDefaults.Web</c>, which camelCases dictionary keys. The only reachable
/// invariant is therefore about LOOKUP, not storage — and it has to hold on both sides. Fixing only
/// the remote side (as this branch first did) made the runtime's own ordinal
/// <c>TryGetValue("ExceptionType", …)</c> succeed in both modes, which is what restored
/// <c>errorTypes</c> error-boundary matching, but left the mirror-image gap: a consumer reading the
/// camelCase spelling resolved on Remote and missed on Local.
/// </para>
/// <para>
/// <c>RemoteInvokerServiceTests</c> pins the remote half of this. This file pins the local half, so
/// neither seam can be normalized without the other.
/// </para>
/// </summary>
public sealed class InvocationKeyCasingParityTests
{
    [Fact]
    public async Task LocalInvoker_Metadata_IsAddressableInEitherCasing()
    {
        var result = await InvokeAsync();

        result.Metadata.ShouldNotBeNull();
        result.Metadata!.ContainsKey("Url").ShouldBeTrue("the producing core's own PascalCase spelling");
        result.Metadata!.ContainsKey("url").ShouldBeTrue(
            "the camelCase spelling a remotely-routed task would have delivered — the local path " +
            "must not be stricter than the remote one");
    }

    [Fact]
    public async Task LocalInvoker_Headers_IsAddressableInEitherCasing()
    {
        var result = await InvokeAsync();

        result.Headers.ShouldNotBeNull();
        result.Headers!.ContainsKey("X-Trace-Marker").ShouldBeTrue();
        result.Headers!.ContainsKey("x-trace-marker").ShouldBeTrue(
            "already true by construction in the HTTP core — pinned so it stays true");
    }

    /// <summary>
    /// Absent must stay absent. Normalizing how keys compare must never promote a null dictionary
    /// to an empty one — "no metadata" and "empty metadata" are different answers to a consumer.
    /// </summary>
    [Fact]
    public void Normalizing_DoesNotInventAnEmptyDictionary()
    {
        var mapped = LocalInvocationResultMapper.ToOrchestratorResult(
            new BBT.Workflow.Execution.TaskInvocationResult { IsSuccess = true });

        mapped.Metadata.ShouldBeNull();
        mapped.Headers.ShouldBeNull();
    }

    private static async Task<BBT.Workflow.Tasks.TaskInvocationResult> InvokeAsync()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"ok": true}""")
        };
        response.Headers.TryAddWithoutValidation("X-Trace-Marker", "parity");

        var invoker = new LocalHttpTaskInvoker(
            new CapturingHttpClientFactory(new StubHttpMessageHandler(response)),
            NullLogger<LocalHttpTaskInvoker>.Instance);

        return await invoker.InvokeAsync("parity-call", Binding(), traceContext: null);
    }

    private static JsonElement Binding() => JsonSerializer.SerializeToElement(new HttpTaskBinding
    {
        Url = "https://workflow.local/endpoint",
        Method = "GET",
        TimeoutSeconds = 30,
        ValidateSSL = true
    });
}
