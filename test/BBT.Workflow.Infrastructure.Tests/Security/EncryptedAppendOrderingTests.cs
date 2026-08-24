using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow;
using BBT.Workflow.Data;
using BBT.Workflow.Definitions;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Instances;
using BBT.Workflow.Security;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Security;

/// <summary>
/// Pins the load-bearing step order of an encrypted instance-data append.
/// <para>
/// The write funnel must decrypt the head, merge and hash on plaintext, validate on plaintext, and
/// only then encrypt. Every one of those steps is easy to reorder during a refactor and the damage
/// is silent — which is what these tests exist to prevent.
/// </para>
/// </summary>
public sealed class EncryptedAppendOrderingTests
{
    private const string KeyId = "v1";

    private static readonly Dictionary<string, SensitiveFieldMetadata> EncryptSsn = new()
    {
        ["ssn"] = new SensitiveFieldMetadata { Enabled = true, Purpose = "PII", EncryptAtRest = true }
    };

    private static SensitiveDataCipher Cipher() => new(new FixedKeyProvider(), isEnabled: true);

    [Fact]
    public void DedupSurvivesEncryption_WhenHeadIsDecryptedBeforeMerging()
    {
        // This is THE test for the step order. GCM uses a fresh nonce per value, so identical
        // content encrypts to different bytes; if the hash or the merge ever saw ciphertext, every
        // append would look like a change and the instance would grow a row per no-op write.
        var cipher = Cipher();
        var content = new JsonData("""{ "ssn": "123-45-6789", "name": "Jane" }""");
        var stored = cipher.Encrypt(content, EncryptSsn);

        var headAsReadFromSql = new InstanceDataHeadRow
        {
            Version = "1.0.0",
            DataHash = InstanceData.ComputeDataHash(content),
            Data = stored.Json
        };

        // Step 2 of the order: decrypt the head.
        var decryptedHead = new InstanceDataHeadRow
        {
            Version = headAsReadFromSql.Version,
            DataHash = headAsReadFromSql.DataHash,
            Data = cipher.Decrypt(new JsonData(headAsReadFromSql.Data)).Json
        };

        // Steps 3 and 4: merge and hash-compare on plaintext.
        var plan = InstanceDataWriteService.PlanAppend(
            decryptedHead,
            new JsonData("""{ "name": "Jane" }"""),
            VersionStrategy.None,
            legacyPipeline: false);

        plan.IsDuplicate.ShouldBeTrue("a no-op delta over an encrypted head must still dedup");
    }

    [Fact]
    public void SkippingTheHeadDecrypt_BreaksDedup()
    {
        // The failure mode the previous test guards against, asserted directly so the guard cannot
        // be weakened into a tautology.
        var cipher = Cipher();
        var content = new JsonData("""{ "ssn": "123-45-6789", "name": "Jane" }""");
        var stored = cipher.Encrypt(content, EncryptSsn);

        var ciphertextHead = new InstanceDataHeadRow
        {
            Version = "1.0.0",
            DataHash = InstanceData.ComputeDataHash(content),
            Data = stored.Json
        };

        var plan = InstanceDataWriteService.PlanAppend(
            ciphertextHead,
            new JsonData("""{ "name": "Jane" }"""),
            VersionStrategy.None,
            legacyPipeline: false);

        plan.IsDuplicate.ShouldBeFalse("merging a ciphertext head cannot match the plaintext hash");
    }

    [Fact]
    public void MergingAnEncryptedHeadWouldCorruptTheDocument()
    {
        // Beyond dedup: a full-merge over ciphertext would persist the marker as if it were the
        // value, so the next read would decrypt a value that was never written.
        var cipher = Cipher();
        var stored = cipher.Encrypt(new JsonData("""{ "ssn": "123-45-6789" }"""), EncryptSsn);

        var merged = new JsonData(stored.Json).Merge(new JsonData("""{ "name": "Jane" }"""));

        merged.Json.ShouldContain(SensitiveDataCipher.MarkerPrefix);
        cipher.Decrypt(merged).Json.ShouldContain("123-45-6789");
    }

    [Fact]
    public void InstanceDataRow_HashesPlaintextButStoresCiphertext()
    {
        var cipher = Cipher();
        var content = new JsonData("""{ "ssn": "123-45-6789", "name": "Jane" }""");
        var stored = cipher.Encrypt(content, EncryptSsn);

        var row = new InstanceData(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "1.0.0",
            stored,
            InstanceData.ComputeDataHash(content),
            isLatest: true);

        row.StoredData.Json.ShouldNotContain("123-45-6789");
        row.DataHash.ShouldBe(InstanceData.ComputeDataHash(content));
        row.HasSameData(content).ShouldBeTrue("change detection must still work against plaintext");
    }

    [Fact]
    public void InstanceDataRow_DataPropertyDecryptsThroughTheAccessor()
    {
        var cipher = Cipher();
        var content = new JsonData("""{ "ssn": "123-45-6789", "name": "Jane" }""");
        var stored = cipher.Encrypt(content, EncryptSsn);

        var row = new InstanceData(
            Guid.NewGuid(), Guid.NewGuid(), "1.0.0", stored,
            InstanceData.ComputeDataHash(content), isLatest: true);

        SensitiveDataCipherAccessor.Configure(cipher);
        try
        {
            // The single seam: every one of the ~20 consumers reads this property.
            JsonDocument.Parse(row.Data.Json).RootElement.GetProperty("ssn").GetString()
                .ShouldBe("123-45-6789");

            // Memoised — a second read must not re-decrypt into a different object.
            row.Data.ShouldBeSameAs(row.Data);
        }
        finally
        {
            SensitiveDataCipherAccessor.Reset();
        }
    }

    [Fact]
    public void InstanceDataRow_WithoutAConfiguredCipher_RefusesToServeCiphertext()
    {
        var stored = Cipher().Encrypt(new JsonData("""{ "ssn": "123-45-6789" }"""), EncryptSsn);
        var row = new InstanceData(
            Guid.NewGuid(), Guid.NewGuid(), "1.0.0", stored, "hash", isLatest: true);

        SensitiveDataCipherAccessor.Reset();

        Should.Throw<SensitiveDataEncryptionException>(() => _ = row.Data);
    }

    [Fact]
    public void Snapshot_CarriesTheStoredPayload()
    {
        var cipher = Cipher();
        var stored = cipher.Encrypt(new JsonData("""{ "ssn": "123-45-6789" }"""), EncryptSsn);
        var row = new InstanceData(
            Guid.NewGuid(), Guid.NewGuid(), "1.0.0", stored, "hash", isLatest: true);

        var snapshot = row.CreateSnapshot();

        snapshot.StoredData.Json.ShouldBe(stored.Json);

        // The payload is shared by reference on purpose (see InstanceData.CreateSnapshot): JsonData
        // is immutable and carries the parse/normalize memos, and the plaintext memo lives on the
        // row, not on the payload — so sharing cannot leak a decrypt across the two rows.
        snapshot.StoredData.ShouldBeSameAs(row.StoredData);
    }

    private sealed class FixedKeyProvider : IDataEncryptionKeyProvider
    {
        private readonly DataEncryptionKey _key = new(KeyId, BuildKey());

        private static byte[] BuildKey()
        {
            var material = new byte[DataEncryptionKey.RequiredKeyLength];
            for (var i = 0; i < material.Length; i++)
                material[i] = (byte)(i * 7 + 1);
            return material;
        }

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public DataEncryptionKey? GetActive() => _key;

        public bool TryGet(string keyId, out DataEncryptionKey key)
        {
            key = _key;
            return string.Equals(keyId, KeyId, StringComparison.Ordinal);
        }
    }
}
