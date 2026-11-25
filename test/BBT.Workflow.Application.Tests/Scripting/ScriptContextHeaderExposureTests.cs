using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using BBT.Workflow.Caching;
using BBT.Workflow.Headers;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Related;
using BBT.Workflow.SubFlow.Headers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Scripting;

/// <summary>
/// <c>Workflow:HeaderExposure:Deny</c> in script contexts: scripts and mappings (<see cref="ScriptContext.Headers"/>) never
/// see a listed header, while the runtime's own reads — the instance-call credential it forwards, the identity it
/// propagates — get the request as it arrived. An empty list leaves the context exactly as before.
/// </summary>
public class ScriptContextHeaderExposureTests
{
    private static ScriptContextFactory Factory(params string[] deny) =>
        new(
            Substitute.For<IComponentCacheStore>(),
            NullLogger<ScriptContext>.Instance,
            NullLogger<RelatedInstanceAccessor>.Instance,
            headerExposureDenyList: new HeaderExposureDenyList(Options.Create(new HeaderExposureOptions { Deny = [.. deny] })));

    private static Dictionary<string, string?> RequestHeaders() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Authorization"] = "Bearer t",
        ["sub"] = "u-1",
        ["x-request-id"] = "abc",
    };

    private static async Task<ScriptContext> BuildAsync(ScriptContextFactory factory, Dictionary<string, string?> headers) =>
        await factory
            .NewBuilder(Substitute.For<IInstanceRepository>())
            .WithRuntime(Substitute.For<IRuntimeInfoProvider>())
            .WithHeaders(headers)
            .BuildAsync();

    [Fact]
    public async Task ADeniedHeader_IsHiddenFromScripts_AndKeptForTheRuntime()
    {
        await using var context = await BuildAsync(Factory("authorization", "sub"), RequestHeaders());

        var scriptView = (Dictionary<string, string?>)context.Headers!;
        scriptView.ContainsKey("Authorization").ShouldBeFalse();
        scriptView.ContainsKey("sub").ShouldBeFalse();
        scriptView.ContainsKey("X-REQUEST-ID").ShouldBeTrue();
        context.GetHeadersAsDictionary().ContainsKey("Authorization").ShouldBeFalse();

        var transport = context.GetTransportHeadersAsDictionary();
        transport["Authorization"].ShouldBe("Bearer t");
        transport["sub"].ShouldBe("u-1");
    }

    [Fact]
    public async Task WithAnEmptyList_ScriptsSeeTheVeryDictionaryThatWasPassedIn()
    {
        var headers = RequestHeaders();

        await using var context = await BuildAsync(Factory(), headers);

        ((object?)context.Headers).ShouldBeSameAs(headers);
        context.GetTransportHeadersAsDictionary().ShouldBeSameAs(headers);
    }

    [Fact]
    public async Task AParallelBranch_KeepsTheSameSplit()
    {
        await using var context = await BuildAsync(Factory("authorization"), RequestHeaders());

        var branch = context.CreateParallelBranch();

        ((Dictionary<string, string?>)branch.Headers!).ContainsKey("Authorization").ShouldBeFalse();
        branch.GetTransportHeadersAsDictionary()["Authorization"].ShouldBe("Bearer t");
    }

    [Fact]
    public void TheSubFlowCarriedHeaders_DropADeniedName()
    {
        var policy = new CarriedHeaderPolicy(
            Options.Create(new SubFlowCarriedHeadersOptions()),
            Substitute.For<ILogger<CarriedHeaderPolicy>>(),
            new HeaderExposureDenyList(Options.Create(new HeaderExposureOptions { Deny = ["x-customer-no"] })));

        var carried = policy.Capture(new Dictionary<string, string?> { ["X-Customer-No"] = "123", ["x-channel"] = "web" });

        carried!.ContainsKey("x-customer-no").ShouldBeFalse();
        carried["x-channel"].ShouldBe("web");
    }
}
