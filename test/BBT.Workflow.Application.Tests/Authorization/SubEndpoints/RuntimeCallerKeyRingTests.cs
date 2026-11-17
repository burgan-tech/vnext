using System;
using System.Linq;
using BBT.Workflow.Execution.RuntimeCaller;
using Shouldly;
using Xunit;
using static BBT.Workflow.Authorization.SubEndpoints.RuntimeCallerTestKeys;

namespace BBT.Workflow.Authorization.SubEndpoints;

/// <summary>
/// Parsing the runtime-caller key ring (vnext#1265): only a well-formed ring with a usable active key
/// loads, and no failure message ever carries key material.
/// </summary>
public sealed class RuntimeCallerKeyRingTests
{
    private static readonly string SecretBase64 = Convert.ToBase64String(Key(7));

    [Fact]
    public void Parse_ShouldLoad_ActiveKeyAndEveryVerifyingKey()
    {
        var ring = Ring(("k2", Key(2)), ("k1", Key(1)));

        ring.ActiveKeyId.ShouldBe("k2");
        ring.ActiveKey.ShouldBe(Key(2));
        ring.Count.ShouldBe(2);
        ring.TryGetKey("k1", out var older).ShouldBeTrue();
        older.ShouldBe(Key(1));
        ring.TryGetKey("K1", out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"keys":{"k1":"__S__"}}""")]
    [InlineData("""{"activeKeyId":"","keys":{"k1":"__S__"}}""")]
    [InlineData("""{"activeKeyId":"k1"}""")]
    [InlineData("""{"activeKeyId":"k1","keys":{}}""")]
    [InlineData("""{"activeKeyId":"k2","keys":{"k1":"__S__"}}""")]
    [InlineData("""{"activeKeyId":"k1","keys":{"k1":"c2hvcnQ="}}""")]
    [InlineData("""{"activeKeyId":"k1","keys":{"k1":"!!not-base64!!"}}""")]
    [InlineData("""{"activeKeyId":"k 1","keys":{"k 1":"__S__"}}""")]
    [InlineData("""{"activeKeyId":"k1","keys":{"k1":42}}""")]
    public void Parse_ShouldReject_InvalidRing_WithoutEchoingKeyMaterial(string template)
    {
        var json = template.Replace("__S__", SecretBase64, StringComparison.Ordinal);

        var exception = Should.Throw<FormatException>(() => RuntimeCallerKeyRing.Parse(json));

        exception.Message.ShouldNotContain(SecretBase64);
        exception.Message.ShouldNotContain("c2hvcnQ=");
    }

    [Theory]
    [InlineData("k1", true)]
    [InlineData("lab.k-1_2", true)]
    [InlineData("k1\n", false)]
    [InlineData("k1\r\n", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsWellFormedKeyId_ShouldAcceptOnlyTheKeyIdShape(string? keyId, bool expected)
        => RuntimeCallerKeyRing.IsWellFormedKeyId(keyId).ShouldBe(expected);

    [Fact]
    public void Parse_ShouldReject_MoreKeysThanTheCap()
    {
        var keys = Enumerable.Range(0, RuntimeCallerKeyRing.MaximumKeys + 1)
            .Select(i => ($"k{i}", Key((byte)i)))
            .ToArray();

        Should.Throw<FormatException>(() => RuntimeCallerKeyRing.Parse(RingJson(keys)));
    }
}
