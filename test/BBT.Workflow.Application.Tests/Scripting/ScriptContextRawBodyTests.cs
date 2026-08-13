using System.Threading.Tasks;
using BBT.Workflow.Caching;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Related;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Scripting;

/// <summary>
/// Verifies the ScriptContext builder auto-populates <see cref="ScriptContext.RawBody"/> from the
/// <see cref="IRequestRawBodyProvider"/>, and that an explicit value overrides the provider.
/// </summary>
public class ScriptContextRawBodyTests
{
    private static ScriptContextFactory CreateFactory(IRequestRawBodyProvider provider) =>
        new(
            Substitute.For<IComponentCacheStore>(),
            NullLogger<ScriptContext>.Instance,
            NullLogger<RelatedInstanceAccessor>.Instance,
            provider);

    [Fact]
    public async Task BuildAsync_PopulatesRawBody_FromProvider()
    {
        var provider = Substitute.For<IRequestRawBodyProvider>();
        provider.GetRawBody().Returns("RAW-ORIGINAL");

        await using var ctx = await CreateFactory(provider)
            .NewBuilder(Substitute.For<IInstanceRepository>())
            .WithRuntime(Substitute.For<IRuntimeInfoProvider>())
            .BuildAsync();

        ctx.RawBody.ShouldBe("RAW-ORIGINAL");
    }

    [Fact]
    public async Task BuildAsync_ExplicitRawBody_OverridesProvider()
    {
        var provider = Substitute.For<IRequestRawBodyProvider>();
        provider.GetRawBody().Returns("FROM-PROVIDER");

        await using var ctx = await CreateFactory(provider)
            .NewBuilder(Substitute.For<IInstanceRepository>())
            .WithRuntime(Substitute.For<IRuntimeInfoProvider>())
            .WithRawBody("EXPLICIT")
            .BuildAsync();

        ctx.RawBody.ShouldBe("EXPLICIT");
    }
}
