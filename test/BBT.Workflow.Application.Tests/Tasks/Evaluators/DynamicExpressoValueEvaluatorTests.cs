using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.DependencyInjection;
using BBT.Aether.Uow;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Rules;
using BBT.Workflow.Tasks.Evaluators;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Evaluators;

public sealed class DynamicExpressoValueEvaluatorTests : IDisposable
{
    private readonly IServiceProvider? _previousAmbientServiceProvider;

    public DynamicExpressoValueEvaluatorTests()
    {
        // Required for the PostSharp SchemaValidation aspect used by Instance.AddData.
        var mockUoW = Substitute.For<IUnitOfWork>();
        var mockUoWManager = Substitute.For<IUnitOfWorkManager>();
        mockUoWManager.BeginAsync(Arg.Any<UnitOfWorkOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(mockUoW));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(mockUoWManager);
        services.AddSingleton(Substitute.For<BBT.Workflow.Caching.IComponentCacheStore>());
        services.AddSingleton(Substitute.For<BBT.Workflow.DefinitionContext.IWorkflowContext>());
        _previousAmbientServiceProvider = AmbientServiceProvider.Current;
        AmbientServiceProvider.Current = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        AmbientServiceProvider.Current = _previousAmbientServiceProvider;
    }

    private static DynamicExpressoValueEvaluator CreateEvaluator() =>
        new(NullLogger<DynamicExpressoValueEvaluator>.Instance);

    [Fact]
    public void Evaluate_LiteralExpression_ReturnsString()
    {
        var script = ScriptCode.FromNative("\"health:status\"", ConditionScriptLocations.DynamicExpresso);
        var context = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance).Build();

        var result = CreateEvaluator().Evaluate(script, context);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("health:status");
    }

    [Fact]
    public void Evaluate_ComputesKeyFromContext()
    {
        var script = ScriptCode.FromNative(
            "\"customer:\" + context.Body[\"customerId\"].ToString() + \":profile\"",
            ConditionScriptLocations.DynamicExpresso);
        var context = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetBody(new Dictionary<string, object> { ["customerId"] = "42" })
            .Build();

        var result = CreateEvaluator().Evaluate(script, context);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("customer:42:profile");
    }

    [Fact]
    public void Evaluate_Sha256Helper_ProducesDeterministicHash()
    {
        var script = ScriptCode.FromNative("\"k:\" + sha256(\"abc\")", ConditionScriptLocations.DynamicExpresso);
        var context = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance).Build();

        var result = CreateEvaluator().Evaluate(script, context);

        result.IsSuccess.ShouldBeTrue();
        // Known SHA-256 of "abc".
        result.Value.ShouldBe("k:ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }

    [Fact]
    public void Evaluate_ExposesInstanceVersion()
    {
        var instance = Instance.Create(Guid.NewGuid(), "flow", "2.0.0", "k");
        var script = ScriptCode.FromNative("\"v:\" + context.Instance.Version", ConditionScriptLocations.DynamicExpresso);
        var context = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetInstance(instance)
            .Build();

        var result = CreateEvaluator().Evaluate(script, context);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("v:2.0.0");
    }

    [Fact]
    public void VaryKey_UsesConfigHeadersAndPrefixes_Canonicalized_WithVersion()
    {
        var script = ScriptCode.FromNative("varyKey(context)", ConditionScriptLocations.DynamicExpresso);
        var context = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetHeaders(new Dictionary<string, string> { ["x-param-b"] = "2", ["x-param-a"] = "1", ["x-other"] = "z", ["x-tenant"] = "acme" })
            .SetQueryParameters(new Dictionary<string, string> { ["version"] = "3" })
            .SetMetadata(new Dictionary<string, object>
            {
                [DynamicExpressoValueEvaluator.VaryByHeadersMetadataKey] = new[] { "x-tenant" },
                [DynamicExpressoValueEvaluator.VaryByPrefixesMetadataKey] = new[] { "x-param-" }
            })
            .Build();

        var result = CreateEvaluator().Evaluate(script, context);

        result.IsSuccess.ShouldBeTrue();
        // sorted names, x-other excluded, version prepended.
        result.Value.ShouldBe("v=3|x-param-a=1|x-param-b=2|x-tenant=acme");
    }

    [Fact]
    public void VaryKey_InstanceVaryBy_OverridesConfig_AndIgnoresOtherHeaders()
    {
        var instance = Instance.Create(Guid.NewGuid(), "flow", "1.0", "k");
        instance.AddData(Guid.NewGuid(), new JsonData("""{ "varyBy": ["x-param-userroles"] }"""));

        var script = ScriptCode.FromNative("varyKey(context)", ConditionScriptLocations.DynamicExpresso);
        var context = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetInstance(instance)
            .SetHeaders(new Dictionary<string, string> { ["x-param-userroles"] = "admin", ["x-param-customertype"] = "gold" })
            .SetMetadata(new Dictionary<string, object>
            {
                [DynamicExpressoValueEvaluator.VaryByPrefixesMetadataKey] = new[] { "x-param-" }
            })
            .Build();

        var result = CreateEvaluator().Evaluate(script, context);

        result.IsSuccess.ShouldBeTrue();
        // instance varyBy wins over config prefixes → only userroles; customertype excluded; default version.
        result.Value.ShouldBe("v=latest|x-param-userroles=admin");
    }

    [Fact]
    public void VaryKey_NoVaryByOrConfig_FallsBackToAllHeaders()
    {
        var script = ScriptCode.FromNative("varyKey(context)", ConditionScriptLocations.DynamicExpresso);
        var context = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetHeaders(new Dictionary<string, string> { ["b"] = "2", ["a"] = "1" })
            .Build();

        var result = CreateEvaluator().Evaluate(script, context);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("v=latest|a=1|b=2");
    }

    [Fact]
    public void Evaluate_WhenNotDynamicExpressoLocation_Fails()
    {
        var script = ScriptCode.FromNative("\"x\"");  // default location "inline"
        var context = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance).Build();

        var result = CreateEvaluator().Evaluate(script, context);

        result.IsSuccess.ShouldBeFalse();
    }

    [Fact]
    public void Evaluate_WhenExpressionInvalid_Fails()
    {
        var script = ScriptCode.FromNative("this is not valid", ConditionScriptLocations.DynamicExpresso);
        var context = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance).Build();

        var result = CreateEvaluator().Evaluate(script, context);

        result.IsSuccess.ShouldBeFalse();
    }
}
