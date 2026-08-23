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
/// Behavior-pin coverage for Katman 1 Task 3: the engine's memoized profile (<c>BaseProfiles</c> /
/// <c>HelperProfiles</c>) + precomputed cache-key path must be byte-for-byte behavior-compatible with
/// the pre-existing per-call key derivation — whichever route produced the key, the same compiled type
/// is served and the evaluator's cache grows by exactly one entry per distinct source. These tests pass
/// BEFORE this task's implementation too (the underlying key was already deterministic); what they pin
/// is that the memoized/precomputed path this task introduces can never diverge from it.
/// </summary>
[Collection("ScriptingTests")]
public class ScriptEngineKeyMemoTests
{
    private static ScriptEngine BuildEngine(
        IEvaluator evaluator,
        IScriptHelperRegistry helperRegistry,
        IServiceProvider serviceProvider,
        bool helpersEnabled = false)
    {
        return new ScriptEngine(
            evaluator,
            Mock.Of<IScriptServices>(),
            Mock.Of<IWorkflowMetrics>(),
            helperRegistry,
            new ScriptHelpersOptions { Enabled = helpersEnabled },
            serviceProvider,
            Mock.Of<ILogger<ScriptEngine>>());
    }

    private static Mapping BuildHelperMapping(string key, string domain, string version, string code)
    {
        var mapping = new Mapping(name: key, code: code, encoding: CodeEncoding.Native);
        mapping.SetReference(new Reference(key, domain, RuntimeSysSchemaInfo.Mappings, version));
        return mapping;
    }

    [Fact]
    public async Task InlineScriptCode_SecondCompile_UsesPrecomputedKey_AndHits()
    {
        // Aynı ScriptCode ile iki compile: ikincisi hit olmalı (Compiled=false zaten Katman 0'da
        // pinli); bu test ENGINE yolunun ScriptCode.ContentHash + profile memo'su üzerinden
        // precomputed anahtar ürettiğini, davranışın raw yolla birebir kaldığını pinler:
        // aynı ScriptCode'u RAW string yoluyla derleyen üçüncü çağrı da AYNI tipe hit etmelidir.
        var evaluator = new CSharpEvaluator();
        var helperRegistry = new ScriptHelperRegistry(evaluator);
        using var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var engine = BuildEngine(evaluator, helperRegistry, serviceProvider);

        var sc = ScriptCode.FromNative(
            "public class KeyMemoProbe" + Guid.NewGuid().ToString("N") +
            " : ITransitionMapping { public Task<dynamic> Handler(ScriptContext context) => Task.FromResult<dynamic>(1); }");

        var viaScriptCode = await engine.CompileToInstanceAsync<ITransitionMapping>(sc);
        var viaScriptCode2 = await engine.CompileToInstanceAsync<ITransitionMapping>(sc);
        var viaRaw = await engine.CompileToInstanceAsync<ITransitionMapping>(sc.DecodedCode);

        viaScriptCode.GetType().ShouldBe(viaScriptCode2.GetType());
        viaRaw.GetType().ShouldBe(viaScriptCode.GetType()); // iki yol aynı anahtara çıkar
    }

    [Fact]
    public async Task HelperScriptCode_PrecomputedKeyPath_MatchesRawFallbackPath_SingleCacheEntry()
    {
        // Helper'lı dalda da precomputed-key ile raw-key yolları aynı cache girdisine çıkmalı.
        // Path 1 caller extraReferences/usingDirectives vermez -> engine'in yeni memoized-profile +
        // precomputed-key dalını kullanır. Path 2 caller AÇIKÇA (boş da olsa) bir extraReferences
        // dizisi verir -> "hard rule" gereği engine ham (precomputed olmayan) yola düşer, evaluator
        // kendi GenerateCacheKey'ini üretir. Helper dalındaki refs/usings birleşimi
        // (extraReferences ?? []).Append(helperSet.Reference) / (usingDirectives ?? []).Concat(...)
        // null ile boş diziyi AYNI sıraya indirger, dolayısıyla iki yolun evaluator'a fiilen geçirdiği
        // girdi birebir aynıdır: BuildProfile+ComputeCacheKey hiç sapmazsa ikinci çağrı da HIT olur ve
        // CachedTypeCount yalnızca 1 artar. Bir sapma olsaydı ikinci çağrı miss olur ve sayaç 2 artardı.
        var evaluator = new CSharpEvaluator();
        var helperRegistry = new ScriptHelperRegistry(evaluator);

        var helperMapping = BuildHelperMapping("shared-helper", "domain-memo", "1.0.0",
            "namespace SharedHelpersMemo { public static class Provider { public static string Value() => \"M\"; } }");

        var componentStore = new Mock<IComponentCacheStore>();
        componentStore
            .Setup(x => x.GetMappingAsync("domain-memo", "shared-helper", "1.0.0", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result<Mapping>.Ok(helperMapping));

        var currentSchema = new Mock<ICurrentSchema>();
        currentSchema.Setup(x => x.Change(It.IsAny<string>())).Returns(Mock.Of<IDisposable>());

        var services = new ServiceCollection();
        services.AddSingleton(componentStore.Object);
        services.AddSingleton(currentSchema.Object);
        using var serviceProvider = services.BuildServiceProvider();

        var engine = BuildEngine(evaluator, helperRegistry, serviceProvider, helpersEnabled: true);

        var mappingSource =
            "public class HelperMemoProbe" + Guid.NewGuid().ToString("N") +
            " : IHelperValueMapping { public string GetValue() => SharedHelpersMemo.Provider.Value(); }";

        var scriptCode = new ScriptCode(
            location: "inline",
            code: mappingSource,
            type: MappingType.Local,
            encoding: CodeEncoding.Native,
            scripts: new ScriptSettings(
                helpers: [new Reference("shared-helper", "domain-memo", RuntimeSysSchemaInfo.Mappings, "1.0.0")]));

        var before = evaluator.CachedTypeCount;

        var viaPrecomputed = await engine.CompileToInstanceAsync<IHelperValueMapping>(scriptCode);

        var viaRawFallback = await engine.CompileToInstanceAsync<IHelperValueMapping>(
            scriptCode, extraReferences: Array.Empty<MetadataReference>());

        viaRawFallback.GetType().ShouldBe(viaPrecomputed.GetType());
        (evaluator.CachedTypeCount - before).ShouldBe(1);
    }
}
