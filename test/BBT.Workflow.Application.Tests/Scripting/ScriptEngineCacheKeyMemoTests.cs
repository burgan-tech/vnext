using System;
using System.Collections.Generic;
using System.Runtime.Loader;
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
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Scripting;

/// <summary>
/// The engine memoizes the evaluator cache key per (ScriptCode instance, target type, profile) instead
/// of re-running SHA-256 over source hash + type name + profile on every compile. The memoized key must
/// be stable across calls, differ for a different source / target type / grant, and follow a REF body
/// that changes under the same ScriptCode (hotfix publish) — never serve a stale key.
/// </summary>
[Collection("ScriptingTests")]
public sealed class ScriptEngineCacheKeyMemoTests
{
    [Fact]
    public async Task SameScriptCode_KeyIsStableAndComputedOnce()
    {
        var evaluator = new RecordingEvaluator();
        var engine = BuildEngine(evaluator);
        var sc = ScriptCode.FromNative(Source("Stable"));

        await engine.CompileToInstanceAsync<ITransitionMapping>(sc);
        await engine.CompileToInstanceAsync<ITransitionMapping>(sc);
        await engine.CompileToInstanceAsync<ITransitionMapping>(sc);

        evaluator.PrecomputedKeys.Count.ShouldBe(3);
        evaluator.PrecomputedKeys.ShouldAllBe(k => k == evaluator.PrecomputedKeys[0]);
        evaluator.ComputeCacheKeyCalls.ShouldBe(1);
    }

    [Fact]
    public async Task MemoizedKey_EqualsTheEvaluatorsOwnAlgorithm()
    {
        var evaluator = new RecordingEvaluator();
        var engine = BuildEngine(evaluator);
        var sc = ScriptCode.FromNative(Source("Algo"));

        await engine.CompileToInstanceAsync<ITransitionMapping>(sc);
        await engine.CompileToInstanceAsync<ITransitionMapping>(sc);

        var expected = evaluator.Inner.ComputeCacheKey(sc.ContentHash, typeof(ITransitionMapping), evaluator.LastProfile!);
        evaluator.PrecomputedKeys.ShouldAllBe(k => k == expected);
    }

    [Fact]
    public async Task DifferentSource_TargetType_OrGrant_GiveDifferentKeys()
    {
        var evaluator = new RecordingEvaluator();
        var engine = BuildEngine(evaluator);
        var a = ScriptCode.FromNative(Source("KeyA"));
        var b = ScriptCode.FromNative(Source("KeyB"));

        await engine.CompileToInstanceAsync<ITransitionMapping>(a);
        await engine.CompileToInstanceAsync<ITransitionMapping>(b);
        await engine.CompileToInstanceAsync<object>(a);
        await engine.CompileToInstanceAsync<ITransitionMapping>(
            a, flowScripts: new ScriptSettings(allowedAssemblies: ["System.Xml"]));
        // And back to the first combination: served from the memo, same key as the first call.
        await engine.CompileToInstanceAsync<ITransitionMapping>(a);

        var keys = evaluator.PrecomputedKeys;
        keys.Count.ShouldBe(5);
        new HashSet<string?>(keys[..4]).Count.ShouldBe(4);
        keys[4].ShouldBe(keys[0]);
        evaluator.ComputeCacheKeyCalls.ShouldBe(4);
    }

    [Fact]
    public async Task ReferencedBody_ChangingUnderTheSameScriptCode_GetsANewKey_EqualBodyReusesIt()
    {
        var v2 = Source("RefV2");
        var bodies = new Queue<string>([Source("RefV1"), v2, new string(v2.ToCharArray())]);
        var currentBody = "";
        var componentStore = Substitute.For<IComponentCacheStore>();
        componentStore.GetMappingAsync("ref-domain", "ref-mapping", "1.0.0", Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                currentBody = bodies.Dequeue();
                return Result<Mapping>.Ok(new Mapping(name: "ref-mapping", code: currentBody, encoding: CodeEncoding.Native));
            });
        var currentSchema = Substitute.For<ICurrentSchema>();
        currentSchema.Change(Arg.Any<string>()).Returns(Substitute.For<IDisposable>());

        var services = new ServiceCollection();
        services.AddSingleton(componentStore);
        services.AddSingleton(currentSchema);
        using var serviceProvider = services.BuildServiceProvider();

        var evaluator = new RecordingEvaluator();
        var engine = BuildEngine(evaluator, serviceProvider);
        var sc = ScriptCode.FromReference(new Reference("ref-mapping", "ref-domain", RuntimeSysSchemaInfo.Mappings, "1.0.0"));

        await engine.CompileToInstanceAsync<ITransitionMapping>(sc); // V1
        await engine.CompileToInstanceAsync<ITransitionMapping>(sc); // V2 (hotfix publish)
        await engine.CompileToInstanceAsync<ITransitionMapping>(sc); // V2 again, a different string instance

        var keys = evaluator.PrecomputedKeys;
        keys[1].ShouldNotBe(keys[0]);
        keys[2].ShouldBe(keys[1]);
        evaluator.ComputeCacheKeyCalls.ShouldBe(2);
    }

    private static string Source(string tag) =>
        "public class " + tag + Guid.NewGuid().ToString("N") +
        " : ITransitionMapping { public Task<dynamic> Handler(ScriptContext context) => Task.FromResult<dynamic>(1); }";

    private static ScriptEngine BuildEngine(IEvaluator evaluator, IServiceProvider? serviceProvider = null) =>
        new(
            evaluator,
            Substitute.For<IScriptServices>(),
            new ScriptHelperRegistry(evaluator),
            new ScriptHelpersOptions { Enabled = false },
            serviceProvider ?? new ServiceCollection().BuildServiceProvider(),
            NullLogger<ScriptEngine>.Instance);

    /// <summary>A real <see cref="CSharpEvaluator"/> that records the keys the engine hands it.</summary>
    private sealed class RecordingEvaluator : IEvaluator
    {
        public CSharpEvaluator Inner { get; } = new();
        public List<string?> PrecomputedKeys { get; } = [];
        public int ComputeCacheKeyCalls { get; private set; }
        public string? LastProfile { get; private set; }

        public Task<EvaluatorCompilation<T>> CompileToInstanceAsync<T>(
            string code, IScriptServices? services = null, IEnumerable<MetadataReference>? extraReferences = null,
            IEnumerable<string>? usingDirectives = null, CancellationToken cancellationToken = default,
            AssemblyLoadContext? loadContext = null, IReadOnlyList<string>? sandboxGrant = null,
            string? precomputedCacheKey = null)
        {
            PrecomputedKeys.Add(precomputedCacheKey);
            return Inner.CompileToInstanceAsync<T>(code, services, extraReferences, usingDirectives,
                cancellationToken, loadContext, sandboxGrant, precomputedCacheKey);
        }

        public int CachedTypeCount => Inner.CachedTypeCount;

        public string BuildProfile(IEnumerable<MetadataReference>? extraReferences, IEnumerable<string>? usingDirectives,
            IReadOnlyList<string>? sandboxGrant, AssemblyLoadContext? loadContext)
        {
            LastProfile = Inner.BuildProfile(extraReferences, usingDirectives, sandboxGrant, loadContext);
            return LastProfile;
        }

        public string ComputeCacheKey(string sourceHashHex, Type targetType, string profile)
        {
            ComputeCacheKeyCalls++;
            return Inner.ComputeCacheKey(sourceHashHex, targetType, profile);
        }

        public CompiledHelpers CompileHelpers(IReadOnlyList<(string Path, string Code)> sources, AssemblyLoadContext loadContext,
            IEnumerable<MetadataReference>? extraReferences = null, IEnumerable<string>? usingDirectives = null,
            IReadOnlyList<string>? sandboxGrant = null, CancellationToken cancellationToken = default) =>
            Inner.CompileHelpers(sources, loadContext, extraReferences, usingDirectives, sandboxGrant, cancellationToken);
    }
}
