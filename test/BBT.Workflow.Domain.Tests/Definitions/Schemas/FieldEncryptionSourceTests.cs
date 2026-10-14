using System.Linq;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions.Schemas;

/// <summary>
/// <c>x-encryption.source</c>: <c>runtime</c> (default) is the at-rest rule; <c>client</c> is an in-transit seal the client
/// applies and the runtime never acts on — no write-time transform, no read rule, no filter refusal.
/// </summary>
public sealed class FieldEncryptionSourceTests
{
    private const string ClientRule = """
        { "source": "client", "key": "urn:client:context:read:device:memory:device.hpke.publicKey",
          "algorithm": "HPKE-P256-SHA256-AES128GCM", "into": "passwordEnc", "encoding": "base64url",
          "nonce": "$instance.facts.nonce", "envelope": "publicKey", "purpose": "Credential" }
        """;

    private static JsonElement Schema(string properties) =>
        JsonDocument.Parse($$"""{ "type": "object", "properties": { {{properties}} } }""").RootElement;

    private static string[] Errors(string properties, string? type) =>
        FieldMaskingDefinition.Validate(Schema(properties), type).Select(e => e.Message).ToArray();

    [Theory]
    [InlineData("master")]
    [InlineData("transition")]
    [InlineData("function")]
    [InlineData(null)]
    [InlineData("workflow")]
    public void ClientRule_IsAccepted_OnAnySchema_TheMasterIncluded(string? type)
        => Errors($$""" "password": { "type": "string", "x-encryption": {{ClientRule}} } """, type).ShouldBeEmpty();

    [Fact]
    public void ClientRule_NeedsKeyAndAlgorithm()
    {
        var errors = Errors(""" "p": { "type": "string", "x-encryption": { "source": "client" } } """, "transition");
        errors.ShouldContain(e => e.Contains("x-encryption.key is required"));
        errors.ShouldContain(e => e.Contains("x-encryption.algorithm is required"));
    }

    [Fact]
    public void ClientRule_RefusesAtRestMembers_AndUnknownValues()
    {
        var errors = Errors("""
            "p": { "type": "string", "x-encryption": { "source": "client", "key": "urn:k", "algorithm": "AES",
                   "type": "encrypt", "roles": [ { "role": "a", "grant": "allow" } ], "envelope": "jwe" } }
            """, "transition");

        errors.ShouldContain(e => e.Contains("x-encryption.type belongs to source 'runtime'"));
        errors.ShouldContain(e => e.Contains("x-encryption.roles belongs to source 'runtime'"));
        errors.ShouldContain(e => e.Contains("x-encryption.algorithm is required"));
        errors.ShouldContain(e => e.Contains("x-encryption.envelope must be 'publicKey'"));
    }

    [Fact]
    public void RuntimeRule_AcceptsAnExplicitSource_AndRefusesClientMembers()
    {
        Errors(""" "p": { "type": "string", "x-encryption": { "source": "runtime", "type": "encrypt" } } """, "master")
            .ShouldBeEmpty();
        Errors(""" "p": { "type": "string", "x-encryption": { "type": "encrypt", "key": "urn:k" } } """, "master")
            .ShouldHaveSingleItem().ShouldContain("x-encryption.key belongs to source 'client'");
        Errors(""" "p": { "type": "string", "x-encryption": { "source": "device", "type": "encrypt" } } """, "master")
            .ShouldHaveSingleItem().ShouldContain("x-encryption.source must be 'runtime' or 'client'");
    }

    [Fact]
    public void ClientRule_IsNotAnActiveTransform_SoItMaySitNextToMasking()
        => Errors($$"""
            "p": { "type": "string", "x-masking": { "operator": "mask" }, "x-encryption": {{ClientRule}} }
            """, "transition").ShouldBeEmpty();

    /// <summary>
    /// On a master schema a client rule is accepted and never handled: no write-time encryption or hash, no read
    /// transform, no encrypted-path filter refusal — the stored value is whatever the client sent.
    /// </summary>
    [Fact]
    public void Parser_YieldsNoRule_ForAClientSource()
    {
        var exposure = SchemaRolesParser.ParseExposure(Schema($$"""
            "password": { "type": "string", "x-encryption": {{ClientRule}} },
            "tckn": { "type": "string", "x-encryption": { "source": "runtime", "type": "encrypt" } }
            """));

        exposure.PathMaskRules.Keys.ShouldBe(["tckn"]);
        exposure.EncryptPaths.ShouldBe(["tckn"]);
        exposure.HashPaths.ShouldBeEmpty();
    }
}
