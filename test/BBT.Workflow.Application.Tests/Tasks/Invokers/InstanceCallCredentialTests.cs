using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using BBT.Workflow.Execution;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Invokers;

/// <summary>
/// The instance-call credential rule: a credential header the mapping SET (blank included) is the mapping's decision;
/// only an ABSENT credential is filled from the caller's request headers. Baggage is never a source.
/// </summary>
public class InstanceCallCredentialTests
{
    private static readonly Dictionary<string, string> Caller = new()
    {
        ["sub"] = "u1", ["act_sub"] = "a1", ["position"] = "p1", ["client_id"] = "c1", ["role"] = "maker"
    };

    [Fact]
    public void AbsentCredential_IsFilledFromTheCaller()
    {
        var result = HttpTaskInvocation.ResolveInstanceCallCredential(
            new Dictionary<string, string?> { ["x-foo"] = "1" }, Caller);

        result["sub"].ShouldBe("u1");
        result["act_sub"].ShouldBe("a1");
        result["position"].ShouldBe("p1");
        result["client_id"].ShouldBe("c1");
        result["role"].ShouldBe("maker");
        result["x-foo"].ShouldBe("1");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void BlankMappingValue_StaysBlank_AndTheCallerIsNotFilledIn(string? blank)
    {
        var result = HttpTaskInvocation.ResolveInstanceCallCredential(
            new Dictionary<string, string?> { ["sub"] = blank }, Caller);

        result["sub"].ShouldBe(string.Empty);
        result["act_sub"].ShouldBe("a1");
    }

    [Fact]
    public void NonBlankMappingValue_Wins()
        => HttpTaskInvocation.ResolveInstanceCallCredential(
            new Dictionary<string, string?> { ["sub"] = "other" }, Caller)["sub"].ShouldBe("other");

    [Fact]
    public void MappingKeyCasing_IsStillExplicit()
    {
        var result = HttpTaskInvocation.ResolveInstanceCallCredential(
            new Dictionary<string, string?> { ["Sub"] = "", ["ACT_SUB"] = "" }, Caller);

        result.Keys.ShouldContain("sub");
        result.Keys.ShouldContain("act_sub");
        result["sub"].ShouldBe(string.Empty);
        result["act_sub"].ShouldBe(string.Empty);
    }

    [Fact]
    public void UnsafeCallerValue_IsNotAdded()
        => HttpTaskInvocation.ResolveInstanceCallCredential(
                null, new Dictionary<string, string> { ["sub"] = "a b\n", ["role"] = "x\r\ny" })
            .ShouldBeEmpty();

    [Fact]
    public void BaggageIsNotASource()
    {
        using var activity = new Activity("t").Start();
        activity.SetBaggage("sub", "u9");
        activity.SetBaggage("act.sub", "a9");

        HttpTaskInvocation.ResolveInstanceCallCredential(null, null).ShouldBeEmpty();
    }

    [Fact]
    public void RemoveBlankCredentials_RemovesOnlyBlankCredentialKeys()
    {
        var headers = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["sub"] = "", ["role"] = "maker", ["x-empty"] = ""
        };

        HttpTaskInvocation.RemoveBlankCredentials(headers);

        headers.ContainsKey("sub").ShouldBeFalse();
        headers["role"].ShouldBe("maker");
        headers.ContainsKey("x-empty").ShouldBeTrue();
    }

    [Fact]
    public void Json_BlankCredentialStaysAsAMarker_AbsentOneIsFilled()
    {
        var json = HttpTaskInvocation.ResolveInstanceCallCredentialJson("{\"sub\":\"\",\"x-foo\":\"1\"}", Caller)!;
        var headers = JsonSerializer.Deserialize<Dictionary<string, string?>>(json)!;

        headers["sub"].ShouldBe(string.Empty);
        headers["act_sub"].ShouldBe("a1");
        headers["x-foo"].ShouldBe("1");
    }

    [Fact]
    public void Json_UnparsableDefinition_IsReturnedUnchanged()
        => HttpTaskInvocation.ResolveInstanceCallCredentialJson("[1,2]", Caller).ShouldBe("[1,2]");

    [Fact]
    public void DropBlankCredentialHeaders_RemovesBlankCredentialsOnly()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://target/");
        request.Headers.TryAddWithoutValidation("sub", "");
        request.Headers.TryAddWithoutValidation("role", "maker");

        HttpTaskInvocation.DropBlankCredentialHeaders(request);

        request.Headers.Contains("sub").ShouldBeFalse();
        request.Headers.GetValues("role").Single().ShouldBe("maker");
    }

    [Fact]
    public void BuildOutgoingHeaders_FollowsTheSameRule()
    {
        var headers = HttpTaskInvocation.BuildOutgoingHeaders(
            new Dictionary<string, string?> { ["Role"] = "", ["sub"] = null, ["traceparent"] = "00-x" }, Caller);

        headers["role"].ShouldBe(string.Empty);
        headers["sub"].ShouldBe(string.Empty);
        headers["act_sub"].ShouldBe("a1");
        headers.ContainsKey("traceparent").ShouldBeFalse();
    }
}
