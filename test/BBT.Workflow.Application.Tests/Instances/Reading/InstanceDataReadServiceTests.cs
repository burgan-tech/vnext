using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Authorization;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Instances.Reading;

/// <summary>
/// <see cref="InstanceDataReadService"/>: the exposure pass's result is served when it applied, and the STORED form —
/// never the plaintext view — when it did not. Instance GET used to serve the stored form unguarded, so the guarded read
/// must never be worse than that for an encrypt field.
/// </summary>
public sealed class InstanceDataReadServiceTests
{
    private const string Token = "ENCRYPTED:AES256:i1:c3RvcmVkLXRva2Vu";

    private static (Instance Instance, InstanceData Row) ProtectedRow()
    {
        var instance = InstanceFactory.CreateDefault("read-guard");
        var row = instance.SeedStoredData($$"""{ "email": "{{Token}}", "label": "plain" }""");
        return (instance, row);
    }

    /// <summary>
    /// The row is opened on demand: the filter receives the plaintext and the stored tokens, so it can serve the opened
    /// value to an exempt caller and the token to everyone else. A row without tokens is never opened.
    /// </summary>
    [Fact]
    public async Task AProtectedRow_IsOpenedForTheFilter_AndAPlainRowIsNot()
    {
        var (instance, row) = ProtectedRow();
        var protector = Substitute.For<IInstanceDataProtector>();
        protector.UnprotectAsync(Arg.Any<string?>(), instance.Id, Arg.Any<JsonData>(), Arg.Any<CancellationToken>())
            .Returns(new InstanceDataView(new JsonData("""{ "email": "user@example.com", "label": "plain" }"""),
                new Dictionary<string, string> { ["email"] = Token }));
        JsonElement? seen = null;
        IReadOnlyDictionary<string, string>? seenTokens = null;
        var filter = Substitute.For<ISchemaFieldFilterService>();
        filter.ApplyAsync(Arg.Any<Definitions.Workflow?>(), Arg.Any<JsonElement?>(), Arg.Any<Instance?>(),
                Arg.Any<AuthorizationRequestContext?>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyDictionary<string, string>?>())
            .Returns(call =>
            {
                seen = call.ArgAt<JsonElement?>(1);
                seenTokens = call.ArgAt<IReadOnlyDictionary<string, string>?>(5);
                return seen;
            });
        var service = new InstanceDataReadService(filter, protector: protector);

        var exposed = await service.ExposeAsync(null, instance, row, null);

        seen!.Value.GetProperty("email").GetString().ShouldBe("user@example.com");
        seenTokens!["email"].ShouldBe(Token);
        exposed!.Value.GetProperty("email").GetString().ShouldBe("user@example.com");

        var plain = InstanceFactory.CreateDefault("plain");
        await service.ExposeAsync(null, plain, plain.SeedData(Guid.NewGuid(), new JsonData("""{ "label": "plain" }""")), null);
        await protector.Received(1).UnprotectAsync(
            Arg.Any<string?>(), Arg.Any<Guid>(), Arg.Any<JsonData>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NothingApplied_ServesTheStoredForm_NeverThePlaintext()
    {
        var (instance, row) = ProtectedRow();
        var filter = Substitute.For<ISchemaFieldFilterService>(); // returns null: nothing applied

        var exposed = await new InstanceDataReadService(filter).ExposeAsync(null, instance, row, null);

        exposed!.Value.GetProperty("email").GetString().ShouldBe(Token);
        exposed.Value.GetProperty("label").GetString().ShouldBe("plain");
    }

    [Fact]
    public async Task AnAppliedExposure_IsServedAsItIs()
    {
        var (instance, row) = ProtectedRow();
        var filtered = JsonSerializer.SerializeToElement(new { label = "plain" });
        var filter = Substitute.For<ISchemaFieldFilterService>();
        filter.ApplyAsync(Arg.Any<Definitions.Workflow?>(), Arg.Any<JsonElement?>(), Arg.Any<Instance?>(),
                Arg.Any<AuthorizationRequestContext?>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyDictionary<string, string>?>())
            .Returns(filtered);

        var exposed = await new InstanceDataReadService(filter).ExposeAsync(null, instance, row, null);

        exposed!.Value.TryGetProperty("email", out _).ShouldBeFalse();
        exposed.Value.GetProperty("label").GetString().ShouldBe("plain");
    }

    /// <summary>The real guard: a schema that cannot be read leaves the token in place instead of the plaintext.</summary>
    [Fact]
    public async Task ASchemaThatCannotBeRead_ServesTheToken()
    {
        var (instance, row) = ProtectedRow();
        var cache = Substitute.For<IComponentCacheStore>();
        cache.GetSchemaAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<SchemaDefinition>.Fail(Error.NotFound("schema", "gone")));
        var filter = new SchemaFieldFilterService(cache, Substitute.For<ITransitionAuthorizationManager>(),
            Substitute.For<ICallerRoleResolver>(), new FakeFieldMaskingEngine(), Options.Create(new SchemaMaskingOptions()));

        var exposed = await new InstanceDataReadService(filter).ExposeAsync(WorkflowWithSchema(), instance, row, null);

        exposed!.Value.GetProperty("email").GetString().ShouldBe(Token);
    }

    [Fact]
    public async Task NoRow_IsNull()
        => (await new InstanceDataReadService(Substitute.For<ISchemaFieldFilterService>())
            .ExposeAsync(null, InstanceFactory.CreateDefault("empty"), null, null)).ShouldBeNull();

    /// <summary>A list page preloads the secrets of its protected rows once, in one call, and nothing for plain rows.</summary>
    [Fact]
    public async Task APageReader_PreloadsTheProtectedRowsOnce()
    {
        var (protectedInstance, protectedRow) = ProtectedRow();
        var plainInstance = InstanceFactory.CreateDefault("plain");
        var plainRow = plainInstance.SeedData(Guid.NewGuid(), new JsonData("""{ "label": "plain" }"""));
        var preloader = Substitute.For<IInstanceSecretPreloader>();
        var reader = new InstanceDataReadService(Substitute.For<ISchemaFieldFilterService>(), preloader)
            .CreatePageReader([protectedInstance, plainInstance]);

        await reader.ExposeAsync(null, protectedInstance, protectedRow, null);
        await reader.ExposeAsync(null, plainInstance, plainRow, null);

        await preloader.Received(1).PreloadAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Single() == protectedInstance.Id),
            Arg.Any<CancellationToken>());
    }

    private static Definitions.Workflow WorkflowWithSchema()
    {
        var reference = new Reference("schema", "bank", "sys-schemas", "1.0.0");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var workflow = JsonSerializer.Deserialize<Definitions.Workflow>(
            "{\"type\":\"F\",\"states\":[],\"schema\":" + JsonSerializer.Serialize(reference) + "}", options)!;
        workflow.SetReference(new Reference("flow", "bank", "sys-flows", "1.0.0"));
        return workflow;
    }
}
