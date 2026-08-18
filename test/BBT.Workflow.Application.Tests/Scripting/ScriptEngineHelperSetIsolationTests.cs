using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Monitoring;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting.Evaluators;
using BBT.Workflow.Scripting.Functions;
using BBT.Workflow.Scripting.Helpers;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Scripting;

/// <summary>
/// Public test contract for mappings that return a helper-provided string, used to distinguish which
/// of two same-shaped helper sets a compiled mapping actually resolved against.
/// </summary>
public interface IHelperValueMapping
{
    string GetValue();
}

/// <summary>
/// End-to-end regression coverage for the helper-set/load-context cache-scope fix. Unlike
/// <see cref="SandboxedScriptingTests"/>, this drives the real production wiring (<see cref="ScriptEngine"/>
/// -&gt; <see cref="IScriptHelperRegistry"/> -&gt; <see cref="IEvaluator"/>) through a DI-built
/// <see cref="ServiceProvider"/> with a mocked <see cref="IComponentCacheStore"/>, because the bug this
/// guards lives specifically in that wiring rather than in the evaluator's cache key alone.
/// </summary>
[Collection("ScriptingTests")]
public class ScriptEngineHelperSetIsolationTests
{
    private static MetadataReference ContractRef =>
        MetadataReference.CreateFromFile(typeof(IHelperValueMapping).Assembly.Location);

    /// <summary>
    /// Builds a mapping component whose Key/Domain/Version are set via <see cref="IReferenceSetter"/>,
    /// mirroring how <see cref="IComponentCacheStore.GetMappingAsync"/> results are shaped in production.
    /// </summary>
    private static Mapping BuildHelperMapping(string key, string domain, string version, string code)
    {
        var mapping = new Mapping(name: key, code: code, encoding: CodeEncoding.Native);
        mapping.SetReference(new Reference(key, domain, RuntimeSysSchemaInfo.Mappings, version));
        return mapping;
    }

    [Fact]
    public async Task ScriptEngine_Compiles_Same_Mapping_Against_Different_Helper_Sets_Without_Cross_Contamination()
    {
        // Regression for the load-context cache-scope fix. Two helper sets export the same namespace and
        // type name but return different values. The mapping source compiled against each is
        // byte-identical.
        //
        // Before the fix, the evaluator had no way to tell the two helper sets' load contexts apart (see
        // CSharpEvaluator.GetCacheScope for the root cause). The second ScriptEngine.CompileToInstanceAsync
        // call below hit the cache entry the first call created and returned the FIRST helper set's
        // compiled type verbatim — GetValue() on the second instance would answer "A" instead of "B", with
        // no exception. This test drives the real wiring (ScriptEngine -> IScriptHelperRegistry ->
        // IEvaluator), not the evaluator's cache key directly.
        var evaluator = new CSharpEvaluator();
        var helperRegistry = new ScriptHelperRegistry(evaluator);

        var mappingA = BuildHelperMapping("shared-helper", "domain-a", "1.0.0",
            "namespace SharedHelpers { public static class Provider { public static string Value() => \"A\"; } }");
        var mappingB = BuildHelperMapping("shared-helper", "domain-b", "1.0.0",
            "namespace SharedHelpers { public static class Provider { public static string Value() => \"B\"; } }");

        var componentStore = new Mock<IComponentCacheStore>();
        componentStore
            .Setup(x => x.GetMappingAsync("domain-a", "shared-helper", "1.0.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Mapping>.Ok(mappingA));
        componentStore
            .Setup(x => x.GetMappingAsync("domain-b", "shared-helper", "1.0.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Mapping>.Ok(mappingB));

        var currentSchema = new Mock<ICurrentSchema>();
        currentSchema.Setup(x => x.Change(It.IsAny<string>())).Returns(Mock.Of<IDisposable>());

        var services = new ServiceCollection();
        services.AddSingleton(componentStore.Object);
        services.AddSingleton(currentSchema.Object);
        using var serviceProvider = services.BuildServiceProvider();

        var engine = new ScriptEngine(
            evaluator,
            Mock.Of<IScriptServices>(),
            Mock.Of<IWorkflowMetrics>(),
            helperRegistry,
            new ScriptHelpersOptions { Enabled = true },
            serviceProvider,
            Mock.Of<ILogger<ScriptEngine>>());

        // Byte-identical mapping source compiled against each helper set in turn.
        const string mappingSource =
            "public class C : IHelperValueMapping { public string GetValue() => SharedHelpers.Provider.Value(); }";

        var scriptCodeA = new ScriptCode(
            location: "inline",
            code: mappingSource,
            type: MappingType.Local,
            encoding: CodeEncoding.Native,
            scripts: new ScriptSettings(
                helpers: [new Reference("shared-helper", "domain-a", RuntimeSysSchemaInfo.Mappings, "1.0.0")]));

        var scriptCodeB = new ScriptCode(
            location: "inline",
            code: mappingSource,
            type: MappingType.Local,
            encoding: CodeEncoding.Native,
            scripts: new ScriptSettings(
                helpers: [new Reference("shared-helper", "domain-b", RuntimeSysSchemaInfo.Mappings, "1.0.0")]));

        var instanceA = await engine.CompileToInstanceAsync<IHelperValueMapping>(
            scriptCodeA, extraReferences: [ContractRef]);
        var instanceB = await engine.CompileToInstanceAsync<IHelperValueMapping>(
            scriptCodeB, extraReferences: [ContractRef]);

        instanceA.GetValue().ShouldBe("A");
        instanceB.GetValue().ShouldBe("B");
    }
}
