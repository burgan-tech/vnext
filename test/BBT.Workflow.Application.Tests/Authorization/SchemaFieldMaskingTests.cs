using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Aether.Users;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Authorization;

/// <summary>
/// The exposure plan built by <see cref="SchemaFieldFilterService"/>: <c>x-roles</c> first, then
/// <c>x-masking</c> with its allow-only exemption list. Every row of the council's matrix is pinned here —
/// the rows that matter most are the ones where the caller is NOT exempt for a reason other than a
/// mismatch: no roles, a failed resolution, a misspelled role.
/// </summary>
public sealed class SchemaFieldMaskingTests
{
    private const string Auditor = "morph-idm.auditor";
    private const string Reader = "morph-idm.reader";
    private const string Admin = "morph-idm.admin";

    private readonly IComponentCacheStore _cache = Substitute.For<IComponentCacheStore>();
    private readonly ICallerRoleResolver _roles = Substitute.For<ICallerRoleResolver>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly FakeFieldMaskingEngine _engine = new();

    private const string SchemaProperties = """
        "iban":      { "type": "string", "x-masking": { "operator": "mask", "params": { "keepFirst": 2, "keepLast": 4 } } },
        "tckn":      { "type": "string", "x-masking": { "operator": "mask", "params": { "keepLast": 3 },
                       "roles": [ { "role": "morph-idm.auditor", "grant": "allow" } ] } },
        "owned":     { "type": "string", "x-masking": { "operator": "replace", "params": { "value": "[gizli]" },
                       "roles": [ { "role": "$InstanceStarter", "grant": "allow" } ] } },
        "dynamic":   { "type": "string", "x-masking": { "operator": "mask",
                       "roles": [ { "role": "$role.$.context.Headers.opsRole", "grant": "allow" } ] } },
        "secret":    { "type": "string", "x-roles": [ { "role": "morph-idm.admin", "grant": "allow" } ],
                       "x-masking": { "operator": "mask" } },
        "customer":  { "type": "object", "properties": {
                       "phone": { "type": "string", "x-masking": { "operator": "mask", "params": { "keepLast": 2 } } } } },
        "hashed":    { "type": "string", "x-encryption": { "type": "hash", "purpose": "PII-Identification" } },
        "legacyHash": { "type": "string", "x-encryption": { "type": "hash" } },
        "persisted": { "type": "string", "x-encryption": { "type": "persisted" } },
        "email":     { "type": "string", "x-encryption": { "type": "encrypt",
                       "roles": [ { "role": "morph-idm.auditor", "grant": "allow" } ] } },
        "legacy":    { "type": "string", "x-encryption": { "type": "encrypt" } },
        "public":    { "type": "string" }
        """;

    private const string Data = """
        { "iban": "TR330006100519786457841326", "tckn": "12345678901", "owned": "owner-only",
          "dynamic": "abcdef", "secret": "top", "customer": { "phone": "5321234567" }, "public": "hello",
          "hashed": "HASHED:SHA256:3f2a", "legacyHash": "98765432109", "persisted": "stored-in-clear",
          "email": "user@example.com", "legacy": "written-before-encrypt" }
        """;

    private const string EmailToken = "ENCRYPTED:AES256:k1:c3RvcmVkLXRva2Vu";

    /// <summary>What the row the data was read from stores: a token for "email", nothing for "legacy".</summary>
    private static readonly IReadOnlyDictionary<string, string> StoredTokens =
        new Dictionary<string, string> { ["email"] = EmailToken };

    private async Task<JsonElement> ApplyAsync(
        Result<string[]?> roles, Instance? instance = null, SchemaMaskingOptions? options = null,
        Dictionary<string, string?>? headers = null, SchemaEncryptionOptions? encryption = null,
        IReadOnlyDictionary<string, string>? tokens = null, bool withoutTokens = false, string? data = null)
    {
        var workflow = ConfigureWorkflow();
        _roles.ResolveRolesAsync(Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(roles);
        var sut = new SchemaFieldFilterService(_cache,
            new TransitionAuthorizationManager(_user, Substitute.For<IInstanceTransitionRepository>()), _roles,
            _engine, Options.Create(options ?? new SchemaMaskingOptions()),
            Options.Create(encryption ?? new SchemaEncryptionOptions()));
        using var json = JsonDocument.Parse(data ?? Data);
        instance ??= InstanceWithData("someone");
        var request = new AuthorizationRequestContext(headers ?? new Dictionary<string, string?>(), null);
        return (await sut.ApplyAsync(workflow, json.RootElement.Clone(), instance, request, CancellationToken.None,
            withoutTokens ? null : tokens ?? StoredTokens))!.Value;
    }

    private static Result<string[]?> Roles(params string[] roles) => Result<string[]?>.Ok(roles);

    [Fact]
    public async Task RuleWithoutRoles_AppliesToEveryCaller()
    {
        var body = await ApplyAsync(Roles(Auditor));

        body.GetProperty("iban").GetString().ShouldBe("TR********************1326");
        body.GetProperty("customer").GetProperty("phone").GetString().ShouldBe("********67");
        body.GetProperty("public").GetString().ShouldBe("hello");
    }

    [Fact]
    public async Task AllowListedRole_SeesTheValueInClear()
        => (await ApplyAsync(Roles(Auditor))).GetProperty("tckn").GetString().ShouldBe("12345678901");

    [Fact]
    public async Task AllowListedRoleAmongOthers_SeesTheValueInClear()
        => (await ApplyAsync(Roles(Reader, Auditor))).GetProperty("tckn").GetString().ShouldBe("12345678901");

    [Fact]
    public async Task UnlistedRole_IsMasked()
        => (await ApplyAsync(Roles(Reader))).GetProperty("tckn").GetString().ShouldBe("********901");

    [Fact]
    public async Task MisspelledRole_IsMasked_AnAllowListFailsClosed()
        => (await ApplyAsync(Roles("morph-idm.audtor"))).GetProperty("tckn").GetString().ShouldBe("********901");

    [Fact]
    public async Task EmptyRoleSet_IsMasked()
        => (await ApplyAsync(Roles())).GetProperty("tckn").GetString().ShouldBe("********901");

    [Fact]
    public async Task RoleResolutionFailure_MasksEveryRuleAndPrunesEveryGuardedField()
    {
        var body = await ApplyAsync(Result<string[]?>.Fail(Error.Forbidden("down", "provider unavailable")));

        body.GetProperty("tckn").GetString().ShouldBe("********901");
        body.GetProperty("owned").GetString().ShouldBe("[gizli]");
        body.TryGetProperty("secret", out _).ShouldBeFalse();
        body.GetProperty("public").GetString().ShouldBe("hello");
    }

    [Fact]
    public async Task IdentityBoundExemption_MatchesARoleLessCallerWhoIsTheStarter()
    {
        _user.ActorUserName.Returns("alice");

        (await ApplyAsync(Roles(), InstanceWithData("alice"))).GetProperty("owned").GetString().ShouldBe("owner-only");
        (await ApplyAsync(Roles(), InstanceWithData("bob"))).GetProperty("owned").GetString().ShouldBe("[gizli]");
    }

    [Fact]
    public async Task RoleBoundDynamicExemption_CannotBeProvenByARoleLessCaller()
    {
        // $role. resolves to "" from the request; a role-less caller is evaluated with an empty role name, and
        // the two must never "match" — that would hand the raw value to exactly the caller who proved nothing.
        var headers = new Dictionary<string, string?> { ["opsRole"] = "" };
        (await ApplyAsync(Roles(), headers: headers)).GetProperty("dynamic").GetString().ShouldBe("******");
    }

    [Fact]
    public async Task RoleBoundDynamicExemption_MatchesACallerHoldingTheResolvedRole()
    {
        var headers = new Dictionary<string, string?> { ["opsRole"] = "ops" };
        (await ApplyAsync(Roles("ops"), headers: headers)).GetProperty("dynamic").GetString().ShouldBe("abcdef");
    }

    [Fact]
    public async Task HiddenField_IsPrunedAndNeverReachesTheMaskRule()
    {
        var body = await ApplyAsync(Roles(Reader));

        body.TryGetProperty("secret", out _).ShouldBeFalse();
        _engine.Calls.ShouldBe(7); // iban, tckn, owned, dynamic, customer.phone, legacy, legacyHash — never secret; email serves its token, hashed its digest
    }

    [Fact]
    public async Task VisibleGuardedField_IsStillMasked()
        => (await ApplyAsync(Roles(Admin))).GetProperty("secret").GetString().ShouldBe("***");

    [Fact]
    public async Task MaskingDisabled_FallsBackToRolesOnly()
    {
        var body = await ApplyAsync(Roles(Reader), options: new SchemaMaskingOptions { Enabled = false });

        body.GetProperty("iban").GetString().ShouldBe("TR330006100519786457841326");
        body.TryGetProperty("secret", out _).ShouldBeFalse();
        _engine.Calls.ShouldBe(2); // the x-encryption rules survive the switch: legacy encrypt value and legacy hash value
    }

    private static Instance InstanceWithData(string createdBy)
    {
        var instance = InstanceFactory.CreateDefault("masking-" + createdBy);
        instance.CreatedBy = createdBy;
        return instance;
    }

    private Definitions.Workflow ConfigureWorkflow()
    {
        var reference = new Reference("schema", "bank", "sys-schemas", "1.0.0");
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var workflow = JsonSerializer.Deserialize<Definitions.Workflow>(
            "{\"type\":\"F\",\"states\":[],\"schema\":" + JsonSerializer.Serialize(reference) + "}", options)!;
        workflow.SetReference(new Reference("flow", "bank", "sys-flows", "1.0.0"));
        var schema = JsonSerializer.Deserialize<SchemaDefinition>(
            "{\"type\":\"master\",\"schema\":{\"type\":\"object\",\"properties\":{" + SchemaProperties + "}}}", options)!;
        schema.SetReference(reference);
        _cache.GetSchemaAsync("bank", "schema", "1.0.0", Arg.Any<CancellationToken>())
            .Returns(Result<SchemaDefinition>.Ok(schema));
        return workflow;
    }

    // ── x-encryption: hash ───────────────────────────────────────────────────

    /// <summary>Hash is applied on write: the stored digest is served as it is, to everyone (there is no raw value left).</summary>
    [Fact]
    public async Task AStoredDigest_IsServedAsIs_ToEveryCaller()
    {
        foreach (var roles in new[] { Roles(Reader), Roles(), Roles(Auditor) })
            (await ApplyAsync(roles)).GetProperty("hashed").GetString().ShouldBe("HASHED:SHA256:3f2a");
    }

    /// <summary>A value written before the hash rule existed is still raw: nobody is shown it.</summary>
    [Fact]
    public async Task AnUnhashedValueUnderAHashRule_IsFullyMasked()
    {
        (await ApplyAsync(Roles(Reader))).GetProperty("legacyHash").GetString().ShouldBe("********");
        (await ApplyAsync(Roles(Auditor))).GetProperty("legacyHash").GetString().ShouldBe("********");
    }

    [Fact]
    public async Task EncryptionTypesWithoutReadPathBehaviour_ServeTheStoredValue()
        => (await ApplyAsync(Roles(Reader))).GetProperty("persisted").GetString().ShouldBe("stored-in-clear");

    [Fact]
    public async Task TheMaskingSwitch_DisablesOnlyXMasking()
    {
        var noMasking = await ApplyAsync(Roles(Reader), options: new SchemaMaskingOptions { Enabled = false });
        noMasking.GetProperty("hashed").GetString().ShouldBe("HASHED:SHA256:3f2a");
        noMasking.GetProperty("legacyHash").GetString().ShouldBe("********");
        noMasking.GetProperty("tckn").GetString().ShouldBe("12345678901");
    }

    // ── x-encryption "encrypt" ───────────────────────────────────────────────

    [Fact]
    public async Task Encrypt_ACallerOutsideTheExemptionList_GetsTheStoredToken()
        => (await ApplyAsync(Roles(Reader))).GetProperty("email").GetString().ShouldBe(EmailToken);

    [Fact]
    public async Task Encrypt_TheAllowListedRole_GetsThePlaintext()
        => (await ApplyAsync(Roles(Auditor))).GetProperty("email").GetString().ShouldBe("user@example.com");

    [Fact]
    public async Task Encrypt_ARoleLessOrFailedCaller_GetsTheToken()
    {
        (await ApplyAsync(Roles())).GetProperty("email").GetString().ShouldBe(EmailToken);
        (await ApplyAsync(Result<string[]?>.Fail(Error.Forbidden("down", "provider unavailable"))))
            .GetProperty("email").GetString().ShouldBe(EmailToken);
    }

    /// <summary>A value written before the field became encrypt has no token: nobody outside the list sees it.</summary>
    [Fact]
    public async Task Encrypt_ALegacyPlaintextValue_IsFullyMasked()
    {
        (await ApplyAsync(Roles(Reader))).GetProperty("legacy").GetString().ShouldBe("********");
        (await ApplyAsync(Roles(Auditor))).GetProperty("legacy").GetString().ShouldBe("********");
    }

    [Fact]
    public async Task Encrypt_WithoutTheRowsTokens_IsMasked_NeverThePlaintext()
        => (await ApplyAsync(Roles(Reader), withoutTokens: true)).GetProperty("email").GetString().ShouldBe("********");

    [Fact]
    public async Task Encrypt_AValueThatIsStillAToken_IsServedAsIs()
    {
        var body = await ApplyAsync(Roles(Reader), withoutTokens: true,
            data: """{ "email": "ENCRYPTED:AES256:gone:dW5yZWFkYWJsZQ" }""");

        body.GetProperty("email").GetString().ShouldBe("ENCRYPTED:AES256:gone:dW5yZWFkYWJsZQ");
    }

    /// <summary>The masking switch cannot drop an encrypt rule: that would hand the engine's plaintext to every caller.</summary>
    [Fact]
    public async Task Encrypt_IsNotTurnedOffByTheMaskingSwitch()
        => (await ApplyAsync(Roles(Reader), options: new SchemaMaskingOptions { Enabled = false }))
            .GetProperty("email").GetString().ShouldBe(EmailToken);
}
