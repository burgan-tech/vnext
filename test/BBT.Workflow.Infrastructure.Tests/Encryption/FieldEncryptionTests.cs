using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;
using BBT.Workflow.Authorization;
using BBT.Workflow.Encryption;
using BBT.Workflow.ExceptionHandling;
using BBT.Workflow.Instances;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Encryption;

/// <summary>
/// <c>x-encryption</c> building blocks under per-instance secrets: the AES-256-GCM token, the hash digest and the
/// protector's passes (open, sanitize a request, hash, seal).
/// </summary>
public sealed class FieldEncryptionTests
{
    private const string Schema = "flow_schema";
    private static readonly Guid InstanceA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid InstanceB = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly IReadOnlySet<string> EmailPath = new HashSet<string>(StringComparer.Ordinal) { "customer.email" };
    private static readonly IReadOnlyDictionary<string, string> TcknHash =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["customer.tckn"] = "sha256" };

    private static InstanceSecretMaterial NewSecret() =>
        new(RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32));

    private static (InstanceDataProtector Protector, InstanceSecretStore Store) Create()
    {
        var store = new InstanceSecretStore(Options.Create(new SchemaEncryptionOptions()));
        return (new InstanceDataProtector(store), store);
    }

    private static JsonData Doc(string json) => new(json);

    private static string? At(JsonData data, string path) => EncryptedValueFormat.StringAt(data.JsonElement, path);

    // ── encrypt ──────────────────────────────────────────────────────────────

    [Fact]
    public void Seal_ThenOpen_RoundTrips_AndTheStoredFormIsATokenOfTheInstanceKey()
    {
        var (protector, store) = Create();
        var secret = NewSecret();
        store.Put(Schema, InstanceA, secret);

        var (stored, view) = protector.Protect(InstanceA, Doc("""{"customer":{"email":"user@example.com","name":"Ayşe"}}"""), EmailPath, null, secret);

        var token = At(stored, "customer.email")!;
        token.ShouldStartWith("ENCRYPTED:AES256:i1:");
        stored.Json.ShouldNotContain("user@example.com");
        At(stored, "customer.name").ShouldBe("Ayşe");
        view.Tokens["customer.email"].ShouldBe(token);

        var opened = protector.Unprotect(Schema, InstanceA, stored);
        At(opened.Plain, "customer.email").ShouldBe("user@example.com");
        opened.Undecryptable.ShouldBeEmpty();
    }

    [Fact]
    public void AnotherInstancesKey_DoesNotOpenTheToken()
    {
        var (protector, store) = Create();
        var secretA = NewSecret();
        store.Put(Schema, InstanceA, secretA);
        store.Put(Schema, InstanceB, NewSecret());
        var stored = protector.Protect(InstanceA, Doc("""{"customer":{"email":"x@y.z"}}"""), EmailPath, null, secretA).Stored;

        protector.Unprotect(Schema, InstanceB, stored).Undecryptable.ShouldContain("customer.email");
    }

    [Fact]
    public void ATokenMovedToAnotherPath_DoesNotOpen()
    {
        var (protector, store) = Create();
        var secret = NewSecret();
        store.Put(Schema, InstanceA, secret);
        var token = At(protector.Protect(InstanceA, Doc("""{"customer":{"email":"x@y.z"}}"""), EmailPath, null, secret).Stored, "customer.email")!;

        protector.Unprotect(Schema, InstanceA, Doc($$$"""{"customer":{"phone":"{{{token}}}"}}""")).Undecryptable
            .ShouldContain("customer.phone");
    }

    /// <summary>No secret row (never written, or crypto-shredded): the token stays closed, never plaintext.</summary>
    [Fact]
    public void WithoutASecret_TheTokenStaysClosed()
    {
        var (protector, _) = Create();
        var stored = Create().Protector.Protect(InstanceA, Doc("""{"customer":{"email":"x@y.z"}}"""), EmailPath, null, NewSecret()).Stored;

        var opened = protector.Unprotect(Schema, InstanceA, stored);

        opened.Undecryptable.ShouldBe(["customer.email"]);
        At(opened.Plain, "customer.email").ShouldStartWith("ENCRYPTED:AES256:i1:");
    }

    [Fact]
    public void AnUnchangedValue_CarriesTheHeadTokenForward_AChangedOneGetsAFreshToken()
    {
        var (protector, store) = Create();
        var secret = NewSecret();
        store.Put(Schema, InstanceA, secret);
        var headStored = protector.Protect(InstanceA, Doc("""{"customer":{"email":"x@y.z"},"n":1}"""), EmailPath, null, secret).Stored;
        var head = protector.Unprotect(Schema, InstanceA, headStored);

        At(protector.Protect(InstanceA, Doc("""{"customer":{"email":"x@y.z"},"n":2}"""), EmailPath, head, secret).Stored, "customer.email")
            .ShouldBe(At(headStored, "customer.email"));
        At(protector.Protect(InstanceA, Doc("""{"customer":{"email":"new@y.z"}}"""), EmailPath, head, secret).Stored, "customer.email")
            .ShouldNotBe(At(headStored, "customer.email"));
    }

    [Fact]
    public void AnUnchangedUndecryptableValue_IsRefused()
    {
        var (protector, _) = Create();
        var stored = Create().Protector.Protect(InstanceA, Doc("""{"customer":{"email":"x@y.z"}}"""), EmailPath, null, NewSecret()).Stored;
        var head = protector.Unprotect(Schema, InstanceA, stored);

        Should.Throw<EncryptionKeyUnavailableException>(() => protector.Protect(InstanceA, head.Plain, EmailPath, head, NewSecret()));
    }

    // ── hash ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Hashing_ReplacesTheValueWithAPrefixedDigest_AndIsIdempotent()
    {
        var (protector, _) = Create();
        var secret = NewSecret();

        var hashed = protector.ApplyHashes(Doc("""{"customer":{"tckn":"12345678901","name":"A"}}"""), TcknHash, secret);

        var digest = At(hashed, "customer.tckn")!;
        digest.ShouldStartWith("HASHED:SHA256:");
        digest.Length.ShouldBe("HASHED:SHA256:".Length + 64);
        hashed.Json.ShouldNotContain("12345678901");
        At(hashed, "customer.name").ShouldBe("A");

        protector.ApplyHashes(hashed, TcknHash, secret).ShouldBeSameAs(hashed); // an existing digest is kept
        At(protector.ApplyHashes(Doc("""{"customer":{"tckn":"12345678901"}}"""), TcknHash, secret), "customer.tckn").ShouldBe(digest);
    }

    /// <summary>Per-instance salt: the same value hashes differently in two instances (no cross-instance matching).</summary>
    [Fact]
    public void TheSameValue_HashesDifferentlyUnderTwoInstanceSalts()
    {
        var (protector, _) = Create();
        var body = Doc("""{"customer":{"tckn":"12345678901"}}""");

        At(protector.ApplyHashes(body, TcknHash, NewSecret()), "customer.tckn")
            .ShouldNotBe(At(protector.ApplyHashes(body, TcknHash, NewSecret()), "customer.tckn"));
    }

    [Fact]
    public void Sha512_UsesItsOwnPrefix()
    {
        var (protector, _) = Create();
        var paths = new Dictionary<string, string>(StringComparer.Ordinal) { ["k"] = "sha512" };

        At(protector.ApplyHashes(Doc("""{"k":"v"}"""), paths, NewSecret()), "k").ShouldStartWith("HASHED:SHA512:");
    }

    // ── sanitize a request ───────────────────────────────────────────────────

    [Fact]
    public void EchoingTheStoredTokenOrDigest_IsANoOp_AnythingElsePrefixedIsRejected()
    {
        var (protector, store) = Create();
        var secret = NewSecret();
        store.Put(Schema, InstanceA, secret);
        var hashed = protector.ApplyHashes(Doc("""{"customer":{"email":"x@y.z","tckn":"123"}}"""), TcknHash, secret);
        var headStored = protector.Protect(InstanceA, hashed, EmailPath, null, secret).Stored;
        var head = protector.Unprotect(Schema, InstanceA, headStored);
        var token = At(headStored, "customer.email")!;
        var digest = At(headStored, "customer.tckn")!;

        var echoed = protector.SanitizeDelta(InstanceA, Doc($$$"""{"customer":{"email":"{{{token}}}","tckn":"{{{digest}}}"}}"""), headStored, head, TcknHash);
        At(echoed, "customer.email").ShouldBe("x@y.z");
        At(echoed, "customer.tckn").ShouldBe(digest);

        Should.Throw<EncryptedValueReservedException>(() =>
                protector.SanitizeDelta(InstanceA, Doc("""{"customer":{"tckn":"HASHED:SHA256:0000"}}"""), headStored, head, TcknHash))
            .Path.ShouldBe("customer.tckn");
        Should.Throw<EncryptedValueReservedException>(() =>
                protector.SanitizeDelta(InstanceA, Doc($$$"""{"note":"{{{token}}}"}"""), headStored, head, TcknHash))
            .Path.ShouldBe("note");
    }

    /// <summary>
    /// The engine sees digests (hash is applied on write), so a mapping copying a hashed field produces a HASHED value on
    /// a plain path: that is an ordinary string there. A token stays reserved everywhere (decryption is prefix-driven).
    /// </summary>
    [Fact]
    public void ADigestOutsideAHashPath_IsAnOrdinaryValue_ATokenIsNot()
    {
        var (protector, store) = Create();
        var secret = NewSecret();
        store.Put(Schema, InstanceA, secret);
        var headStored = protector.Protect(InstanceA,
            protector.ApplyHashes(Doc("""{"customer":{"email":"x@y.z","tckn":"123"}}"""), TcknHash, secret), EmailPath, null, secret).Stored;
        var head = protector.Unprotect(Schema, InstanceA, headStored);
        var digest = At(headStored, "customer.tckn")!;
        var token = At(headStored, "customer.email")!;

        var copied = protector.SanitizeDelta(InstanceA, Doc($$$"""{"mirrored":"{{{digest}}}","list":["{{{digest}}}"]}"""), headStored, head, TcknHash);
        At(copied, "mirrored").ShouldBe(digest);

        Should.Throw<EncryptedValueReservedException>(() =>
                protector.SanitizeDelta(InstanceA, Doc($$$"""{"mirrored":"{{{token}}}"}"""), headStored, head, TcknHash))
            .Path.ShouldBe("mirrored");
    }

    // ── DataHash ─────────────────────────────────────────────────────────────

    [Fact]
    public void TheKeyedDataHash_IsDeterministicPerInstanceKey_AndNotTheUnkeyedDigest()
    {
        var secret = NewSecret();
        var plain = Doc("""{"tckn":"12345678901"}""");

        var hash = InstanceDataProtector.KeyedDataHash(plain, secret);

        hash.Length.ShouldBe(40);
        hash.ShouldBe(InstanceDataProtector.KeyedDataHash(Doc("""{ "tckn" : "12345678901" }"""), secret));
        hash.ShouldNotBe(InstanceData.ComputeDataHash(plain));
        InstanceDataProtector.KeyedDataHash(plain, NewSecret()).ShouldNotBe(hash);
    }
}
