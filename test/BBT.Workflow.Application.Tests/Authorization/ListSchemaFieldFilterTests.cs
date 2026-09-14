using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Aether.Users;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Authorization;

/// <summary>List preparation shares schema metadata while keeping caller and instance decisions independent.</summary>
public sealed class ListSchemaFieldFilterTests
{
    private readonly IComponentCacheStore _cache = Substitute.For<IComponentCacheStore>();
    private readonly ICallerRoleResolver _roles = Substitute.For<ICallerRoleResolver>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();

    [Fact]
    public async Task ListScope_ResolvesSchemaOnce_ButRolesForEveryItem_AndStartsFreshForNextList()
    {
        var workflow = ConfigureWorkflow("bank", "1.0.0", "admin");
        _roles.ResolveRolesAsync(Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(Result<string[]?>.Ok(["admin"]), Result<string[]?>.Ok(["reader"]), Result<string[]?>.Ok(["reader"]));
        var factory = CreateFactory();
        var page = factory.CreateForList();
        using var json = JsonDocument.Parse("""{"amount":50,"public":"visible"}""");
        var first = await page.ApplyAsync(workflow, json.RootElement, InstanceFactory.CreateDefault("first"));
        var second = await page.ApplyAsync(workflow, json.RootElement, InstanceFactory.CreateDefault("second"));
        first!.Value.TryGetProperty("amount", out _).ShouldBeTrue();
        second!.Value.TryGetProperty("amount", out _).ShouldBeFalse();
        second.Value.GetProperty("public").GetString().ShouldBe("visible");
        await _cache.Received(1).GetSchemaAsync("bank", "schema", "1.0.0", Arg.Any<CancellationToken>());
        await _roles.Received(2).ResolveRolesAsync(Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<CancellationToken>());

        await factory.CreateForList().ApplyAsync(workflow, json.RootElement, InstanceFactory.CreateDefault("third"));
        await _cache.Received(2).GetSchemaAsync("bank", "schema", "1.0.0", Arg.Any<CancellationToken>());
        await _roles.Received(3).ResolveRolesAsync(Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListScope_PredefinedRoleIsReevaluatedForEachInstance()
    {
        var workflow = ConfigureWorkflow("bank", "1.0.0", "$InstanceStarter");
        _user.ActorUserName.Returns("alice");
        _roles.ResolveRolesAsync(Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(Result<string[]?>.Ok([]));
        var alice = InstanceFactory.CreateDefault("alice-owned");
        alice.CreatedBy = "alice";
        var bob = InstanceFactory.CreateDefault("bob-owned");
        bob.CreatedBy = "bob";
        using var json = JsonDocument.Parse("""{"amount":50}""");
        var page = CreateFactory().CreateForList();

        var visible = await page.ApplyAsync(workflow, json.RootElement, alice);
        var hidden = await page.ApplyAsync(workflow, json.RootElement, bob);
        visible!.Value.TryGetProperty("amount", out _).ShouldBeTrue();
        hidden!.Value.TryGetProperty("amount", out _).ShouldBeFalse();
        await _cache.Received(1).GetSchemaAsync("bank", "schema", "1.0.0", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListScope_KeepsDomainAndSchemaVersionMetadataSeparate()
    {
        var guarded = ConfigureWorkflow("bank", "1.0.0", "admin");
        var later = ConfigureWorkflow("bank", "2.0.0", "reader");
        var otherDomain = ConfigureWorkflow("other", "1.0.0", "reader");
        _roles.ResolveRolesAsync(Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(Result<string[]?>.Ok(["reader"]));
        var page = CreateFactory().CreateForList();
        using var json = JsonDocument.Parse("""{"amount":50}""");
        (await page.ApplyAsync(guarded, json.RootElement))!.Value.TryGetProperty("amount", out _).ShouldBeFalse();
        (await page.ApplyAsync(later, json.RootElement))!.Value.TryGetProperty("amount", out _).ShouldBeTrue();
        (await page.ApplyAsync(otherDomain, json.RootElement))!.Value.TryGetProperty("amount", out _).ShouldBeTrue();
        await _cache.Received(1).GetSchemaAsync("bank", "schema", "1.0.0", Arg.Any<CancellationToken>());
        await _cache.Received(1).GetSchemaAsync("bank", "schema", "2.0.0", Arg.Any<CancellationToken>());
        await _cache.Received(1).GetSchemaAsync("other", "schema", "1.0.0", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ListScope_RoleResolutionFailureAfterSuccessfulItem_PrunesGuardedFields()
    {
        var workflow = ConfigureWorkflow("bank", "1.0.0", "admin");
        _roles.ResolveRolesAsync(Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(Result<string[]?>.Ok(["admin"]), Result<string[]?>.Fail(Error.Forbidden("unavailable", "Role provider unavailable")));
        var page = CreateFactory().CreateForList();
        using var json = JsonDocument.Parse("""{"amount":50,"public":"safe"}""");
        (await page.ApplyAsync(workflow, json.RootElement))!.Value.TryGetProperty("amount", out _).ShouldBeTrue();
        var denied = await page.ApplyAsync(workflow, json.RootElement);
        denied!.Value.TryGetProperty("amount", out _).ShouldBeFalse();
        denied.Value.GetProperty("public").GetString().ShouldBe("safe");
    }

    private SchemaFieldFilterService CreateFactory() => new(_cache,
        new TransitionAuthorizationManager(_user, Substitute.For<IInstanceTransitionRepository>()), _roles);

    private Definitions.Workflow ConfigureWorkflow(string domain, string version, string role)
    {
        var reference = new Reference("schema", domain, "sys-schemas", version);
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var workflow = JsonSerializer.Deserialize<Definitions.Workflow>(
            "{\"type\":\"F\",\"states\":[],\"schema\":" + JsonSerializer.Serialize(reference) + "}", options)!;
        workflow.SetReference(new Reference("flow", domain, "sys-flows", version));
        var schema = JsonSerializer.Deserialize<SchemaDefinition>(
            "{\"type\":\"workflow\",\"schema\":{\"type\":\"object\",\"properties\":{\"amount\":{\"type\":\"number\",\"x-roles\":[{\"role\":"
            + JsonSerializer.Serialize(role) + ",\"grant\":\"allow\"}]}}}}", options)!;
        schema.SetReference(reference);
        _cache.GetSchemaAsync(domain, "schema", version, Arg.Any<CancellationToken>()).Returns(Result<SchemaDefinition>.Ok(schema));
        return workflow;
    }
}
