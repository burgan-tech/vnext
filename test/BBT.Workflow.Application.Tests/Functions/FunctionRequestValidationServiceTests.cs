using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Functions.Contracts;
using BBT.Workflow.Functions.Validation;
using BBT.Workflow.Scripting;
using BBT.Workflow.Tasks.Coordinator;
using BBT.Workflow.Validation;
using BBT.Workflow.Selection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Functions;

/// <summary>
/// Unit tests for <see cref="FunctionRequestValidationService"/>: the request body is only validated
/// when the function declares an input schema and the request actually carries a body, so functions
/// authored before contract declaration are unaffected. Rule-based input schemas resolve through
/// <see cref="FunctionContractResolver"/> before validation runs.
/// </summary>
public sealed class FunctionRequestValidationServiceTests
{
    private const string TestDomain = FunctionTestFactory.Domain;
    private const string TestVersion = FunctionTestFactory.Version;

    private readonly IJsonSchemaValidator _schemaValidator = Substitute.For<IJsonSchemaValidator>();
    private readonly IComponentCacheStore _componentCacheStore = Substitute.For<IComponentCacheStore>();
    private readonly ITaskConditionService _conditionService = Substitute.For<ITaskConditionService>();
    private readonly FunctionRequestValidationService _service;

    /// <summary>Counts how often the lazy script context was actually materialized.</summary>
    private int _scriptContextBuilds;

    public FunctionRequestValidationServiceTests()
    {
        _service = new FunctionRequestValidationService(
            _schemaValidator,
            _componentCacheStore,
            new FunctionContractResolver(
                new RuleBasedSelectionResolver(_conditionService),
                NullLogger<FunctionContractResolver>.Instance),
            NullLogger<FunctionRequestValidationService>.Instance);
    }

    [Fact]
    public async Task NoInputSchema_ReturnsOk_WithoutTouchingTheCache()
    {
        var function = CreateFunction(inputSchemaJson: null);

        var result = await _service.ValidateRequestAsync(function, ParseBody("""{"a":1}"""), Lazy());

        result.IsSuccess.ShouldBeTrue();
        await _componentCacheStore.DidNotReceiveWithAnyArgs()
            .GetSchemaAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task NoBody_ReturnsOk_WithoutTouchingTheCache()
    {
        var function = CreateFunction(SingleSchema());

        var result = await _service.ValidateRequestAsync(function, body: null, Lazy());

        result.IsSuccess.ShouldBeTrue();
        await _componentCacheStore.DidNotReceiveWithAnyArgs()
            .GetSchemaAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task NullJsonBody_ReturnsOk()
    {
        var function = CreateFunction(SingleSchema());

        var result = await _service.ValidateRequestAsync(function, ParseBody("null"), Lazy());

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task UnresolvableSchema_Fails()
    {
        var function = CreateFunction(SingleSchema());
        _componentCacheStore
            .GetSchemaAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<SchemaDefinition>.Fail(Error.NotFound("schema.notfound", "missing")));

        var result = await _service.ValidateRequestAsync(function, ParseBody("""{"a":1}"""), Lazy());

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe("schema.notfound");
    }

    [Fact]
    public async Task ValidBody_ReturnsOk()
    {
        var function = CreateFunction(SingleSchema());
        SetupSchema();
        _schemaValidator
            .Validate(Arg.Any<JsonElement>(), Arg.Any<JsonElement?>(), Arg.Any<SchemaValidationOptions>())
            .Returns(Result.Ok());

        var result = await _service.ValidateRequestAsync(function, ParseBody("""{"a":1}"""), Lazy());

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task InvalidBody_SurfacesTheValidatorFailure()
    {
        var function = CreateFunction(SingleSchema());
        SetupSchema();
        _schemaValidator
            .Validate(Arg.Any<JsonElement>(), Arg.Any<JsonElement?>(), Arg.Any<SchemaValidationOptions>())
            .Returns(Result.Fail(Error.Validation("schema.invalid", "a is required")));

        var result = await _service.ValidateRequestAsync(function, ParseBody("""{"b":1}"""), Lazy());

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe("schema.invalid");
    }

    [Fact]
    public async Task ResolvesCultureFromAcceptLanguageHeader()
    {
        var function = CreateFunction(SingleSchema());
        SetupSchema();
        _schemaValidator
            .Validate(Arg.Any<JsonElement>(), Arg.Any<JsonElement?>(), Arg.Any<SchemaValidationOptions>())
            .Returns(Result.Ok());

        var headers = new Dictionary<string, string?> { ["Accept-Language"] = "tr-TR" };
        await _service.ValidateRequestAsync(function, ParseBody("""{"a":1}"""), Lazy(), headers);

        _schemaValidator.Received(1).Validate(
            Arg.Any<JsonElement>(),
            Arg.Any<JsonElement?>(),
            Arg.Is<SchemaValidationOptions>(o => o.Culture == "tr-TR"));
    }

    [Fact]
    public async Task SingleSchema_NeverBuildsTheScriptContext()
    {
        var function = CreateFunction(SingleSchema());
        SetupSchema();
        _schemaValidator
            .Validate(Arg.Any<JsonElement>(), Arg.Any<JsonElement?>(), Arg.Any<SchemaValidationOptions>())
            .Returns(Result.Ok());

        await _service.ValidateRequestAsync(function, ParseBody("""{"a":1}"""), Lazy());

        _scriptContextBuilds.ShouldBe(0);
    }

    [Fact]
    public async Task RuleBasedSchema_ValidatesAgainstTheFirstMatchingEntry()
    {
        var function = CreateFunction(RuleBasedSchemas());
        SetupSchema("second-schema");
        SetupCondition(matches: false, thenMatches: true);
        _schemaValidator
            .Validate(Arg.Any<JsonElement>(), Arg.Any<JsonElement?>(), Arg.Any<SchemaValidationOptions>())
            .Returns(Result.Ok());

        var result = await _service.ValidateRequestAsync(function, ParseBody("""{"a":1}"""), Lazy());

        result.IsSuccess.ShouldBeTrue();
        await _componentCacheStore.Received(1).GetSchemaAsync(
            TestDomain, "second-schema", TestVersion, Arg.Any<CancellationToken>());
        _scriptContextBuilds.ShouldBe(1);
    }

    [Fact]
    public async Task RuleBasedSchema_NoRuleMatches_SkipsValidationEntirely()
    {
        var function = CreateFunction(RuleBasedSchemas());
        SetupCondition(matches: false, thenMatches: false);

        var result = await _service.ValidateRequestAsync(function, ParseBody("""{"a":1}"""), Lazy());

        result.IsSuccess.ShouldBeTrue();
        await _componentCacheStore.DidNotReceiveWithAnyArgs()
            .GetSchemaAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task RuleBasedSchema_FallsBackToTheRuleLessEntry()
    {
        var function = CreateFunction(RuleThenFallbackSchemas());
        SetupSchema("fallback-schema");
        _conditionService
            .ExecuteConditionAsync(Arg.Any<ScriptCode>(), Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<bool>.Ok(false));
        _schemaValidator
            .Validate(Arg.Any<JsonElement>(), Arg.Any<JsonElement?>(), Arg.Any<SchemaValidationOptions>())
            .Returns(Result.Ok());

        var result = await _service.ValidateRequestAsync(function, ParseBody("""{"a":1}"""), Lazy());

        result.IsSuccess.ShouldBeTrue();
        await _componentCacheStore.Received(1).GetSchemaAsync(
            TestDomain, "fallback-schema", TestVersion, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RuleBasedSchema_RuleFailure_SkipsToTheNextEntry()
    {
        var function = CreateFunction(RuleThenFallbackSchemas());
        SetupSchema("fallback-schema");
        _conditionService
            .ExecuteConditionAsync(Arg.Any<ScriptCode>(), Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<bool>.Fail(Error.Failure("script.boom", "compile error")));
        _schemaValidator
            .Validate(Arg.Any<JsonElement>(), Arg.Any<JsonElement?>(), Arg.Any<SchemaValidationOptions>())
            .Returns(Result.Ok());

        var result = await _service.ValidateRequestAsync(function, ParseBody("""{"a":1}"""), Lazy());

        result.IsSuccess.ShouldBeTrue();
        await _componentCacheStore.Received(1).GetSchemaAsync(
            TestDomain, "fallback-schema", TestVersion, Arg.Any<CancellationToken>());
    }

    private LazyScriptContext Lazy() => new(_ =>
    {
        _scriptContextBuilds++;
        return Task.FromResult(new ScriptContext(NullLogger<ScriptContext>.Instance));
    });

    private static string SingleSchema() =>
        $$""" "inputSchema": {{FunctionTestFactory.Ref("in-schema", "sys-schemas")}} """;

    private static string RuleBasedSchemas() =>
        $$"""
          "inputSchema": [
              { "rule": {{FunctionTestFactory.Rule("first")}}, "schema": {{FunctionTestFactory.Ref("first-schema", "sys-schemas")}} },
              { "rule": {{FunctionTestFactory.Rule("second")}}, "schema": {{FunctionTestFactory.Ref("second-schema", "sys-schemas")}} }
          ]
          """;

    private static string RuleThenFallbackSchemas() =>
        $$"""
          "inputSchema": [
              { "rule": {{FunctionTestFactory.Rule("first")}}, "schema": {{FunctionTestFactory.Ref("first-schema", "sys-schemas")}} },
              { "schema": {{FunctionTestFactory.Ref("fallback-schema", "sys-schemas")}} }
          ]
          """;

    private static JsonElement ParseBody(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static Function CreateFunction(string? inputSchemaJson) =>
        FunctionTestFactory.FromJson(FunctionTestFactory.Attributes(inputSchemaJson));

    /// <summary>Returns <paramref name="matches"/> on the first call and <paramref name="thenMatches"/> after.</summary>
    private void SetupCondition(bool matches, bool thenMatches)
    {
        _conditionService
            .ExecuteConditionAsync(Arg.Any<ScriptCode>(), Arg.Any<ScriptContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<bool>.Ok(matches), Result<bool>.Ok(thenMatches));
    }

    private void SetupSchema(string key = "in-schema")
    {
        var schema = JsonSerializer.Deserialize<SchemaDefinition>(
            """{"type":"json","schema":{"type":"object"}}""",
            JsonSerializerConstants.JsonOptions)!;
        schema.SetReference(new Reference(key, TestDomain, "sys-schemas", TestVersion));

        _componentCacheStore
            .GetSchemaAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<SchemaDefinition>.Ok(schema));
    }
}
