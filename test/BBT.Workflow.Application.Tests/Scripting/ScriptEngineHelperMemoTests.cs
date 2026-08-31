using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting.Evaluators;
using BBT.Workflow.Scripting.Functions;
using BBT.Workflow.Scripting.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Scripting;

/// <summary>
/// Katman 1 Task 4 (A7): pins the generation-token guard on the engine's helper-set resolution memo.
/// The authored helper reference (domain/key/version) is NEVER the cache identity by itself — a version
/// resolves floating, exactly as the mapping <c>CacheSet&lt;Mapping&gt;</c> itself resolves it (see
/// <see cref="IComponentGenerationProvider"/>): a hotfix publish that keeps the authored version spelling
/// reachable must still surface its new content on the next compile. These tests drive the real
/// production wiring (<see cref="ScriptEngine"/> -&gt; <see cref="IScriptHelperRegistry"/> -&gt;
/// <see cref="IEvaluator"/>) with mocked <see cref="IComponentCacheStore"/> and
/// <see cref="IComponentGenerationProvider"/>, mirroring <see cref="ScriptEngineHelperSetIsolationTests"/>.
/// </summary>
[Collection("ScriptingTests")]
public class ScriptEngineHelperMemoTests
{
    private const string Domain = "domain-memo-guard";
    private const string HelperKey = "shared-helper";
    private const string HelperVersion = "1.0.0";

    private static Mapping BuildHelperMapping(string key, string domain, string version, string code)
    {
        var mapping = new Mapping(name: key, code: code, encoding: CodeEncoding.Native);
        mapping.SetReference(new Reference(key, domain, RuntimeSysSchemaInfo.Mappings, version));
        return mapping;
    }

    /// <summary>
    /// One self-contained engine + mocked store/generation-provider per test, mirroring the
    /// fresh-evaluator-per-test convention used across this suite (each test's static memo tables are
    /// scoped to its own <see cref="IEvaluator"/> instance).
    /// </summary>
    private sealed class Fixture
    {
        public CSharpEvaluator Evaluator { get; } = new();
        public Mock<IComponentCacheStore> ComponentStore { get; } = new();
        public Mock<IComponentGenerationProvider> GenerationProvider { get; } = new();
        public ScriptEngine Engine { get; }
        public ScriptCode ScriptCodeWithHelper { get; }

        public Fixture()
        {
            var helperRegistry = new ScriptHelperRegistry(Evaluator);

            var currentSchema = new Mock<ICurrentSchema>();
            currentSchema.Setup(x => x.Change(It.IsAny<string>())).Returns(Mock.Of<IDisposable>());

            // Initial generation token for the (componentTypeKey, domain, key) triple the mapping
            // CacheSet itself reads/bumps under (RuntimeSysSchemaInfo.Mappings = "sys-mappings").
            GenerationProvider
                .Setup(p => p.GetAsync(RuntimeSysSchemaInfo.Mappings, Domain, HelperKey, It.IsAny<CancellationToken>()))
                .ReturnsAsync("gen-1");

            SetupStoreToReturn(
                "namespace HelperMemoGuard { public static class Provider { public static string Value() => \"v1\"; } }");

            var services = new ServiceCollection();
            services.AddSingleton(ComponentStore.Object);
            services.AddSingleton(currentSchema.Object);
            services.AddSingleton(GenerationProvider.Object);
            var serviceProvider = services.BuildServiceProvider();

            Engine = new ScriptEngine(
                Evaluator,
                Mock.Of<IScriptServices>(),
                helperRegistry,
                new ScriptHelpersOptions { Enabled = true },
                serviceProvider,
                Mock.Of<ILogger<ScriptEngine>>());

            const string mappingSource =
                "public class HelperMemoGuardProbe : IHelperValueMapping " +
                "{ public string GetValue() => HelperMemoGuard.Provider.Value(); }";

            ScriptCodeWithHelper = new ScriptCode(
                location: "inline",
                code: mappingSource,
                type: MappingType.Local,
                encoding: CodeEncoding.Native,
                scripts: new ScriptSettings(
                    helpers: [new Reference(HelperKey, Domain, RuntimeSysSchemaInfo.Mappings, HelperVersion)]));
        }

        /// <summary>Re-points the mocked store's answer for the (fixed) helper reference to new content.</summary>
        public void SetupStoreToReturn(string helperCode)
        {
            var mapping = BuildHelperMapping(HelperKey, Domain, HelperVersion, helperCode);
            ComponentStore
                .Setup(x => x.GetMappingAsync(Domain, HelperKey, HelperVersion, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Result<Mapping>.Ok(mapping));
        }
    }

    [Fact]
    public async Task HelperResolution_SecondCompile_SkipsStoreWhenTokensUnchanged()
    {
        var fx = new Fixture();

        await fx.Engine.CompileToInstanceAsync<IHelperValueMapping>(fx.ScriptCodeWithHelper);
        fx.ComponentStore.Invocations.Clear();

        await fx.Engine.CompileToInstanceAsync<IHelperValueMapping>(fx.ScriptCodeWithHelper);

        // Token unchanged ("gen-1" both times) -> memo hit -> the component store is never touched again
        // for helper resolution.
        fx.ComponentStore.Verify(
            s => s.GetMappingAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task HelperResolution_TokenBump_TriggersFullReResolve_AndNewHelperContentWins()
    {
        var fx = new Fixture();

        await fx.Engine.CompileToInstanceAsync<IHelperValueMapping>(fx.ScriptCodeWithHelper);

        // Hotfix simulation: the SAME (componentTypeKey, domain, key) triple the mapping CacheSet reads
        // is bumped and the store now answers with new content, while the authored reference (domain/
        // key/version) on the consuming ScriptCode is completely unchanged — floating resolution.
        fx.GenerationProvider
            .Setup(p => p.GetAsync(RuntimeSysSchemaInfo.Mappings, Domain, HelperKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync("gen-2");
        fx.SetupStoreToReturn(
            "namespace HelperMemoGuard { public static class Provider { public static string Value() => \"v2\"; } }");

        var result = await fx.Engine.CompileToInstanceAsync<IHelperValueMapping>(fx.ScriptCodeWithHelper);

        // Token mismatch -> full re-resolve through the component store, not served from the memo.
        fx.ComponentStore.Verify(
            s => s.GetMappingAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        result.GetValue().ShouldBe("v2"); // floating resolution is visible immediately, no stale memo
    }
}
