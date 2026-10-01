using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using BBT.Workflow.Execution;
using BBT.Workflow.Execution.Invokers;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Invokers;

/// <summary>
/// The header set an instance-read task presents is ONE rule for both directions: the cross-domain invoker sends the
/// binding's headers + the trusted correlation stamp, and the same-domain read builds the identical set as a dictionary
/// (<see cref="HttpTaskInvocation.BuildOutgoingHeaders"/>) — binding headers minus reserved trace headers and
/// Content-Type, sub / act_sub filled only where the binding did not set them.
/// </summary>
public sealed class TaskReadCredentialHeadersTests
{
    [Fact]
    public void BuildOutgoingHeaders_DropsReservedAndContentType_AndIsCaseInsensitive()
    {
        var headers = HttpTaskInvocation.BuildOutgoingHeaders(new Dictionary<string, string?>
        {
            ["role"] = "morph-idm.maker",
            ["client_id"] = "svc-client",
            ["traceparent"] = "00-x",
            ["X-Correlation-Id"] = "c",
            ["Content-Type"] = "application/json",
        });

        headers["ROLE"].ShouldBe("morph-idm.maker");
        headers["client_id"].ShouldBe("svc-client");
        headers.Keys.ShouldNotContain("traceparent");
        headers.Keys.ShouldNotContain("X-Correlation-Id");
        headers.Keys.ShouldNotContain("Content-Type");
    }

    /// <summary>The caller's whole credential travels — sub, act_sub, position, client_id and role — and nothing else.</summary>
    [Fact]
    public void BuildOutgoingHeaders_CarriesTheCallersFullCredential_AndNothingElseOfTheCaller()
    {
        var headers = HttpTaskInvocation.BuildOutgoingHeaders(null, new Dictionary<string, string>
        {
            ["sub"] = "alice",
            ["act_sub"] = "alice-actor",
            ["position"] = "HQ",
            ["client_id"] = "web-client",
            ["role"] = "morph-idm.maker,other",
            ["x-device-id"] = "device-1",        // not a credential
            ["user_reference"] = "u-1",          // not a credential
        });

        headers["sub"].ShouldBe("alice");
        headers["act_sub"].ShouldBe("alice-actor");
        headers["position"].ShouldBe("HQ");
        headers["client_id"].ShouldBe("web-client");
        headers["role"].ShouldBe("morph-idm.maker,other");
        headers.Keys.ShouldNotContain("x-device-id");
        headers.Keys.ShouldNotContain("user_reference");
    }

    /// <summary>A value the task's mapping sets wins over the caller's, header by header.</summary>
    [Fact]
    public void BuildOutgoingHeaders_TheBindingsValueWins_PerCredentialHeader()
    {
        var headers = HttpTaskInvocation.BuildOutgoingHeaders(
            new Dictionary<string, string?> { ["Role"] = "svc.reader", ["sub"] = "svc" },
            new Dictionary<string, string> { ["role"] = "morph-idm.auditor", ["sub"] = "alice", ["act_sub"] = "alice-actor" });

        headers["role"].ShouldBe("svc.reader");
        headers["sub"].ShouldBe("svc");
        headers["act_sub"].ShouldBe("alice-actor"); // not set by the task: the caller's
    }

    /// <summary>
    /// The caller's role travels only as the caller sent it. A role a provider resolved (morph-idm get-roles) is not a request
    /// header, so a caller that sent none forwards none — the target resolves roles itself from the forwarded credential.
    /// </summary>
    [Fact]
    public void BuildOutgoingHeaders_NoRoleHeaderFromTheCaller_ForwardsNoRole()
    {
        var headers = HttpTaskInvocation.BuildOutgoingHeaders(null,
            new Dictionary<string, string> { ["act_sub"] = "alice-actor", ["client_id"] = "web-client" });

        headers.Keys.ShouldNotContain("role");
        headers["act_sub"].ShouldBe("alice-actor");
        headers["client_id"].ShouldBe("web-client");
    }

    [Fact]
    public void BuildOutgoingHeaders_DropsUnsafeCredentialValues()
    {
        var headers = HttpTaskInvocation.BuildOutgoingHeaders(null, new Dictionary<string, string>
        {
            ["sub"] = "alice smith",          // not a safe identity claim
            ["position"] = "HQ\r\nX-Evil: 1", // header injection
        });

        headers.Keys.ShouldNotContain("sub");
        headers.Keys.ShouldNotContain("position");
    }

    [Fact]
    public void BuildOutgoingHeaders_WithNoBindingAndNoCaller_IsEmpty()
        => HttpTaskInvocation.BuildOutgoingHeaders(null).ShouldBeEmpty();

    /// <summary>The cross-domain Get* request carries the task's headers; the caller's sub fills in only when absent.</summary>
    [Fact]
    public void AddBindingHeaders_ThenTrustedStamp_MatchesTheLocalSet()
    {
        using var activity = new Activity("test").Start();
        activity.SetBaggage("sub", "caller");
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://target/instances/1");

        InvokerHelpers.AddBindingHeaders(request, """{ "role": "morph-idm.maker", "traceparent": "00-x" }""");
        InvokerHelpers.ApplyTrustedCorrelationHeaders(request);

        request.Headers.GetValues("role").Single().ShouldBe("morph-idm.maker");
        request.Headers.GetValues("sub").Single().ShouldBe("caller");
        request.Headers.Contains("traceparent").ShouldBeFalse();

        var local = HttpTaskInvocation.BuildOutgoingHeaders(
            new Dictionary<string, string?> { ["role"] = "morph-idm.maker", ["traceparent"] = "00-x" });
        local["role"].ShouldBe("morph-idm.maker");
        local["sub"].ShouldBe("caller");
    }

    // ── HTTP / DaprService task bindings: credential added, nothing else touched ──

    [Fact]
    public void WithCallerCredential_AddsTheMissingCredential_AndKeepsEveryOtherHeader()
    {
        var json = HttpTaskInvocation.WithCallerCredential(
            """{ "Content-Type": "application/xml", "X-Api-Key": "k", "role": "svc.writer" }""",
            new Dictionary<string, string>
            {
                ["sub"] = "alice", ["act_sub"] = "alice-actor", ["position"] = "HQ", ["client_id"] = "web-client",
                ["role"] = "morph-idm.auditor", ["x-device-id"] = "device-1"
            });

        var headers = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json!)!;
        headers["Content-Type"].ShouldBe("application/xml");   // the binding's own headers are untouched
        headers["X-Api-Key"].ShouldBe("k");
        headers["role"].ShouldBe("svc.writer");                // the mapping's role wins
        headers["sub"].ShouldBe("alice");
        headers["act_sub"].ShouldBe("alice-actor");
        headers["position"].ShouldBe("HQ");
        headers["client_id"].ShouldBe("web-client");
        headers.Keys.ShouldNotContain("x-device-id");          // not a credential
    }

    [Fact]
    public void WithCallerCredential_NothingToAdd_ReturnsTheDefinitionUnchanged()
    {
        const string definition = """{ "sub": "svc" }""";
        HttpTaskInvocation.WithCallerCredential(definition, null).ShouldBeSameAs(definition);
        HttpTaskInvocation.WithCallerCredential("not json", new Dictionary<string, string> { ["sub"] = "alice" })
            .ShouldBe("not json");
    }

    /// <summary>A credential header the mapping left EMPTY counts as not set: the request's value fills it.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyMappingValue_IsFilledFromTheRequest(string empty)
    {
        var headers = HttpTaskInvocation.BuildOutgoingHeaders(
            new Dictionary<string, string?> { ["Role"] = empty, ["position"] = empty },
            new Dictionary<string, string> { ["role"] = "morph-idm.maker", ["position"] = "HQ" });

        headers["role"].ShouldBe("morph-idm.maker");
        headers["position"].ShouldBe("HQ");
        headers.Keys.Count(k => string.Equals(k, "role", System.StringComparison.OrdinalIgnoreCase)).ShouldBe(1);
    }

    [Fact]
    public void AnEmptyMappingValue_WithNothingFromTheRequest_StaysAsTheMappingLeftIt()
    {
        var json = HttpTaskInvocation.WithCallerCredential("""{ "role": "" }""", new Dictionary<string, string>());
        json.ShouldBe("""{ "role": "" }""");
    }
}
