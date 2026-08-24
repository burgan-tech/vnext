using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.Json;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Security;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Security;

/// <summary>
/// Unit tests for <see cref="SensitiveDataCipher"/>. The invariants that matter: plaintext never
/// survives into the stored document, ciphertext never survives into a read, and every failure mode
/// throws rather than passing a marker through.
/// </summary>
public sealed class SensitiveDataCipherTests
{
    private const string KeyId = "v1";
    private const string OtherKeyId = "v2";

    private static readonly Dictionary<string, SensitiveFieldMetadata> EncryptSsn = new()
    {
        ["ssn"] = new SensitiveFieldMetadata { Enabled = true, Purpose = "PII", EncryptAtRest = true }
    };

    private static SensitiveDataCipher Cipher(bool enabled = true, params string[] keyIds)
        => new(new FakeKeyProvider(keyIds.Length == 0 ? [KeyId] : keyIds, KeyId), enabled);

    private static JsonData Data(string json) => new(json);

    [Fact]
    public void Encrypt_ReplacesOnlyTheAnnotatedLeaf()
    {
        var stored = Cipher().Encrypt(
            Data("""{ "ssn": "123-45-6789", "name": "Jane" }"""),
            EncryptSsn);

        stored.Json.ShouldNotContain("123-45-6789");
        stored.Json.ShouldContain(SensitiveDataCipher.MarkerPrefix);
        // Unannotated fields must be untouched.
        stored.Json.ShouldContain("Jane");
    }

    [Fact]
    public void RoundTrip_RestoresTheExactValue()
    {
        var cipher = Cipher();
        var original = Data("""{ "ssn": "123-45-6789", "name": "Jane" }""");

        var restored = cipher.Decrypt(cipher.Encrypt(original, EncryptSsn));

        JsonDocument.Parse(restored.Json).RootElement.GetProperty("ssn").GetString()
            .ShouldBe("123-45-6789");
        JsonDocument.Parse(restored.Json).RootElement.GetProperty("name").GetString()
            .ShouldBe("Jane");
    }

    [Fact]
    public void Encrypt_UsesAFreshNonce_SoCiphertextDiffersEachTime()
    {
        // This is exactly why DataHash must be computed over plaintext: identical content encrypts
        // to different bytes, so a ciphertext hash would make every append look like a change.
        var cipher = Cipher();
        var input = Data("""{ "ssn": "123-45-6789" }""");

        cipher.Encrypt(input, EncryptSsn).Json.ShouldNotBe(cipher.Encrypt(input, EncryptSsn).Json);
    }

    [Fact]
    public void Encrypt_IsIdempotent_SoBackfillAndReAppendAreSafe()
    {
        var cipher = Cipher();
        var once = cipher.Encrypt(Data("""{ "ssn": "123-45-6789" }"""), EncryptSsn);
        var twice = cipher.Encrypt(once, EncryptSsn);

        twice.Json.ShouldBe(once.Json);
    }

    [Fact]
    public void Encrypt_ReachesArrayItemPaths()
    {
        var fields = new Dictionary<string, SensitiveFieldMetadata>
        {
            ["cards[].number"] = new() { Enabled = true, Purpose = "PCI", EncryptAtRest = true }
        };
        var cipher = Cipher();

        var stored = cipher.Encrypt(
            Data("""{ "cards": [ { "number": "4111" }, { "number": "5555" } ] }"""),
            fields);

        stored.Json.ShouldNotContain("4111");
        stored.Json.ShouldNotContain("5555");

        var restored = JsonDocument.Parse(cipher.Decrypt(stored).Json).RootElement
            .GetProperty("cards");
        restored[0].GetProperty("number").GetString().ShouldBe("4111");
        restored[1].GetProperty("number").GetString().ShouldBe("5555");
    }

    [Fact]
    public void Encrypt_WhenDisabled_IsAPassThrough()
        => Cipher(enabled: false).Encrypt(Data("""{ "ssn": "123-45-6789" }"""), EncryptSsn)
            .Json.ShouldContain("123-45-6789");

    [Fact]
    public void Decrypt_WorksEvenWhenEncryptionIsDisabled()
    {
        // Turning encryption off must never strand rows written while it was on.
        var stored = Cipher().Encrypt(Data("""{ "ssn": "123-45-6789" }"""), EncryptSsn);

        Cipher(enabled: false).Decrypt(stored).Json.ShouldContain("123-45-6789");
    }

    [Fact]
    public void Decrypt_LeavesPlaintextUntouched()
    {
        var plain = Data("""{ "name": "Jane" }""");
        Cipher().Decrypt(plain).Json.ShouldBe(plain.Json);
    }

    [Fact]
    public void Decrypt_WhenCiphertextIsMovedToAnotherField_Fails()
    {
        // The field path is bound as AAD, so a relocated ciphertext cannot decrypt into the wrong
        // place — it fails authentication instead.
        var cipher = Cipher();
        var stored = cipher.Encrypt(Data("""{ "ssn": "123-45-6789" }"""), EncryptSsn);
        var marker = JsonDocument.Parse(stored.Json).RootElement.GetProperty("ssn").GetString();

        var relocated = Data($"{{ \"other\": {JsonSerializer.Serialize(marker)} }}");

        Should.Throw<SensitiveDataEncryptionException>(() => cipher.Decrypt(relocated));
    }

    [Fact]
    public void Decrypt_WhenCiphertextIsTampered_Fails()
    {
        var cipher = Cipher();
        var stored = cipher.Encrypt(Data("""{ "ssn": "123-45-6789" }"""), EncryptSsn);
        var tampered = stored.Json.Replace("enc:v1:v1:A", "enc:v1:v1:B", StringComparison.Ordinal);

        if (string.Equals(tampered, stored.Json, StringComparison.Ordinal))
        {
            // Payload did not start with 'A'; flip a byte deterministically instead.
            var marker = JsonDocument.Parse(stored.Json).RootElement.GetProperty("ssn").GetString()!;
            var flipped = marker[..^1] + (marker[^1] == 'A' ? 'B' : 'A');
            tampered = $"{{ \"ssn\": {JsonSerializer.Serialize(flipped)} }}";
        }

        Should.Throw<SensitiveDataEncryptionException>(() => cipher.Decrypt(Data(tampered)));
    }

    [Fact]
    public void Decrypt_WhenKeyIsNotLoaded_FailsLoudly()
    {
        var written = Cipher(true, KeyId).Encrypt(Data("""{ "ssn": "123-45-6789" }"""), EncryptSsn);
        var readerWithoutTheKey = new SensitiveDataCipher(
            new FakeKeyProvider([OtherKeyId], OtherKeyId), isEnabled: true);

        var ex = Should.Throw<SensitiveDataEncryptionException>(() => readerWithoutTheKey.Decrypt(written));
        ex.Message.ShouldContain(KeyId);
    }

    [Fact]
    public void Decrypt_WhenMarkerIsMalformed_Fails()
        => Should.Throw<SensitiveDataEncryptionException>(
            () => Cipher().Decrypt(Data("""{ "ssn": "enc:v1:" }""")));

    [Fact]
    public void Encrypt_WhenEnabledWithoutAnActiveKey_Fails()
    {
        var cipher = new SensitiveDataCipher(new FakeKeyProvider([], activeKeyId: null), isEnabled: true);

        Should.Throw<SensitiveDataEncryptionException>(
            () => cipher.Encrypt(Data("""{ "ssn": "1" }"""), EncryptSsn));
    }

    [Fact]
    public void Encrypt_IgnoresFieldsMarkedSensitiveButNotEncrypted()
    {
        var redactOnly = new Dictionary<string, SensitiveFieldMetadata>
        {
            ["ssn"] = new() { Enabled = true, Purpose = "PII", RedactInLogs = true }
        };

        Cipher().Encrypt(Data("""{ "ssn": "123-45-6789" }"""), redactOnly)
            .Json.ShouldContain("123-45-6789");
    }

    [Fact]
    public void Encrypt_LeavesNonStringValuesAlone()
    {
        // encryptAtRest is restricted to type: string at publish time; if a number slips through,
        // silently mangling the document would be worse than leaving it.
        var fields = new Dictionary<string, SensitiveFieldMetadata>
        {
            ["amount"] = new() { Enabled = true, Purpose = "Financial", EncryptAtRest = true }
        };

        Cipher().Encrypt(Data("""{ "amount": 42 }"""), fields).Json.ShouldContain("42");
    }

    [Fact]
    public void NullCipher_PassesPlaintextButRefusesCiphertext()
    {
        var plain = Data("""{ "name": "Jane" }""");
        NullSensitiveDataCipher.Instance.Decrypt(plain).Json.ShouldBe(plain.Json);

        var stored = Cipher().Encrypt(Data("""{ "ssn": "123-45-6789" }"""), EncryptSsn);

        // Passing a marker through would hand 'enc:v1:...' to a caller as if it were the value.
        Should.Throw<SensitiveDataEncryptionException>(
            () => NullSensitiveDataCipher.Instance.Decrypt(stored));
    }

    [Fact]
    public void ContainsCiphertext_DetectsTheMarker()
    {
        ISensitiveDataCipher.ContainsCiphertext(null).ShouldBeFalse();
        ISensitiveDataCipher.ContainsCiphertext("""{ "a": 1 }""").ShouldBeFalse();
        ISensitiveDataCipher.ContainsCiphertext("""{ "a": "enc:v1:v1:xyz" }""").ShouldBeTrue();
    }

    private sealed class FakeKeyProvider(string[] keyIds, string? activeKeyId) : IDataEncryptionKeyProvider
    {
        private readonly Dictionary<string, DataEncryptionKey> _keys = BuildKeys(keyIds);

        private static Dictionary<string, DataEncryptionKey> BuildKeys(string[] ids)
        {
            var keys = new Dictionary<string, DataEncryptionKey>(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                // Deterministic, non-random material: Math.Random is unavailable and tests must not
                // depend on entropy for reproducibility.
                var material = new byte[DataEncryptionKey.RequiredKeyLength];
                for (var i = 0; i < material.Length; i++)
                    material[i] = (byte)(id.GetHashCode(StringComparison.Ordinal) + i);

                keys[id] = new DataEncryptionKey(id, material);
            }

            return keys;
        }

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public DataEncryptionKey? GetActive()
            => activeKeyId is not null && _keys.TryGetValue(activeKeyId, out var key) ? key : null;

        public bool TryGet(string keyId, out DataEncryptionKey key) => _keys.TryGetValue(keyId, out key!);
    }
}
