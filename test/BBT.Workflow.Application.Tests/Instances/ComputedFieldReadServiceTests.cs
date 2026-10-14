using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Aether.Users;
using BBT.Workflow.Authorization;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// Runtime <c>x-computed</c> on the read path: added to every exposed row, built from what the caller sees (after
/// <c>x-roles</c>), schema resolved once per scope.
/// </summary>
public sealed class ComputedFieldReadServiceTests
{
    private const string SchemaJson = """
        { "type": "object", "properties": {
            "firstName": { "type": "string" },
            "lastName": { "type": "string", "x-roles": [ { "role": "admin", "grant": "allow" } ] },
            "fullName": { "type": "string", "readOnly": true,
                          "x-computed": { "evaluatedBy": "runtime", "fallback": { "join": { "fields": ["firstName", "lastName"] } } } },
            "nickname": { "type": "string", "x-computed": { "evaluatedBy": "client", "fallback": { "join": { "fields": ["firstName"] } } } } } }
        """;

    private readonly IComponentCacheStore _cache = Substitute.For<IComponentCacheStore>();
    private readonly ICallerRoleResolver _roles = Substitute.For<ICallerRoleResolver>();

    [Theory]
    [InlineData("admin", "Ada Lovelace")]
    [InlineData("reader", "Ada")]
    public async Task ComputedValue_IsBuiltFromTheCallersView(string role, string expected)
    {
        var workflow = ConfigureWorkflow();
        _roles.ResolveRolesAsync(Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(Result<string[]?>.Ok([role]));
        var service = CreateReadService();
        var instance = InstanceFactory.CreateDefault("i-1");
        var row = instance.SeedStoredData("""{"firstName":"Ada","lastName":"Lovelace","fullName":"stale"}""");

        var exposed = (await service.ExposeAsync(workflow, instance, row, requestContext: null))!.Value;

        exposed.GetProperty("fullName").GetString().ShouldBe(expected);
        exposed.TryGetProperty("nickname", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task ListPage_ResolvesTheSchemaOncePerScope()
    {
        var workflow = ConfigureWorkflow();
        var computed = new ComputedFieldReadService(_cache);
        using var json = JsonDocument.Parse("""{"firstName":"Ada"}""");

        (await computed.ApplyAsync(workflow, json.RootElement)).GetProperty("fullName").GetString().ShouldBe("Ada");
        (await computed.ApplyAsync(workflow, json.RootElement)).GetProperty("fullName").GetString().ShouldBe("Ada");

        await _cache.Received(1).GetSchemaAsync("bank", "schema", "1.0.0", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnloadableSchema_LeavesTheDataUnchanged()
    {
        var workflow = ConfigureWorkflow();
        _cache.GetSchemaAsync("bank", "schema", "1.0.0", Arg.Any<CancellationToken>())
            .Returns(Result<SchemaDefinition>.Fail(Error.NotFound("schema", "missing")));
        using var json = JsonDocument.Parse("""{"firstName":"Ada"}""");

        var result = await new ComputedFieldReadService(_cache).ApplyAsync(workflow, json.RootElement);

        result.TryGetProperty("fullName", out _).ShouldBeFalse();
    }

    private InstanceDataReadService CreateReadService() => new(
        new SchemaFieldFilterService(_cache,
            new TransitionAuthorizationManager(Substitute.For<ICurrentUser>(), Substitute.For<IInstanceTransitionRepository>()),
            _roles, new FakeFieldMaskingEngine(), Microsoft.Extensions.Options.Options.Create(new SchemaMaskingOptions())),
        computedFields: new ComputedFieldReadService(_cache));

    private Definitions.Workflow ConfigureWorkflow()
    {
        var reference = new Reference("schema", "bank", "sys-schemas", "1.0.0");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var workflow = JsonSerializer.Deserialize<Definitions.Workflow>(
            "{\"type\":\"F\",\"states\":[],\"schema\":" + JsonSerializer.Serialize(reference) + "}", options)!;
        workflow.SetReference(new Reference("flow", "bank", "sys-flows", "1.0.0"));
        var schema = JsonSerializer.Deserialize<SchemaDefinition>(
            "{\"type\":\"master\",\"schema\":" + SchemaJson + "}", options)!;
        schema.SetReference(reference);
        _cache.GetSchemaAsync("bank", "schema", "1.0.0", Arg.Any<CancellationToken>()).Returns(Result<SchemaDefinition>.Ok(schema));
        return workflow;
    }
}
