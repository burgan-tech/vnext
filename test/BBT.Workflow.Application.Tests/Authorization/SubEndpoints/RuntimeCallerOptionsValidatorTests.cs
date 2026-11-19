using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using BBT.Workflow.Execution.RuntimeCaller;
using BBT.Workflow.RuntimeCaller;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Xunit;
using static BBT.Workflow.Authorization.SubEndpoints.RuntimeCallerTestKeys;

namespace BBT.Workflow.Authorization.SubEndpoints;

/// <summary>
/// Startup validation of <c>Workflow:RuntimeCaller</c> and the identity's signing (vnext#1265): a host
/// never starts with a ring it cannot load, with key material in configuration, or with a key file its
/// unsandboxed flow scripts could read; and the signer always stamps last, over any same-named header.
/// </summary>
public sealed class RuntimeCallerOptionsValidatorTests : IDisposable
{
    private readonly string _keyFile = Path.Combine(Path.GetTempPath(), $"vnext-1265-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_keyFile))
            File.Delete(_keyFile);
    }

    private static IConfiguration Config(params (string Key, string Value)[] values)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static IReadOnlyList<string> Failures(RuntimeCallerOptions options, IConfiguration? configuration = null,
        bool runsFlowScripts = false)
    {
        var result = new RuntimeCallerOptionsValidator(configuration ?? Config(), runsFlowScripts).Validate(null, options);
        return result.Failed ? result.Failures!.ToList() : [];
    }

    [Fact]
    public void Unset_ShouldPass() => Failures(new RuntimeCallerOptions()).ShouldBeEmpty();

    [Fact]
    public void Required_WithoutKeyFile_ShouldFail()
        => Failures(new RuntimeCallerOptions { Required = true }).ShouldContain(f => f.Contains("no KeyFile"));

    [Fact]
    public void MissingKeyFile_ShouldFail()
        => Failures(new RuntimeCallerOptions { KeyFile = _keyFile }).ShouldContain(f => f.Contains("cannot be read"));

    [Fact]
    public void InvalidRing_ShouldFail_WithoutEchoingTheKey()
    {
        var shortKey = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 });
        File.WriteAllText(_keyFile, $$$"""{"activeKeyId":"k1","keys":{"k1":"{{{shortKey}}}"}}""");

        var failures = Failures(new RuntimeCallerOptions { KeyFile = _keyFile });

        failures.ShouldContain(f => f.Contains("is invalid"));
        failures.ShouldAllBe(f => !f.Contains(shortKey));
    }

    [Fact]
    public void ValidRing_ShouldPass()
    {
        File.WriteAllText(_keyFile, RingJson(("k1", Key(1))));

        Failures(new RuntimeCallerOptions { KeyFile = _keyFile, Required = true }).ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Keys:k1")]
    [InlineData("Key")]
    [InlineData("SigningSecret")]
    public void KeyMaterialInConfiguration_ShouldFail(string key)
        => Failures(new RuntimeCallerOptions(), Config(($"{RuntimeCallerOptions.SectionName}:{key}", "x")))
            .ShouldContain(f => f.Contains("looks like key material"));

    [Theory]
    [InlineData(59)]
    [InlineData(901)]
    public void ClockSkewOutsideRange_ShouldFail(int seconds)
        => Failures(new RuntimeCallerOptions { MaxClockSkewSeconds = seconds })
            .ShouldContain(f => f.Contains("MaxClockSkewSeconds"));

    [Theory]
    [InlineData(true, "false", false, true)]
    [InlineData(true, "false", true, false)]
    [InlineData(true, "true", false, false)]
    [InlineData(false, "false", false, false)]
    public void KeyRing_OnAScriptHostWithoutSandbox_ShouldFailUnlessBypassed(
        bool runsFlowScripts, string sandboxEnabled, bool bypass, bool expectFailure)
    {
        File.WriteAllText(_keyFile, RingJson(("k1", Key(1))));

        var failures = Failures(
            new RuntimeCallerOptions { KeyFile = _keyFile, AllowWithoutScriptSandbox = bypass },
            Config(("Scripting:Sandbox:Enabled", sandboxEnabled)),
            runsFlowScripts);

        failures.Any(f => f.Contains("script sandbox", StringComparison.OrdinalIgnoreCase)
                          || f.Contains("Scripting:Sandbox:Enabled")).ShouldBe(expectFailure);
    }

    [Fact]
    public void Stamp_ShouldReplaceAnySameNamedHeader_WithAFreshToken()
    {
        var identity = Identity(DefaultRing(), domain: "core");
        var target = RuntimeCallTarget.SubStart("partner", "child");
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://partner/api/v1/x");
        request.Headers.TryAddWithoutValidation(RuntimeCallerToken.HeaderName, "smuggled-by-a-mapping");

        identity.Stamp(request, target, RuntimeCallerClasses.SubFlow);

        var token = request.Headers.GetValues(RuntimeCallerToken.HeaderName).Single();
        var verdict = identity.Verify(token, target);
        verdict.IsValid.ShouldBeTrue();
        verdict.CallerDomain.ShouldBe("core");
    }

    [Fact]
    public void Stamp_WithoutKeyRing_ShouldOnlyRemoveTheHeader()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://partner/api/v1/x");
        request.Headers.TryAddWithoutValidation(RuntimeCallerToken.HeaderName, "smuggled-by-a-mapping");

        Identity(null).Stamp(request, RuntimeCallTarget.SubStart("partner", "child"), RuntimeCallerClasses.SubFlow);
        NullRuntimeCallerSigner.Instance.Stamp(request, RuntimeCallTarget.SubStart("partner", "child"), RuntimeCallerClasses.SubFlow);

        request.Headers.Contains(RuntimeCallerToken.HeaderName).ShouldBeFalse();
    }
}
