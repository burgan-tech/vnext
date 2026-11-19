using System;
using System.Linq;
using BBT.Workflow.Execution.RuntimeCaller;
using Shouldly;
using Xunit;
using static BBT.Workflow.Authorization.SubEndpoints.RuntimeCallerTestKeys;

namespace BBT.Workflow.Authorization.SubEndpoints;

/// <summary>
/// The runtime-caller token (vnext#1265): a token proves a runtime call only for the operation and
/// route it was minted for, inside the clock window, under a key the receiver holds.
/// </summary>
public sealed class RuntimeCallerTokenTests
{
    private static readonly RuntimeCallTarget SubStart = RuntimeCallTarget.SubStart("partner", "child-flow");
    private static readonly Guid Parent = Guid.Parse("3a7f6e1c-1d2b-4c3d-9e8f-0a1b2c3d4e5f");
    private static readonly RuntimeCallTarget Complete = RuntimeCallTarget.Complete("core", "parent-flow", Parent);

    private static string Mint(RuntimeCallTarget target, DateTimeOffset? at = null, string keyId = "k1", byte seed = 1)
        => RuntimeCallerToken.Create(keyId, Key(seed), target, "core", RuntimeCallerClasses.SubFlow, at ?? Now);

    private static RuntimeCallerTokenVerdict Check(string? token, RuntimeCallTarget target,
        RuntimeCallerKeyRing? ring = null, DateTimeOffset? at = null, int skew = 300)
        => RuntimeCallerToken.Verify(token, ring ?? DefaultRing(), target, at ?? Now, skew);

    [Fact]
    public void Verify_ShouldAccept_TokenMintedForTheSameTarget()
    {
        var verdict = Check(Mint(Complete), Complete);

        verdict.IsValid.ShouldBeTrue();
        verdict.KeyId.ShouldBe("k1");
        verdict.CallerDomain.ShouldBe("core");
        verdict.CallerClass.ShouldBe(RuntimeCallerClasses.SubFlow);
        verdict.SkewSeconds.ShouldBe(0);
    }

    [Fact]
    public void Verify_ShouldIgnoreCase_OfRouteValues()
    {
        // ASP.NET routing matches case-insensitively and keeps the caller's spelling; the binding must too.
        var verdict = Check(Mint(Complete),
            RuntimeCallTarget.Complete("CORE", "Parent-Flow", Parent.ToString("D").ToUpperInvariant()));

        verdict.IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData("other", "parent-flow", null, RuntimeCallOperations.Complete)]
    [InlineData("core", "other-flow", null, RuntimeCallOperations.Complete)]
    [InlineData("core", "parent-flow", "11111111-1111-1111-1111-111111111111", RuntimeCallOperations.Complete)]
    [InlineData("core", "parent-flow", null, RuntimeCallOperations.SubStart)]
    public void Verify_ShouldRefuse_TokenReplayedOnAnotherRouteOrOperation(
        string domain, string workflow, string? instance, string operation)
    {
        var target = new RuntimeCallTarget(operation, domain, workflow,
            operation == RuntimeCallOperations.Complete ? instance ?? Parent.ToString("D") : null);

        Check(Mint(Complete), target).Status.ShouldBe(RuntimeCallerTokenStatus.BadSignature);
    }

    [Theory]
    [InlineData(-300, RuntimeCallerTokenStatus.Valid)]
    [InlineData(300, RuntimeCallerTokenStatus.Valid)]
    [InlineData(-301, RuntimeCallerTokenStatus.ClockSkew)]
    [InlineData(301, RuntimeCallerTokenStatus.ClockSkew)]
    public void Verify_ShouldAcceptOnlyInsideTheClockWindow(int receiverClockOffsetSeconds, RuntimeCallerTokenStatus expected)
    {
        var verdict = Check(Mint(SubStart), SubStart, at: Now.AddSeconds(receiverClockOffsetSeconds));

        verdict.Status.ShouldBe(expected);
        verdict.SkewSeconds.ShouldBe(receiverClockOffsetSeconds);
    }

    [Fact]
    public void Verify_ShouldReportBadSignature_BeforeClockSkew_ForAForgery()
    {
        // A forged token must never be told apart as "only stale": the MAC is checked first.
        var forged = RuntimeCallerToken.Create("k1", Key(99), SubStart, "core", RuntimeCallerClasses.SubFlow, Now.AddHours(-2));

        Check(forged, SubStart).Status.ShouldBe(RuntimeCallerTokenStatus.BadSignature);
    }

    [Fact]
    public void Verify_ShouldRefuse_UnknownKeyId()
        => Check(Mint(SubStart, keyId: "k9"), SubStart).Status.ShouldBe(RuntimeCallerTokenStatus.UnknownKey);

    [Fact]
    public void Verify_ShouldReportNoClaims_UntilTheSignatureVerifies()
    {
        // Anything in an unverified token is client text: it must not reach logs or span tags.
        var forged = RuntimeCallerToken.Create("k1", Key(99), SubStart, "core\r\nInjected: yes", "subflow", Now);
        var unknown = RuntimeCallerToken.Create("k9", Key(9), SubStart, "core", "subflow", Now);
        var badKeyId = Mint(SubStart).Replace(";k=k1;", ";k=k%0Ax;", StringComparison.Ordinal);

        var forgedVerdict = Check(forged, SubStart);
        var unknownVerdict = Check(unknown, SubStart);

        forgedVerdict.Status.ShouldBe(RuntimeCallerTokenStatus.BadSignature);
        (forgedVerdict.CallerDomain, forgedVerdict.CallerClass, forgedVerdict.SkewSeconds).ShouldBe((null, null, null));
        unknownVerdict.Status.ShouldBe(RuntimeCallerTokenStatus.UnknownKey);
        (unknownVerdict.CallerDomain, unknownVerdict.CallerClass).ShouldBe((null, null));
        unknownVerdict.KeyId.ShouldBe("k9");
        Check(badKeyId, SubStart).KeyId.ShouldBeNull();
    }

    [Fact]
    public void Verify_ShouldAccept_RetiringKeyStillInTheRing_AndRefuseItOnceRemoved()
    {
        var token = Mint(SubStart, keyId: "k1", seed: 1);
        var rotating = Ring(("k2", Key(2)), ("k1", Key(1)));
        var rotated = Ring(("k2", Key(2)));

        Check(token, SubStart, rotating).IsValid.ShouldBeTrue();
        Check(token, SubStart, rotated).Status.ShouldBe(RuntimeCallerTokenStatus.UnknownKey);
    }

    [Fact]
    public void Verify_ShouldRefuse_AlteredCallerDomain()
    {
        var token = Mint(SubStart).Replace(";d=core;", ";d=partner;", StringComparison.Ordinal);

        Check(token, SubStart).Status.ShouldBe(RuntimeCallerTokenStatus.BadSignature);
    }

    [Theory]
    [InlineData(null, RuntimeCallerTokenStatus.Missing)]
    [InlineData("", RuntimeCallerTokenStatus.Missing)]
    [InlineData("   ", RuntimeCallerTokenStatus.Missing)]
    [InlineData("forged", RuntimeCallerTokenStatus.Malformed)]
    [InlineData("v2;k=k1;t=1;d=core;c=subflow;s=AAAA", RuntimeCallerTokenStatus.Malformed)]
    [InlineData("v1;k=k1;t=x;d=core;c=subflow;s=AAAA", RuntimeCallerTokenStatus.Malformed)]
    [InlineData("v1;k=k1;t=1;d=core;c=subflow;s=AAAA", RuntimeCallerTokenStatus.Malformed)]
    [InlineData("v1;k=k1;t=1;d=core;c=subflow;s=!!!!", RuntimeCallerTokenStatus.Malformed)]
    [InlineData("v1;k=k1;t=1;d=core;c=subflow", RuntimeCallerTokenStatus.Malformed)]
    [InlineData("v1;k=;t=1;d=core;c=subflow;s=AAAA", RuntimeCallerTokenStatus.Malformed)]
    public void Verify_ShouldClassify_MissingAndMalformedTokens(string? token, RuntimeCallerTokenStatus expected)
        => Check(token, SubStart).Status.ShouldBe(expected);

    [Fact]
    public void Verify_ShouldRefuse_RepeatedHeaderJoinedByTheStrip()
    {
        // A repeated header reaches the gate joined with ','; it must not verify as either copy.
        var token = Mint(SubStart);

        Check($"{token},{token}", SubStart).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void Verify_ShouldRefuse_OversizedToken()
        => Check(Mint(SubStart) + new string('x', 600), SubStart).Status.ShouldBe(RuntimeCallerTokenStatus.Malformed);

    [Fact]
    public void Create_ShouldEscapeCallerFields_SoTheWireFormStaysParseable()
    {
        var token = RuntimeCallerToken.Create("k1", Key(1), SubStart, "odd;domain=x", "class;y", Now);

        var verdict = Check(token, SubStart);
        verdict.IsValid.ShouldBeTrue();
        verdict.CallerDomain.ShouldBe("odd;domain=x");
        verdict.CallerClass.ShouldBe("class;y");
    }

    [Fact]
    public void Verdict_Reason_ShouldBeStableSnakeCase()
    {
        Enum.GetValues<RuntimeCallerTokenStatus>()
            .Select(status => new RuntimeCallerTokenVerdict(status).Reason)
            .ShouldBe(["missing", "malformed", "unknown_key", "bad_signature", "clock_skew", "valid"]);
    }
}
