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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace BBT.Workflow.Domains.Instances;

/// <summary>
/// <c>x-encryption.type: "encrypt"</c> against a real PostgreSQL, through the real write funnel:
/// <list type="bullet">
///   <item><c>InstanceData.Data</c> is the <c>"Data"</c> column as stored — the token — in memory and after a reload;
///   nothing decrypts it in place, and the model needs no migration;</item>
///   <item>the protector opens it on demand, loading a secret it has not cached through EF (a fresh pod);</item>
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
        Options.Create(new SchemaEncryptionOptions()), SecretLookupScopes()));

    /// <summary>
    /// What the store's EF lookup resolves per call: a fresh scope with its own context, schema and (non-transactional)
    /// unit of work — never the caller's context.
    /// </summary>
    private IServiceScopeFactory SecretLookupScopes()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<BBT.Aether.Uow.IUnitOfWorkManager>());
        services.AddScoped(_ => Substitute.For<ICurrentSchema>());
        services.AddScoped<IAetherDbContextProvider<WorkflowDbContext>>(_ => new OwnedDbContextProvider(CreateContext()));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private WorkflowDbContext CreateContext()
    {
        var builder = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseNpgsql(_connectionString)
            .ConfigureWarnings(w => w.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
        return new WorkflowDbContext(builder.Options, new StaticCurrentSchema("public"));
    }

    private Task<InstanceDataView> OpenAsync(InstanceDataProtector protector, InstanceData row, CancellationToken ct = default)
        => protector.UnprotectAsync("public", row.InstanceId, row.Data, ct);

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
        await using var ctx = CreateContext();
        return await ctx.Database
            .SqlQueryRaw<string>("SELECT \"Data\"::text AS \"Value\" FROM \"public\".\"InstancesData\" WHERE \"InstanceId\" = {0} AND \"IsLatest\"", instanceId)
            .SingleAsync();
    }

    private static string? At(JsonElement root, string path) => InstanceDataProtector.TryGetString(root, path);

    [Fact]
    public async Task TheColumnAndTheWrittenRow_HoldTheToken_AndTheProtectorOpensIt()
    {
        var instance = await CreateInstanceAsync();
        await using var ctx = CreateContext();

        var row = await CreateService(ctx).AppendAsync(
            instance, new JsonData($$$"""{"customer":{"email":"{{{Email}}}","name":"Ayşe"}}"""), null, CancellationToken.None, _workflow);

        row.ShouldNotBeNull();
        At(row.Data.JsonElement, "customer.email").ShouldStartWith("ENCRYPTED:AES256:i1:");
        var opened = await OpenAsync(_protector, row);
        At(opened.Plain.JsonElement, "customer.email").ShouldBe(Email);
        opened.Tokens.ShouldContainKey("customer.email");

        var raw = await RawColumnAsync(instance.Id);
        raw.ShouldNotContain(Email);
        raw.ShouldContain("ENCRYPTED:AES256:i1:");
        raw.ShouldContain("Ayşe");
        At(JsonDocument.Parse(raw).RootElement, "customer.email").ShouldBe(At(row.Data.JsonElement, "customer.email"));
        row.DataHash.ShouldNotBe(InstanceData.ComputeDataHash(opened.Plain)); // keyed, not the plaintext SHA-1
    }

    /// <summary>
    /// <c>Data</c> is the mapped column again, under its old name and column, so the model the migrations describe is
    /// exactly the model in code: no migration is needed for the storage-model change.
    /// </summary>
    [Fact]
    public async Task TheModel_HasNoPendingChanges()
    {
        await using var ctx = CreateContext();
        ctx.Database.HasPendingModelChanges().ShouldBeFalse();
    }

    [Fact]
    public async Task AReload_ShowsTheToken_ForTrackedAndNoTrackingQueries_AndTheProtectorOpensIt()
    {
        var instance = await CreateInstanceAsync();
        await using (var ctx = CreateContext())
            await CreateService(ctx).AppendAsync(instance, new JsonData($$$"""{"customer":{"email":"{{{Email}}}"}}"""), null, CancellationToken.None, _workflow);

        await using var read = CreateContext();
        var tracked = await read.Instances.Include(i => i.DataList).SingleAsync(i => i.Id == instance.Id);
        At(tracked.LatestData!.Data.JsonElement, "customer.email").ShouldStartWith("ENCRYPTED:AES256:i1:");
        ((string)tracked.Data!.customer.email).ShouldStartWith("ENCRYPTED:AES256:i1:");

        var detached = await read.InstancesData.AsNoTracking().SingleAsync(d => d.InstanceId == instance.Id && d.IsLatest);
        At(detached.Data.JsonElement, "customer.email").ShouldStartWith("ENCRYPTED:AES256:i1:");

        var opened = await OpenAsync(_protector, detached);
        At(opened.Plain.JsonElement, "customer.email").ShouldBe(Email);
        opened.Undecryptable.ShouldBeEmpty();
    }

    /// <summary>
    /// A script's <c>DecryptAsync</c> on a row a fresh pod has not opened yet: the secret is loaded through EF in its own
    /// scope (not the read DbContext), a cancelled call throws and caches nothing, and the next call opens the value. The
    /// script snapshot shows the token meanwhile.
    /// </summary>
    [Fact]
    public async Task DecryptAsync_OnAFreshPod_LoadsTheSecretAsynchronously_AndACancelledCallCachesNothing()
    {
        var instance = await CreateInstanceAsync();
        await using (var ctx = CreateContext())
            await CreateService(ctx).AppendAsync(instance, new JsonData($$$"""{"customer":{"email":"{{{Email}}}"}}"""), null, CancellationToken.None, _workflow);

        var freshPod = NewProtector();
        await using var read = CreateContext();
        var loaded = await read.Instances.AsNoTracking().Include(i => i.DataList).SingleAsync(i => i.Id == instance.Id);
        var script = loaded.CreateSnapshot();
        script.BindDecryption(freshPod, "public");

        ((string)script.Data!.customer.email).ShouldStartWith("ENCRYPTED:AES256:i1:");

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(() => script.DecryptAsync("customer.email", cancelled.Token));
        freshPod.Secrets.TryGetCached("public", instance.Id).ShouldBeNull();

        (await script.DecryptAsync("customer.email")).ShouldBe(Email);
        freshPod.Secrets.TryGetCached("public", instance.Id).ShouldNotBeNull();
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
            At(loaded.LatestData!.Data.JsonElement, "customer.email").ShouldStartWith("ENCRYPTED:AES256:i1:");
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

    /// <summary>A pod that never saw the instance loads the secret through EF (no preload) and caches it.</summary>
    [Fact]
    public async Task AColdProtector_LoadsTheSecretThroughEf_AndCachesIt()
    {
        var instance = await CreateInstanceAsync();
        await using (var ctx = CreateContext())
            await CreateService(ctx).AppendAsync(instance, new JsonData($$$"""{"customer":{"email":"{{{Email}}}"}}"""), null, CancellationToken.None, _workflow);

        var cold = NewProtector();
        await using var read = CreateContext();
        var row = await read.InstancesData.AsNoTracking().SingleAsync(d => d.InstanceId == instance.Id && d.IsLatest);

        At((await OpenAsync(cold, row)).Plain.JsonElement, "customer.email").ShouldBe(Email);
        cold.Secrets.TryGetCached("public", instance.Id).ShouldNotBeNull();
    }

    /// <summary>Deleting the instance deletes its secret; deleting only the secret leaves the tokens unopenable.</summary>
    [Fact]
    public async Task DeletingTheSecret_CryptoShredsTheEncryptedValues()
    {
        var instance = await CreateInstanceAsync();
        await using (var ctx = CreateContext())
            await CreateService(ctx).AppendAsync(instance, new JsonData($$$"""{"customer":{"email":"{{{Email}}}"}}"""), null, CancellationToken.None, _workflow);

        await using (var ctx = CreateContext())
            await ctx.Database.ExecuteSqlRawAsync("DELETE FROM \"public\".\"InstanceSecrets\" WHERE \"InstanceId\" = {0}", instance.Id);

        await using var read = CreateContext();
        var row = await read.InstancesData.AsNoTracking().SingleAsync(d => d.InstanceId == instance.Id && d.IsLatest);
        var opened = await OpenAsync(NewProtector(), row);

        opened.Undecryptable.ShouldContain("customer.email");
        opened.Plain.Json.ShouldNotContain(Email);
    }

    [Fact]
    public async Task DeletingTheInstance_CascadesToItsSecret()
    {
        var instance = await CreateInstanceAsync();
        await using (var ctx = CreateContext())
            await CreateService(ctx).AppendAsync(instance, new JsonData($$$"""{"customer":{"email":"{{{Email}}}"}}"""), null, CancellationToken.None, _workflow);

        await using (var ctx = CreateContext())
            await ctx.Database.ExecuteSqlRawAsync("DELETE FROM \"public\".\"Instances\" WHERE \"Id\" = {0}", instance.Id);

        await using var verify = CreateContext();
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

    /// <summary>A scope's own context, disposed with the scope.</summary>
    private sealed class OwnedDbContextProvider(WorkflowDbContext context)
        : IAetherDbContextProvider<WorkflowDbContext>, IDisposable, IAsyncDisposable
    {
        public Task<WorkflowDbContext> GetDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(context);

        public void Dispose() => context.Dispose();

        public ValueTask DisposeAsync() => context.DisposeAsync();
    }

    private sealed class FixedDbContextProvider(WorkflowDbContext context)
        : IAetherDbContextProvider<WorkflowDbContext>
    {
        public Task<WorkflowDbContext> GetDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(context);
    }
}
