using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Functions.Validation;
using BBT.Workflow.Validation;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Functions;

/// <summary>
/// Unit tests for <see cref="FunctionRequestValidationService"/>: the request body is only validated
/// when the function declares an input schema and the request actually carries a body, so functions
/// authored before contract declaration are unaffected.
/// </summary>
public sealed class FunctionRequestValidationServiceTests
{
    private const string TestDomain = "test-domain";
    private const string TestVersion = "1.0.0";

    private readonly IJsonSchemaValidator _schemaValidator = Substitute.For<IJsonSchemaValidator>();
    private readonly IComponentCacheStore _componentCacheStore = Substitute.For<IComponentCacheStore>();
    private readonly FunctionRequestValidationService _service;

    public FunctionRequestValidationServiceTests()
    {
        _service = new FunctionRequestValidationService(
            _schemaValidator,
            _componentCacheStore,
            NullLogger<FunctionRequestValidationService>.Instance);
    }

    [Fact]
    public async Task NoInputSchema_ReturnsOk_WithoutTouchingTheCache()
    {
        var function = CreateFunction(inputSchema: null);

        var result = await _service.ValidateRequestAsync(function, ParseBody("""{"a":1}"""));

        result.IsSuccess.ShouldBeTrue();
        await _componentCacheStore.DidNotReceiveWithAnyArgs()
            .GetSchemaAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task NoBody_ReturnsOk_WithoutTouchingTheCache()
    {
        var function = CreateFunction(SchemaRef());

        var result = await _service.ValidateRequestAsync(function, body: null);

        result.IsSuccess.ShouldBeTrue();
        await _componentCacheStore.DidNotReceiveWithAnyArgs()
            .GetSchemaAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task NullJsonBody_ReturnsOk()
    {
        var function = CreateFunction(SchemaRef());

        var result = await _service.ValidateRequestAsync(function, ParseBody("null"));

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task UnresolvableSchema_Fails()
    {
        var function = CreateFunction(SchemaRef());
        _componentCacheStore
            .GetSchemaAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<SchemaDefinition>.Fail(Error.NotFound("schema.notfound", "missing")));

        var result = await _service.ValidateRequestAsync(function, ParseBody("""{"a":1}"""));

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe("schema.notfound");
    }

    [Fact]
    public async Task ValidBody_ReturnsOk()
    {
        var function = CreateFunction(SchemaRef());
        SetupSchema();
        _schemaValidator
            .Validate(Arg.Any<JsonElement>(), Arg.Any<JsonElement?>(), Arg.Any<SchemaValidationOptions>())
            .Returns(Result.Ok());

        var result = await _service.ValidateRequestAsync(function, ParseBody("""{"a":1}"""));

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task InvalidBody_SurfacesTheValidatorFailure()
    {
        var function = CreateFunction(SchemaRef());
        SetupSchema();
        _schemaValidator
            .Validate(Arg.Any<JsonElement>(), Arg.Any<JsonElement?>(), Arg.Any<SchemaValidationOptions>())
            .Returns(Result.Fail(Error.Validation("schema.invalid", "a is required")));

        var result = await _service.ValidateRequestAsync(function, ParseBody("""{"b":1}"""));

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe("schema.invalid");
    }

    [Fact]
    public async Task ResolvesCultureFromAcceptLanguageHeader()
    {
        var function = CreateFunction(SchemaRef());
        SetupSchema();
        _schemaValidator
            .Validate(Arg.Any<JsonElement>(), Arg.Any<JsonElement?>(), Arg.Any<SchemaValidationOptions>())
            .Returns(Result.Ok());

        var headers = new Dictionary<string, string?> { ["Accept-Language"] = "tr-TR" };
        await _service.ValidateRequestAsync(function, ParseBody("""{"a":1}"""), headers);

        _schemaValidator.Received(1).Validate(
            Arg.Any<JsonElement>(),
            Arg.Any<JsonElement?>(),
            Arg.Is<SchemaValidationOptions>(o => o.Culture == "tr-TR"));
    }

    private static Reference SchemaRef() => new("in-schema", TestDomain, "sys-schemas", TestVersion);

    private static JsonElement ParseBody(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static Function CreateFunction(Reference? inputSchema)
    {
        var task = OnExecuteTask.Create(
            1,
            new Reference("my-task", TestDomain, "sys-tasks", TestVersion),
            ScriptCode.FromNative(string.Empty));
        var function = new Function(TaskScope.Domain, task, inputSchema: inputSchema);
        function.SetReference(new Reference("my-fn", TestDomain, "sys-functions", TestVersion));
        return function;
    }

    private void SetupSchema()
    {
        var schema = JsonSerializer.Deserialize<SchemaDefinition>(
            """{"type":"json","schema":{"type":"object"}}""",
            JsonSerializerConstants.JsonOptions)!;
        schema.SetReference(SchemaRef());

        _componentCacheStore
            .GetSchemaAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<SchemaDefinition>.Ok(schema));
    }
}
