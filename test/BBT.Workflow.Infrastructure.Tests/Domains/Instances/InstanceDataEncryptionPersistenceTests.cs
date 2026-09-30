using System.Collections.Generic;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Domain.EntityFrameworkCore;
using BBT.Aether.MultiSchema;
using BBT.Aether.Results;
using BBT.Workflow.Authorization;
using BBT.Workflow.BackgroundJobs.Options;
using BBT.Workflow.Caching;
using BBT.Workflow.Data;
using BBT.Workflow.DataSink;
using BBT.Workflow.Definitions;
using BBT.Workflow.Encryption;
using BBT.Workflow.ExceptionHandling;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using BBT.Workflow.Security;
using BBT.Workflow.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace BBT.Workflow.Domains.Instances;

/// <summary>
/// <c>x-encryption.type: "encrypt"</c> against a real PostgreSQL, through the real write funnel and the real EF
/// materialization interceptor:
/// <list type="bullet">
///   <item>the <c>"Data"</c> column holds the token, the engine's in-memory row the plaintext;</item>
///   <item>a reload opens the token (every query shape goes through the interceptor);</item>
///   <item>the detached retry/fault <c>UpdateAsync</c> never rewrites the column — the hazard that would have
///   put the plaintext back on disk;</item>
///   <item>dedup still recognizes an unchanged document although every seal uses a fresh nonce;</item>
///   <item>a request echoing the stored token is a no-op, a foreign token is refused;</item>
///   <item>a schema that cannot be resolved refuses the write instead of storing plaintext.</item>
/// </list>
/// </summary>
public sealed class InstanceDataEncryptionPersistenceTests : IAsyncLifetime
{
    private const string Flow = "encryption-flow";
    private const string Email = "user@example.com";
    private const string Tckn = "12345678901";

    private const string MasterSchemaJson = """
        {
          "type": "master",
          "schema": {
            "type": "object",
            "properties": {
              "customer": {
                "type": "object",
                "properties": {
                  "email": { "type": "string", "format": "email", "x-encryption": { "type": "encrypt" } },
                  "tckn": { "type": "string", "x-encryption": { "type": "hash" } },
                  "name": { "type": "string" }
                }
              },
              "n": { "type": "integer" }
            }
          }
        }
        """;

    private PostgreSqlContainer _postgres = null!;
    private string _connectionString = null!;
    private InstanceDataProtector _protector = null!;
    private InstanceDataProtectorInterceptor _interceptor = null!;
    private IComponentCacheStore _componentCacheStore = null!;
    private Definitions.Workflow _workflow = null!;

    async Task IAsyncLifetime.InitializeAsync()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("testdb").WithUsername("test").WithPassword("test")
            .Build();
        await _postgres.StartAsync();
        _connectionString = _postgres.GetConnectionString();

        _protector = NewProtector();
        _interceptor = new InstanceDataProtectorInterceptor(_protector);

        var schema = JsonSerializer.Deserialize<SchemaDefinition>(MasterSchemaJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        schema.SetReference(new Reference("encrypted-master", "core", "sys-schemas", "1.0.0"));
        _componentCacheStore = Substitute.For<IComponentCacheStore>();
        _componentCacheStore
            .GetSchemaAsync("core", "encrypted-master", "1.0.0", Arg.Any<CancellationToken>())
            .Returns(Result<SchemaDefinition>.Ok(schema));

        _workflow = Definitions.Workflow.Create();
        _workflow.SetReference(new Reference(Flow, "core", "sys-flows", "1.0.0"));
        _workflow.SetSchema(new Reference("encrypted-master", "core", "sys-schemas", "1.0.0"));

        await using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _postgres.StopAsync();
        await _postgres.DisposeAsync();
    }

    /// <summary>A protector with its own (empty) in-process cache — what a second pod, or a restarted one, has.</summary>
    private InstanceDataProtector NewProtector() => new(new InstanceSecretStore(
        Options.Create(new SchemaEncryptionOptions()),
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = _connectionString })
            .Build()));

    private WorkflowDbContext CreateContext(bool withProtector = true, InstanceDataProtectorInterceptor? interceptor = null)
    {
        // Every test instance builds its own interceptor (and so its own EF service provider); the >20 guard is noise here.
        var builder = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
        if (withProtector)
            builder.AddInterceptors(interceptor ?? _interceptor);
        return new WorkflowDbContext(builder.Options, new StaticCurrentSchema("public"));
    }

    private InstanceDataWriteService CreateService(WorkflowDbContext context, IComponentCacheStore? store = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(store ?? _componentCacheStore);
        var validator = Substitute.For<IJsonSchemaValidator>();
        validator.Validate(Arg.Any<JsonElement>(), Arg.Any<JsonElement?>()).Returns(Result.Ok());
        return new InstanceDataWriteService(
            new FixedDbContextProvider(context),
            services.BuildServiceProvider(),
            validator,
            Options.Create(new WorkflowExecutionOptions()),
            NullLogger<InstanceDataWriteService>.Instance,
            _protector);
    }

    private async Task<Instance> CreateInstanceAsync()
    {
        await using var ctx = CreateContext();
        var instance = Instance.Create(Guid.NewGuid(), Flow, "1.0.0");
        ctx.Instances.Add(instance);
        await ctx.SaveChangesAsync();
        return instance;
    }

    private async Task<string> RawColumnAsync(Guid instanceId)
    {
        await using var ctx = CreateContext(withProtector: false);
        return await ctx.Database
            .SqlQueryRaw<string>("SELECT \"Data\"::text AS \"Value\" FROM \"public\".\"InstancesData\" WHERE \"InstanceId\" = {0} AND \"IsLatest\"", instanceId)
            .SingleAsync();
    }

    private static string? At(JsonElement root, string path) => InstanceDataProtector.TryGetString(root, path);

    [Fact]
    public async Task TheColumnHoldsTheToken_TheEngineHoldsThePlaintext()
    {
        var instance = await CreateInstanceAsync();
        await using var ctx = CreateContext();

        var row = await CreateService(ctx).AppendAsync(
            instance, new JsonData($$$"""{"customer":{"email":"{{{Email}}}","name":"Ayşe"}}"""), null, CancellationToken.None, _workflow);

        row.ShouldNotBeNull();
        At(row.Data.JsonElement, "customer.email").ShouldBe(Email);
        row.StoredTokens.ShouldContainKey("customer.email");

        var raw = await RawColumnAsync(instance.Id);
        raw.ShouldNotContain(Email);
        raw.ShouldContain("ENCRYPTED:AES256:i1:");
        raw.ShouldContain("Ayşe");
        row.DataHash.ShouldNotBe(InstanceData.ComputeDataHash(row.Data)); // keyed, not the plaintext SHA-1
    }

    [Fact]
    public async Task AReload_OpensTheToken_ForTrackedAndNoTrackingQueries()
    {
        var instance = await CreateInstanceAsync();
        await using (var ctx = CreateContext())
            await CreateService(ctx).AppendAsync(instance, new JsonData($$$"""{"customer":{"email":"{{{Email}}}"}}"""), null, CancellationToken.None, _workflow);

        await using var read = CreateContext();
        var tracked = await read.Instances.Include(i => i.DataList).SingleAsync(i => i.Id == instance.Id);
        At(tracked.LatestData!.Data.JsonElement, "customer.email").ShouldBe(Email);

        var detached = await read.InstancesData.AsNoTracking().SingleAsync(d => d.InstanceId == instance.Id && d.IsLatest);
        At(detached.Data.JsonElement, "customer.email").ShouldBe(Email);
        detached.StoredTokens["customer.email"].ShouldStartWith("ENCRYPTED:AES256:i1:");
        detached.UndecryptablePaths.ShouldBeEmpty();
    }

    [Fact]
    public async Task WithoutAProtector_ALoadedRowShowsTheTokenNeverThePlaintext()
    {
        var instance = await CreateInstanceAsync();
        await using (var ctx = CreateContext())
            await CreateService(ctx).AppendAsync(instance, new JsonData($$$"""{"customer":{"email":"{{{Email}}}"}}"""), null, CancellationToken.None, _workflow);

        await using var read = CreateContext(withProtector: false);
        var row = await read.InstancesData.AsNoTracking().SingleAsync(d => d.InstanceId == instance.Id && d.IsLatest);

        At(row.Data.JsonElement, "customer.email").ShouldStartWith("ENCRYPTED:AES256:");
    }

    [Fact]
    public async Task TheDetachedRetryUpdate_NeverRewritesTheColumn()
    {
        var instance = await CreateInstanceAsync();
        await using (var ctx = CreateContext())
            await CreateService(ctx).AppendAsync(instance, new JsonData($$$"""{"customer":{"email":"{{{Email}}}"}}"""), null, CancellationToken.None, _workflow);
        var before = await RawColumnAsync(instance.Id);

        // Mirrors InstanceRetryAppService / MarkInstanceFaultedAsync: loaded in one scope, updated in another.
        Instance loaded;
        await using (var loadCtx = CreateContext())
        {
            loaded = await loadCtx.Instances.AsNoTracking().Include(i => i.DataList).SingleAsync(i => i.Id == instance.Id);
            At(loaded.LatestData!.Data.JsonElement, "customer.email").ShouldBe(Email); // plaintext in memory
        }

        await using (var updateCtx = CreateContext())
        {
            await CreateRepository(updateCtx).UpdateAsync(loaded, autoSave: false);
            await updateCtx.SaveChangesAsync();
        }

        (await RawColumnAsync(instance.Id)).ShouldBe(before);
    }

    [Fact]
    public async Task AnUnchangedDocument_IsDeduplicated_DespiteTheFreshNonce()
    {
        var instance = await CreateInstanceAsync();
        var body = new JsonData($$$"""{"customer":{"email":"{{{Email}}}"},"n":1}""");

        await using (var ctx = CreateContext())
            (await CreateService(ctx).AppendAsync(instance, body, null, CancellationToken.None, _workflow)).ShouldNotBeNull();
        await using (var ctx = CreateContext())
            (await CreateService(ctx).AppendAsync(instance, body, null, CancellationToken.None, _workflow)).ShouldBeNull();

        await using var verify = CreateContext();
        (await verify.InstancesData.CountAsync(d => d.InstanceId == instance.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task AnUnchangedValue_KeepsItsToken_WhenAnotherFieldChanges()
    {
        var instance = await CreateInstanceAsync();
        await using (var ctx = CreateContext())
            await CreateService(ctx).AppendAsync(instance, new JsonData($$$"""{"customer":{"email":"{{{Email}}}"},"n":1}"""), null, CancellationToken.None, _workflow);
        var first = At(JsonDocument.Parse(await RawColumnAsync(instance.Id)).RootElement, "customer.email");

        await using (var ctx = CreateContext())
            await CreateService(ctx).AppendAsync(instance, new JsonData("""{"n":2}"""), null, CancellationToken.None, _workflow);
        var second = At(JsonDocument.Parse(await RawColumnAsync(instance.Id)).RootElement, "customer.email");

        second.ShouldBe(first);
    }

    [Fact]
    public async Task EchoingTheStoredToken_IsANoOp_AndAForeignTokenIsRefused()
    {
        var instance = await CreateInstanceAsync();
        await using (var ctx = CreateContext())
            await CreateService(ctx).AppendAsync(instance, new JsonData($$$"""{"customer":{"email":"{{{Email}}}"},"n":1}"""), null, CancellationToken.None, _workflow);
        var token = At(JsonDocument.Parse(await RawColumnAsync(instance.Id)).RootElement, "customer.email")!;

        // A client that read the token and posts the form back unchanged.
        await using (var ctx = CreateContext())
            (await CreateService(ctx).AppendAsync(instance, new JsonData($$$"""{"customer":{"email":"{{{token}}}"},"n":1}"""), null, CancellationToken.None, _workflow))
                .ShouldBeNull();

        var other = await CreateInstanceAsync();
        await using (var ctx = CreateContext())
        {
            await Should.ThrowAsync<EncryptedValueReservedException>(() => CreateService(ctx).AppendAsync(
                other, new JsonData($$$"""{"customer":{"email":"{{{token}}}"}}"""), null, CancellationToken.None, _workflow));
        }
    }

    /// <summary>
    /// An instance whose head already carries x-encryption values is known to use them: without its schema the write cannot
    /// know which paths to protect, so it is refused rather than stored in plaintext. (An instance with no protected values
    /// yet keeps the old behaviour — validation skipped, write accepted.)
    /// </summary>
    [Fact]
    public async Task AnUnresolvableSchema_RefusesTheWrite_OnceTheInstanceCarriesProtectedValues()
    {
        var instance = await CreateInstanceAsync();
        await using (var ctx = CreateContext())
            await CreateService(ctx).AppendAsync(instance, new JsonData($$$"""{"customer":{"email":"{{{Email}}}"}}"""), null, CancellationToken.None, _workflow);

        var failing = Substitute.For<IComponentCacheStore>();
        failing.GetSchemaAsync("core", "encrypted-master", "1.0.0", Arg.Any<CancellationToken>())
            .Returns(Result<SchemaDefinition>.Fail(Error.Failure("Cache:Down", "store unavailable")));

        await using var write = CreateContext();
        await Should.ThrowAsync<EncryptionSchemaUnavailableException>(() => CreateService(write, failing).AppendAsync(
            instance, new JsonData("""{"n":5}"""), null, CancellationToken.None, _workflow));

        var fresh = await CreateInstanceAsync();
        await using var other = CreateContext();
        (await CreateService(other, failing).AppendAsync(fresh, new JsonData("""{"n":1}"""), null, CancellationToken.None, _workflow))
            .ShouldNotBeNull();
    }

    // ── per-instance secrets and hash-on-write ───────────────────────────────

    [Fact]
    public async Task TheFirstProtectedWrite_CreatesExactlyOneSecretRow_EvenUnderConcurrentFirstWrites()
    {
        var instance = await CreateInstanceAsync();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            await using var ctx = CreateContext();
            var copy = Instance.Create(instance.Id, instance.Flow, instance.FlowVersion);
            await CreateService(ctx).AppendAsync(copy, new JsonData($$$"""{"customer":{"email":"{{{Email}}}"},"n":{{{i}}}}"""),
                null, CancellationToken.None, _workflow);
        }));

        await using var verify = CreateContext();
        (await verify.InstanceSecrets.CountAsync(x => x.InstanceId == instance.Id)).ShouldBe(1);
        var raw = await RawColumnAsync(instance.Id);
        raw.ShouldContain("ENCRYPTED:AES256:i1:");
        At(JsonDocument.Parse(raw).RootElement, "customer.email").ShouldNotBe(Email);
    }

    /// <summary>
    /// GetOrCreate is one statement (insert-or-read CTE). The first call takes the insert branch, every later call the
    /// read branch; both must return the same persisted secret — also from a store with an empty cache (another pod).
    /// </summary>
    [Fact]
    public async Task GetOrCreate_InsertsOnce_AndLaterCallsReadTheSameRow()
    {
        var instance = await CreateInstanceAsync();
        var store = new InstanceSecretStore(Options.Create(new SchemaEncryptionOptions()));

        await using var first = CreateContext();
        var created = await store.GetOrCreateAsync(first, instance.Id, CancellationToken.None);

        await using var second = CreateContext();
        var again = await new InstanceSecretStore(Options.Create(new SchemaEncryptionOptions()))
            .GetOrCreateAsync(second, instance.Id, CancellationToken.None);

        again.EncryptionKey.ShouldBe(created.EncryptionKey);
        again.HashSalt.ShouldBe(created.HashSalt);
        created.EncryptionKey.Length.ShouldBe(32);
        await using var verify = CreateContext();
        (await verify.InstanceSecrets.CountAsync(x => x.InstanceId == instance.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task AHashPath_IsStoredAsTheDigest_AndTheEngineSeesTheDigestToo()
    {
        var instance = await CreateInstanceAsync();
        await using var ctx = CreateContext();

        var row = await CreateService(ctx).AppendAsync(
            instance, new JsonData($$$"""{"customer":{"tckn":"{{{Tckn}}}","name":"A"}}"""), null, CancellationToken.None, _workflow);

        var digest = At(row!.Data.JsonElement, "customer.tckn")!;
        digest.ShouldStartWith("HASHED:SHA256:");
        (await RawColumnAsync(instance.Id)).ShouldNotContain(Tckn);
        At(JsonDocument.Parse(await RawColumnAsync(instance.Id)).RootElement, "customer.tckn").ShouldBe(digest);
    }

    [Fact]
    public async Task AnUnchangedHashedValue_IsDeduplicated_AndKeepsItsDigest()
    {
        var instance = await CreateInstanceAsync();
        var body = new JsonData($$$"""{"customer":{"tckn":"{{{Tckn}}}"}}""");

        await using (var ctx = CreateContext())
            (await CreateService(ctx).AppendAsync(instance, body, null, CancellationToken.None, _workflow)).ShouldNotBeNull();
        await using (var ctx = CreateContext())
            (await CreateService(ctx).AppendAsync(instance, body, null, CancellationToken.None, _workflow)).ShouldBeNull();
    }

    [Fact]
    public async Task TwoInstances_HashTheSameValueDifferently()
    {
        var a = await CreateInstanceAsync();
        var b = await CreateInstanceAsync();
        var body = new JsonData($$$"""{"customer":{"tckn":"{{{Tckn}}}"}}""");

        await using (var ctx = CreateContext())
            await CreateService(ctx).AppendAsync(a, body, null, CancellationToken.None, _workflow);
        await using (var ctx = CreateContext())
            await CreateService(ctx).AppendAsync(b, body, null, CancellationToken.None, _workflow);

        At(JsonDocument.Parse(await RawColumnAsync(a.Id)).RootElement, "customer.tckn")
            .ShouldNotBe(At(JsonDocument.Parse(await RawColumnAsync(b.Id)).RootElement, "customer.tckn"));
    }

    /// <summary>A pod that never saw the instance opens its tokens through the synchronous fallback (no preload).</summary>
    [Fact]
    public async Task AColdProtector_OpensTokensThroughTheFallbackLookup()
    {
        var instance = await CreateInstanceAsync();
        await using (var ctx = CreateContext())
            await CreateService(ctx).AppendAsync(instance, new JsonData($$$"""{"customer":{"email":"{{{Email}}}"}}"""), null, CancellationToken.None, _workflow);

        var cold = new InstanceDataProtectorInterceptor(NewProtector());
        await using var read = CreateContext(interceptor: cold);
        var row = await read.InstancesData.AsNoTracking().SingleAsync(d => d.InstanceId == instance.Id && d.IsLatest);

        At(row.Data.JsonElement, "customer.email").ShouldBe(Email);
    }

    /// <summary>Deleting the instance deletes its secret; deleting only the secret leaves the tokens unopenable.</summary>
    [Fact]
    public async Task DeletingTheSecret_CryptoShredsTheEncryptedValues()
    {
        var instance = await CreateInstanceAsync();
        await using (var ctx = CreateContext())
            await CreateService(ctx).AppendAsync(instance, new JsonData($$$"""{"customer":{"email":"{{{Email}}}"}}"""), null, CancellationToken.None, _workflow);

        await using (var ctx = CreateContext(withProtector: false))
            await ctx.Database.ExecuteSqlRawAsync("DELETE FROM \"public\".\"InstanceSecrets\" WHERE \"InstanceId\" = {0}", instance.Id);

        var cold = new InstanceDataProtectorInterceptor(NewProtector());
        await using var read = CreateContext(interceptor: cold);
        var row = await read.InstancesData.AsNoTracking().SingleAsync(d => d.InstanceId == instance.Id && d.IsLatest);

        row.UndecryptablePaths.ShouldContain("customer.email");
        row.Data.Json.ShouldNotContain(Email);
    }

    [Fact]
    public async Task DeletingTheInstance_CascadesToItsSecret()
    {
        var instance = await CreateInstanceAsync();
        await using (var ctx = CreateContext())
            await CreateService(ctx).AppendAsync(instance, new JsonData($$$"""{"customer":{"email":"{{{Email}}}"}}"""), null, CancellationToken.None, _workflow);

        await using (var ctx = CreateContext(withProtector: false))
            await ctx.Database.ExecuteSqlRawAsync("DELETE FROM \"public\".\"Instances\" WHERE \"Id\" = {0}", instance.Id);

        await using var verify = CreateContext(withProtector: false);
        (await verify.InstanceSecrets.CountAsync(x => x.InstanceId == instance.Id)).ShouldBe(0);
    }

    private static EfCoreInstanceRepository CreateRepository(WorkflowDbContext context) => new(
        new FixedDbContextProvider(context),
        new ServiceCollection().BuildServiceProvider(),
        Substitute.For<IRuntimeInfoProvider>(),
        Substitute.For<IDataSinkManager>(),
        new StaticCurrentSchema("public"),
        Substitute.For<ISchemaValidator>(),
        Options.Create(new WorkflowExecutionOptions()),
        NullLogger<EfCoreInstanceRepository>.Instance);

    private sealed class FixedDbContextProvider(WorkflowDbContext context)
        : IAetherDbContextProvider<WorkflowDbContext>
    {
        public Task<WorkflowDbContext> GetDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(context);
    }
}
