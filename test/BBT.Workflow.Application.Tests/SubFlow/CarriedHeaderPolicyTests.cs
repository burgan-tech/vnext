using System;
using System.Collections.Generic;
using System.Linq;
using BBT.Workflow.SubFlow.Headers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace BBT.Workflow.SubFlow;

public class CarriedHeaderPolicyTests
{
    private static CarriedHeaderPolicy Create(Action<SubFlowCarriedHeadersOptions>? configure = null)
    {
        var o = new SubFlowCarriedHeadersOptions();
        configure?.Invoke(o);
        return new CarriedHeaderPolicy(Options.Create(o), NullLogger<CarriedHeaderPolicy>.Instance);
    }

    private static Dictionary<string, string?> H(params string[] names) =>
        names.ToDictionary(n => n, _ => (string?)"v", StringComparer.Ordinal);

    [Fact]
    public void Defaults_Emit_Expose_And_ApplyToResume_On()
    {
        var policy = Create();

        policy.ExposeToOutputMapping.ShouldBeTrue();
        policy.ApplyToResume.ShouldBeTrue();
    }

    [Fact]
    public void Keeps_Identity_And_Custom_Headers()
    {
        var names = new[] { "sub", "act_sub", "role", "position", "client_id", "accept-language", "x-custom" };
        var r = Create().Capture(H(names))!;
        r.Count.ShouldBe(7);
        r.ContainsKey("role").ShouldBeTrue();
    }

    [Fact]
    public void Drops_Substring_Floor_Matches()
    {
        var r = Create().Capture(H("Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie",
            "x-auth-token", "client_secret", "x-session-locale", "sub"))!;
        r.Keys.ShouldBe(["sub"]);
    }

    [Fact]
    public void Excludes_Framework_Headers()
    {
        var r = Create().Capture(H("X-Subflow-Depth", "X-Parent-Instance-Id", "X-Root-Instance-Id",
            "X-Workflow-Instance-Id", "X-Correlation-Id", "traceparent", "tracestate", "baggage", "Host",
            "Transfer-Encoding", "Connection", "Keep-Alive", "content-type", "content-length",
            "dapr-app-id", "grpc-timeout", "sub"))!;
        r.Keys.ShouldBe(["sub"]);
    }

    [Fact]
    public void Applies_AdditionalDenied_Exactly()
    {
        var r = Create(o => o.AdditionalDenied = ["x-api-key"]).Capture(H("x-api-key", "X-Api-Keys"))!;
        r.Keys.ShouldBe(["x-api-keys"]);
    }

    [Fact]
    public void Drops_Values_Over_MaxValueLength()
    {
        var r = Create().Capture(new Dictionary<string, string?>
        {
            ["long"] = new string('a', 4097),
            ["ok"] = new string('a', 4096)
        })!;
        r.Keys.ShouldBe(["ok"]);
    }

    [Fact]
    public void Caps_Entry_Count_At_MaxEntries()
    {
        var r = Create().Capture(H(Enumerable.Range(0, 70).Select(i => $"x-h{i}").ToArray()))!;
        r.Count.ShouldBe(64);
    }

    [Fact]
    public void Capture_Lowercases_Keys()
    {
        var r = Create().Capture(H("X-Custom"))!;
        r.Keys.ShouldBe(new[] { "x-custom" });
    }

    [Fact]
    public void Capture_Returns_Null_When_Emit_Off_Or_Result_Empty()
    {
        Create(o => o.Emit = false).Capture(H("sub")).ShouldBeNull();
        Create().Capture(H("Authorization")).ShouldBeNull();
        Create().Capture(null).ShouldBeNull();
    }

    [Fact]
    public void Apply_Returns_Empty_Case_Insensitive_For_Null()
    {
        var p = Create();
        var empty = p.Apply(null);
        empty.Count.ShouldBe(0);
        empty.ContainsKey("SUB").ShouldBeFalse();
        p.Apply(new Dictionary<string, string?> { ["Sub"] = "u1" }).ContainsKey("SUB").ShouldBeTrue();
    }

    [Fact]
    public void Apply_Refilters_Forged_Headers()
    {
        var r = Create().Apply(new Dictionary<string, string?>
        {
            ["authorization"] = "x", ["x-subflow-depth"] = "3", ["sub"] = "u1"
        });
        r.Keys.ShouldBe(["sub"]);
    }

    [Fact]
    public void Null_AdditionalDenied_Does_Not_Throw()
    {
        var p = Create(o => o.AdditionalDenied = null!);
        p.Capture(H("sub", "Authorization"))!.Keys.ShouldBe(["sub"]);
        p.Apply(H("sub")).Keys.ShouldBe(["sub"]);
    }
}
