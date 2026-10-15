using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Authorization;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Files;

/// <summary>
/// Covers <see cref="InstanceFileAppService"/>: the handle is looked up only in the instance's latest data at the
/// schema's x-storage paths; <c>If-None-Match</c> short-circuits the store; with an authorization context the state
/// queryRoles (403) and the path's x-roles (hidden → 404) apply; without one neither runs.
/// </summary>
public sealed class InstanceFileAppServiceTests
{
    private const string Domain = "core";
    private const string Flow = "kyc";
    private const string Binding = "vnext-blob-local";
    private const string ETag = "abc123";

    private const string MasterSchema = """
        { "type": "object", "properties": {
            "passport": { "type": "object", "x-storage": { "binding": "vnext-blob-local" } },
            "docs": { "type": "object", "properties": {
                "tax": { "type": "object", "x-storage": { "binding": "vnext-blob-local" } } } },
            "files": { "type": "array", "items": { "type": "object", "x-storage": { "binding": "vnext-blob-local" } } } } }
        """;

    private readonly IInstanceRepository _repository = Substitute.For<IInstanceRepository>();
    private readonly IComponentCacheStore _cache = Substitute.For<IComponentCacheStore>();
    private readonly IFileOffloadService _offload = Substitute.For<IFileOffloadService>();
    private readonly IFileBlobStore _store = Substitute.For<IFileBlobStore>();
    private readonly ICallerRoleResolver _roles = Substitute.For<ICallerRoleResolver>();
    private readonly ITransitionAuthorizationManager _authz = Substitute.For<ITransitionAuthorizationManager>();
    private readonly IRoleGrantEvaluator _evaluator = Substitute.For<IRoleGrantEvaluator>();
    private readonly Definitions.Workflow _workflow;
    private readonly Instance _instance;
    private readonly InstanceFileAppService _sut;

    private static readonly byte[] Bytes = [1, 2, 3];

    private const string F1 = "0b9f6f3e-1c1a-4a7e-9c55-3f1d2a6b7c80";
    private const string FA = "1a1a1a1a-0000-4000-8000-00000000000a";
    private const string FB = "1b1b1b1b-0000-4000-8000-00000000000b";
    private const string FAbc = "abcabcab-0000-4000-8000-000000000abc";
    private const string FOld = "01d01d01-0000-4000-8000-000000000001";
    private const string FNew = "0e00e00e-0000-4000-8000-000000000002";

    private readonly IRuntimeInfoProvider _runtime = Substitute.For<IRuntimeInfoProvider>();
    private FileStorageOptions _options = new();

    public InstanceFileAppServiceTests() : this(MasterSchema) { }

    private InstanceFileAppServiceTests(string masterSchema)
    {
        _workflow = Definitions.Workflow.Create();
        _workflow.SetReference(new Reference(Flow, Domain, "sys-flows", "1.0.0"));
        _workflow.SetType("F");
        _workflow.SetSchema(new Reference("master", Domain, "sys-schemas", "1.0.0"));
        _cache.GetFlowAsync(Domain, Flow, "1.0.0", Arg.Any<CancellationToken>())
            .Returns(Result<Definitions.Workflow>.Ok(_workflow));
        UseSchema(masterSchema);

        _instance = Instance.Create(Guid.NewGuid(), Flow, "1.0.0", key: "customer-1");
        _repository.FindByIdentifierAsReadOnlyAsync(_instance.Id.ToString(), Arg.Any<CancellationToken>())
            .Returns(_instance);

        _store.GetAsync(Binding, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Result<byte[]>.Ok(Bytes));
        _roles.ResolveRolesAsync(Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(Result<string[]?>.Ok(["customer"]));
        _authz.IsQueryAllowedAsync(default!, default!, default, default, default).ReturnsForAnyArgs(true);
        _authz.CreateEvaluatorAsync(default, default, default, default!, default).ReturnsForAnyArgs(_evaluator);
        _evaluator.IsAnyRoleAllowed(default, default!, default).ReturnsForAnyArgs(true);

        _sut = CreateSut();
    }

    private InstanceFileAppService CreateSut()
    {
        _runtime.When(r => r.Check(Arg.Is<string>(d => d != Domain)))
            .Do(ci => throw new BBT.Workflow.ExceptionHandling.NotFoundDomainException(ci.Arg<string>(), Domain));
        return new InstanceFileAppService(
            _repository, _cache, _offload, _store, _roles, _authz, _runtime,
            Microsoft.Extensions.Options.Options.Create(_options), NullLogger<InstanceFileAppService>.Instance);
    }

    private void UseSchema(string masterSchema)
    {
        var schema = JsonSerializer.Deserialize<SchemaDefinition>(
            $$"""{ "type": "JSON", "schema": {{masterSchema}} }""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        schema.SetReference(new Reference("master", Domain, "sys-schemas", "1.0.0"));
        _cache.GetSchemaAsync(Domain, "master", "1.0.0", Arg.Any<CancellationToken>())
            .Returns(Result<SchemaDefinition>.Ok(schema));
        _offload.GetFieldsAsync(Arg.Any<Definitions.Workflow>(), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<FileStorageField>>.Ok(FileStorageSchemaParser.Parse(schema.Schema)));
    }

    private string Handle(string file) =>
        $$"""{ "component": "{{Binding}}", "file": "{{file}}", "name": "p.pdf", "mimeType": "application/pdf", "size": 3, "eTag": "{{ETag}}", "owner": { "domain": "{{Domain}}", "flow": "{{Flow}}", "instance": "{{_instance.Id}}" } }""";

    private InstanceFileRequest Request(string file, string? ifNoneMatch = null, AuthorizationRequestContext? auth = null)
        => new(Domain, Flow, _instance.Id.ToString(), file, ifNoneMatch, auth);

    private static readonly AuthorizationRequestContext Caller =
        new(new Dictionary<string, string?> { ["role"] = "customer" });

    [Fact]
    public async Task FileInLatestData_ReturnsBytesFromTheStoreAndTheHandle()
    {
        Seed("1.0.0", $$"""{ "passport": {{Handle(F1)}}, "other": 1 }""");

        var result = await _sut.ReadAsync(Request(F1), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.NotModified.ShouldBeFalse();
        result.Value.Bytes.ShouldBe(Bytes);
        result.Value.Path.ShouldBe("passport");
        result.Value.Handle.File.ShouldBe(F1);
        result.Value.Handle.MimeType.ShouldBe("application/pdf");
        result.Value.Handle.ETag.ShouldBe(ETag);
        await _store.Received(1).GetAsync(Binding, F1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FileInArrayItem_IsFoundByItsId()
    {
        Seed("1.0.0", $$"""{ "files": [ {{Handle(FA)}}, {{Handle(FB)}} ] }""");

        var result = await _sut.ReadAsync(Request(FB), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Path.ShouldBe("files[1]");
        await _store.Received(1).GetAsync(Binding, FB, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FileIdComparison_IsOrdinal()
    {
        Seed("1.0.0", $$"""{ "passport": {{Handle(FAbc)}} }""");

        var result = await _sut.ReadAsync(Request(FAbc.ToUpperInvariant()), CancellationToken.None);

        result.Error.Code.ShouldBe(WorkflowErrorCodes.FileNotFound);
    }

    [Fact]
    public async Task FileOnlyInAnOlderVersion_IsNotFound()
    {
        Seed("1.0.0", $$"""{ "passport": {{Handle(FOld)}} }""");
        Seed("1.0.1", $$"""{ "passport": {{Handle(FNew)}} }""");

        var result = await _sut.ReadAsync(Request(FOld), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.FileNotFound);
        await _store.DidNotReceiveWithAnyArgs().GetAsync(default!, default!, default);
    }

    [Fact]
    public async Task HandleOutsideAnXStoragePath_IsNotFound()
    {
        Seed("1.0.0", $$"""{ "elsewhere": {{Handle(F1)}} }""");

        var result = await _sut.ReadAsync(Request(F1), CancellationToken.None);

        result.Error.Code.ShouldBe(WorkflowErrorCodes.FileNotFound);
    }

    [Fact]
    public async Task UnknownInstance_IsNotFound()
    {
        var result = await _sut.ReadAsync(
            new InstanceFileRequest(Domain, Flow, Guid.NewGuid().ToString(), F1, null, null), CancellationToken.None);

        result.Error.Code.ShouldBe(WorkflowErrorCodes.FileNotFound);
    }

    [Fact]
    public async Task InstanceOfAnotherFlow_IsNotFound()
    {
        Seed("1.0.0", $$"""{ "passport": {{Handle(F1)}} }""");

        var result = await _sut.ReadAsync(
            new InstanceFileRequest(Domain, "other-flow", _instance.Id.ToString(), F1, null, null), CancellationToken.None);

        result.Error.Code.ShouldBe(WorkflowErrorCodes.FileNotFound);
    }

    [Theory]
    [InlineData("\"abc123\"")]
    [InlineData("W/\"abc123\"")]
    [InlineData("\"zzz\", \"abc123\"")]
    [InlineData("*")]
    public async Task IfNoneMatchOnTheETag_IsNotModified_WithoutAStoreRead(string ifNoneMatch)
    {
        Seed("1.0.0", $$"""{ "passport": {{Handle(F1)}} }""");

        var result = await _sut.ReadAsync(Request(F1, ifNoneMatch), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.NotModified.ShouldBeTrue();
        result.Value.Bytes.ShouldBeNull();
        await _store.DidNotReceiveWithAnyArgs().GetAsync(default!, default!, default);
    }

    [Theory]
    [InlineData("abc123")]
    [InlineData("\"other\"")]
    public async Task IfNoneMatchOnAnotherTagOrUnquoted_ReadsTheStore(string ifNoneMatch)
    {
        Seed("1.0.0", $$"""{ "passport": {{Handle(F1)}} }""");

        var result = await _sut.ReadAsync(Request(F1, ifNoneMatch), CancellationToken.None);

        result.Value!.NotModified.ShouldBeFalse();
        result.Value.Bytes.ShouldBe(Bytes);
    }

    [Fact]
    public async Task StoreFailure_IsPropagated()
    {
        Seed("1.0.0", $$"""{ "passport": {{Handle(F1)}} }""");
        _store.GetAsync(Binding, F1, Arg.Any<CancellationToken>())
            .Returns(Result<byte[]>.Fail(WorkflowErrors.FileStoreUnavailable(Binding)));

        var result = await _sut.ReadAsync(Request(F1), CancellationToken.None);

        result.Error.Code.ShouldBe(WorkflowErrorCodes.FileStoreUnavailable);
    }

    [Fact]
    public async Task WithAuthorization_QueryRolesDeny_IsForbidden()
    {
        Seed("1.0.0", $$"""{ "passport": {{Handle(F1)}} }""");
        _authz.IsQueryAllowedAsync(default!, default!, default, default, default).ReturnsForAnyArgs(false);

        var result = await _sut.ReadAsync(Request(F1, auth: Caller), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.AuthorizationRoleDenied);
        await _store.DidNotReceiveWithAnyArgs().GetAsync(default!, default!, default);
        await _authz.Received(1).IsQueryAllowedAsync(
            _workflow, _instance, Arg.Is<IReadOnlyCollection<string>?>(r => r != null && r.Contains("customer")),
            Caller, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithAuthorization_XRolesOnTheArrayDenied_HidesTheFile()
    {
        var sut = WithRoles("""
            { "type": "object", "properties": {
            "files": { "type": "array", "x-roles": [ { "role": "officer", "grant": "allow" } ],
                       "items": { "type": "object", "x-storage": { "binding": "vnext-blob-local" } } } } }
            """);
        Seed("1.0.0", $$"""{ "files": [ {{Handle(F1)}} ] }""");
        _evaluator.IsAnyRoleAllowed(default, default!, default).ReturnsForAnyArgs(false);

        var result = await sut.ReadAsync(Request(F1, auth: Caller), CancellationToken.None);

        result.Error.Code.ShouldBe(WorkflowErrorCodes.FileNotFound);
        await _store.DidNotReceiveWithAnyArgs().GetAsync(default!, default!, default);
        _evaluator.Received(1).IsAnyRoleAllowed(
            Arg.Any<IReadOnlyCollection<string>?>(),
            Arg.Is<IReadOnlyCollection<RoleGrant>>(g => g.Count == 1),
            Arg.Any<Transition?>());
    }

    [Fact]
    public async Task WithAuthorization_XRolesOnAnAncestorDenied_HidesTheFile()
    {
        var sut = WithRoles("""
            { "type": "object", "properties": {
            "docs": { "type": "object", "x-roles": [ { "role": "officer", "grant": "allow" } ], "properties": {
                "tax": { "type": "object", "x-roles": [ { "role": "customer", "grant": "allow" } ],
                         "x-storage": { "binding": "vnext-blob-local" } } } } } }
            """);
        Seed("1.0.0", $$"""{ "docs": { "tax": {{Handle(F1)}} } }""");
        // The leaf allows the caller, the ancestor does not: every guarded prefix must allow.
        _evaluator.IsAnyRoleAllowed(Arg.Any<IReadOnlyCollection<string>?>(),
                Arg.Is<IReadOnlyCollection<RoleGrant>>(g => GrantRole(g) == "officer"), Arg.Any<Transition?>())
            .Returns(false);

        var result = await sut.ReadAsync(Request(F1, auth: Caller), CancellationToken.None);

        result.Error.Code.ShouldBe(WorkflowErrorCodes.FileNotFound);
    }

    [Fact]
    public async Task WithAuthorization_XRolesAllowed_ReadsTheFile()
    {
        var sut = WithRoles("""
            { "type": "object", "properties": {
            "files": { "type": "array", "x-roles": [ { "role": "customer", "grant": "allow" } ],
                       "items": { "type": "object", "x-storage": { "binding": "vnext-blob-local" } } } } }
            """);
        Seed("1.0.0", $$"""{ "files": [ {{Handle(F1)}} ] }""");

        var result = await sut.ReadAsync(Request(F1, auth: Caller), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Bytes.ShouldBe(Bytes);
    }

    [Fact]
    public async Task WithAuthorization_RoleResolutionFailure_IsADenial()
    {
        Seed("1.0.0", $$"""{ "passport": {{Handle(F1)}} }""");
        _roles.ResolveRolesAsync(Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(Result<string[]?>.Fail(Error.Failure("roles:down", "provider down")));

        var result = await _sut.ReadAsync(Request(F1, auth: Caller), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        await _store.DidNotReceiveWithAnyArgs().GetAsync(default!, default!, default);
    }

    [Fact]
    public async Task WithoutAuthorization_NeitherCheckRuns()
    {
        var sut = WithRoles("""
            { "type": "object", "properties": {
            "files": { "type": "array", "x-roles": [ { "role": "officer", "grant": "allow" } ],
                       "items": { "type": "object", "x-storage": { "binding": "vnext-blob-local" } } } } }
            """);
        Seed("1.0.0", $$"""{ "files": [ {{Handle(F1)}} ] }""");
        _authz.IsQueryAllowedAsync(default!, default!, default, default, default).ReturnsForAnyArgs(false);
        _evaluator.IsAnyRoleAllowed(default, default!, default).ReturnsForAnyArgs(false);

        var result = await sut.ReadAsync(Request(F1), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Bytes.ShouldBe(Bytes);
        await _roles.DidNotReceiveWithAnyArgs().ResolveRolesAsync(default, default);
        await _authz.DidNotReceiveWithAnyArgs().IsQueryAllowedAsync(default!, default!, default, default, default);
        await _authz.DidNotReceiveWithAnyArgs().CreateEvaluatorAsync(default, default, default, default!, default);
    }

    // ── I1: the stored record alone is not trusted ──────────────────────────────────────────────────────────

    private string RawHandle(string component, string file) =>
        $$"""{ "component": "{{component}}", "file": "{{file}}", "size": 3, "eTag": "{{ETag}}", "owner": { "domain": "{{Domain}}", "flow": "{{Flow}}", "instance": "{{_instance.Id}}" } }""";

    [Fact]
    public async Task StoredHandleWithAForeignComponent_IsNotFound_AndTheStoreIsNeverCalled()
    {
        Seed("1.0.0", $$"""{ "passport": {{RawHandle("other-bucket", F1)}} }""");

        var result = await _sut.ReadAsync(Request(F1), CancellationToken.None);

        result.Error.Code.ShouldBe(WorkflowErrorCodes.FileNotFound);
        await _store.DidNotReceiveWithAnyArgs().GetAsync(default!, default!, default);
    }

    [Theory]
    [InlineData("../secrets/key")]
    [InlineData("not-a-guid")]
    [InlineData("0b9f6f3e1c1a4a7e9c553f1d2a6b7c80")]
    public async Task RequestedOrStoredFileThatIsNotAGuid_IsNotFound_AndTheStoreIsNeverCalled(string file)
    {
        Seed("1.0.0", $$"""{ "passport": {{RawHandle(Binding, file)}} }""");

        var result = await _sut.ReadAsync(Request(file), CancellationToken.None);

        result.Error.Code.ShouldBe(WorkflowErrorCodes.FileNotFound);
        await _store.DidNotReceiveWithAnyArgs().GetAsync(default!, default!, default);
    }

    [Fact]
    public async Task StoredHandleOfAnAllowedBinding_IsRead()
    {
        _options = new FileStorageOptions { AllowedBindings = ["vnext-blob-parent"] };
        var sut = CreateSut();
        _store.GetAsync("vnext-blob-parent", F1, Arg.Any<CancellationToken>()).Returns(Result<byte[]>.Ok(Bytes));
        Seed("1.0.0", $$"""{ "passport": {{RawHandle("vnext-blob-parent", F1)}} }""");

        var result = await sut.ReadAsync(Request(F1), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        await _store.Received(1).GetAsync("vnext-blob-parent", F1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StoredHandleWithAnEmptyETag_IsNotFound()
    {
        Seed("1.0.0", $$"""{ "passport": { "component": "{{Binding}}", "file": "{{F1}}", "size": 3, "eTag": "", "owner": { "domain": "d", "flow": "f", "instance": "i" } } }""");

        var result = await _sut.ReadAsync(Request(F1), CancellationToken.None);

        result.Error.Code.ShouldBe(WorkflowErrorCodes.FileNotFound);
    }

    // ── I3 / M3 / M4 ─────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SchemaUnavailable_Is503()
    {
        Seed("1.0.0", $$"""{ "passport": {{Handle(F1)}} }""");
        _offload.GetFieldsAsync(Arg.Any<Definitions.Workflow>(), Arg.Any<CancellationToken>())
            .Returns(Result<IReadOnlyList<FileStorageField>>.Fail(WorkflowErrors.FileSchemaUnavailable("master")));

        var result = await _sut.ReadAsync(Request(F1), CancellationToken.None);

        result.Error.Code.ShouldBe(WorkflowErrorCodes.FileSchemaUnavailable);
        await _store.DidNotReceiveWithAnyArgs().GetAsync(default!, default!, default);
    }

    /// <summary>queryRoles are decided before the file is located: a denied caller gets 403 for a file that does not exist too.</summary>
    [Fact]
    public async Task WithAuthorization_QueryRolesDeny_IsForbidden_EvenForAMissingFile()
    {
        Seed("1.0.0", $$"""{ "passport": {{Handle(F1)}} }""");
        _authz.IsQueryAllowedAsync(default!, default!, default, default, default).ReturnsForAnyArgs(false);

        var missing = await _sut.ReadAsync(Request(FB, auth: Caller), CancellationToken.None);
        var present = await _sut.ReadAsync(Request(F1, auth: Caller), CancellationToken.None);

        missing.Error.Code.ShouldBe(WorkflowErrorCodes.AuthorizationRoleDenied);
        present.Error.Code.ShouldBe(WorkflowErrorCodes.AuthorizationRoleDenied);
        await _offload.DidNotReceiveWithAnyArgs().GetFieldsAsync(default!, default);
    }

    [Fact]
    public async Task ForeignRouteDomain_IsNotFoundDomain_LikeTheDataFunction()
    {
        await Should.ThrowAsync<BBT.Workflow.ExceptionHandling.NotFoundDomainException>(
            () => _sut.ReadAsync(new InstanceFileRequest("other-domain", Flow, _instance.Id.ToString(), F1, null, null), CancellationToken.None));
        await _repository.DidNotReceiveWithAnyArgs().FindByIdentifierAsReadOnlyAsync(default!, default);
    }

    private InstanceFileAppService WithRoles(string masterSchema)
    {
        UseSchema(masterSchema);
        return _sut;
    }

    private static string? GrantRole(IReadOnlyCollection<RoleGrant> grants)
    {
        foreach (var g in grants)
            return g.Role;
        return null;
    }

    /// <summary>The aggregate's persisted-row hook is internal to Domain; same reflection the benchmarks use.</summary>
    private void Seed(string version, string json)
    {
        var ctor = typeof(InstanceData).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(Guid), typeof(Guid), typeof(string), typeof(JsonData), typeof(bool)])!;
        var row = (InstanceData)ctor.Invoke([Guid.NewGuid(), _instance.Id, version, new JsonData(json), true]);
        typeof(Instance).GetMethod("AcceptPersistedData", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(_instance, [row]);
    }
}
