using System;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Evaluators;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Evaluators;

public sealed class CacheKeyEvaluatorTests
{
    private readonly IDynamicExpressoValueEvaluator _expresso = Substitute.For<IDynamicExpressoValueEvaluator>();
    private readonly IScriptEngine _engine = Substitute.For<IScriptEngine>();
    private readonly IComponentCacheStore _store = Substitute.For<IComponentCacheStore>();
    private readonly ICurrentSchema _schema = Substitute.For<ICurrentSchema>();
    private readonly ScriptContext _context = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
        .SetRuntime(Substitute.For<IRuntimeInfoProvider>()).Build();

    private CacheKeyEvaluator Sut() => new(_expresso, _engine, _store, _schema, NullLogger<CacheKeyEvaluator>.Instance);

    [Fact]
    public async Task DynamicExpresso_Native_DelegatesToExpresso()
    {
        var script = ScriptCode.FromNative("\"k:\" + context.Instance.Key", location: "dynamicExpresso");
        _expresso.Evaluate(script, _context).Returns(Result<string>.Ok("k:1"));

        (await Sut().EvaluateAsync(script, _context)).Value.ShouldBe("k:1");
    }

    [Fact]
    public async Task DynamicExpresso_Base64_DelegatesToExpresso()
    {
        var script = ScriptCode.FromBase64("ImsiOg==", location: "dynamicExpresso");
        _expresso.Evaluate(script, _context).Returns(Result<string>.Ok("k:"));

        (await Sut().EvaluateAsync(script, _context)).Value.ShouldBe("k:");
    }

    [Fact]
    public async Task DynamicExpresso_Reference_ResolvesBodyThenEvaluatesNative()
    {
        var script = ScriptCode.FromReference(new Reference("key-expr", "core", "sys-mappings", "1.0.0"), location: "dynamicExpresso");
        StubMapping("key-expr", "\"from-ref\"");
        _expresso.Evaluate(Arg.Is<ScriptCode>(s => s.Location == "dynamicExpresso" && s.DecodedCode == "\"from-ref\""), _context)
            .Returns(Result<string>.Ok("from-ref"));

        (await Sut().EvaluateAsync(script, _context)).Value.ShouldBe("from-ref");
    }

    [Fact]
    public async Task DynamicExpresso_UnresolvableReference_Fails()
    {
        var script = ScriptCode.FromReference(new Reference("missing", "core", "sys-mappings", "1.0.0"), location: "dynamicExpresso");
        // _store returns its default (failed/null) result for "missing"
        (await Sut().EvaluateAsync(script, _context)).IsSuccess.ShouldBeFalse();
    }

    [Theory]
    [InlineData(0)] // NAT
    [InlineData(1)] // B64
    [InlineData(2)] // REF
    public async Task Roslyn_AnyEncoding_CompilesICacheKeyMapping(int encoding)
    {
        var script = encoding switch
        {
            0 => ScriptCode.FromNative("class K {}", location: "./key.csx"),
            1 => ScriptCode.FromBase64("Y2xhc3MgSyB7fQ==", location: "./key.csx"),
            _ => ScriptCode.FromReference(new Reference("key-cs", "core", "sys-mappings", "1.0.0"), location: "./key.csx")
        };
        var mapping = Substitute.For<ICacheKeyMapping>();
        mapping.Handler(_context).Returns("cs:key");
        _engine.CompileToInstanceAsync<ICacheKeyMapping>(script, Arg.Any<ScriptSettings?>(), null, null, Arg.Any<CancellationToken>())
            .Returns(mapping);

        (await Sut().EvaluateAsync(script, _context)).Value.ShouldBe("cs:key");
        _expresso.DidNotReceiveWithAnyArgs().Evaluate(default!, default!);
    }

    [Fact]
    public async Task Roslyn_HandlerThrows_Fails()
    {
        var script = ScriptCode.FromNative("class K {}", location: "./key.csx");
        var mapping = Substitute.For<ICacheKeyMapping>();
        mapping.Handler(_context).Returns<Task<string?>>(_ => throw new InvalidOperationException("boom"));
        _engine.CompileToInstanceAsync<ICacheKeyMapping>(script, Arg.Any<ScriptSettings?>(), null, null, Arg.Any<CancellationToken>())
            .Returns(mapping);

        var result = await Sut().EvaluateAsync(script, _context);
        result.IsSuccess.ShouldBeFalse();
        result.Error.Message.ShouldContain("boom");
    }

    [Fact]
    public async Task Roslyn_NullResult_IsEmptyString()
    {
        var script = ScriptCode.FromNative("class K {}", location: "./key.csx");
        var mapping = Substitute.For<ICacheKeyMapping>();
        mapping.Handler(_context).Returns((string?)null);
        _engine.CompileToInstanceAsync<ICacheKeyMapping>(script, Arg.Any<ScriptSettings?>(), null, null, Arg.Any<CancellationToken>())
            .Returns(mapping);

        (await Sut().EvaluateAsync(script, _context)).Value.ShouldBe(string.Empty);
    }

    private void StubMapping(string key, string body)
    {
        var mapping = new Mapping(key, body, CodeEncoding.Native);
        _store.GetMappingAsync("core", key, "1.0.0", Arg.Any<CancellationToken>())
            .Returns(Result<Mapping>.Ok(mapping));
    }
}
