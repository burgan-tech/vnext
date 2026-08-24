using System;
using System.Collections.Generic;
using System.Reflection;
using BBT.Workflow;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Infrastructure.Security;
using BBT.Workflow.Security;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Security;

/// <summary>
/// Pins the "does this row need work?" decision that makes the maintenance pass idempotent.
/// <para>
/// It cannot be a string comparison: a fresh nonce makes every re-encryption differ byte-for-byte,
/// so comparing the rewritten payload to the stored one would rewrite every row on every pass —
/// forever, and pointlessly.
/// </para>
/// </summary>
public sealed class EncryptionMaintenanceCurrencyTests
{
    private const string ActiveKeyId = "v2";

    private static bool IsAlreadyCurrent(string stored, string rewritten, string? activeKeyId)
    {
        var method = typeof(InstanceDataEncryptionMaintenanceService)
            .GetMethod("IsAlreadyCurrent", BindingFlags.NonPublic | BindingFlags.Static)!;
        return (bool)method.Invoke(null, [stored, rewritten, activeKeyId])!;
    }

    [Fact]
    public void PlainRowThatShouldStayPlain_NeedsNoWork()
        => IsAlreadyCurrent("""{ "name": "Jane" }""", """{ "name": "Jane" }""", ActiveKeyId)
            .ShouldBeTrue();

    [Fact]
    public void PlainRowThatShouldBeEncrypted_NeedsBackfill()
        => IsAlreadyCurrent("""{ "ssn": "123-45-6789" }""", """{ "ssn": "enc:v1:v2:abc" }""", ActiveKeyId)
            .ShouldBeFalse();

    [Fact]
    public void RowUnderTheActiveKey_NeedsNoWork()
    {
        // Note the payloads differ (fresh nonce) yet the row is current — this is the case a naive
        // string comparison would get wrong, rewriting every row on every pass.
        IsAlreadyCurrent(
                """{ "ssn": "enc:v1:v2:AAAA" }""",
                """{ "ssn": "enc:v1:v2:BBBB" }""",
                ActiveKeyId)
            .ShouldBeTrue();
    }

    [Fact]
    public void RowUnderARetiredKey_NeedsRotation()
        => IsAlreadyCurrent(
                """{ "ssn": "enc:v1:v1:AAAA" }""",
                """{ "ssn": "enc:v1:v2:BBBB" }""",
                ActiveKeyId)
            .ShouldBeFalse();

    [Fact]
    public void RowWithAMixOfKeys_NeedsRotation()
        => IsAlreadyCurrent(
                """{ "ssn": "enc:v1:v2:AAAA", "pan": "enc:v1:v1:CCCC" }""",
                """{ "ssn": "enc:v1:v2:BBBB", "pan": "enc:v1:v2:DDDD" }""",
                ActiveKeyId)
            .ShouldBeFalse();

    [Fact]
    public void EncryptedRowWhoseFieldIsNoLongerSensitive_NeedsRewrite()
        => IsAlreadyCurrent("""{ "ssn": "enc:v1:v2:AAAA" }""", """{ "ssn": "123-45-6789" }""", ActiveKeyId)
            .ShouldBeFalse();

    [Fact]
    public void EncryptedRowWithNoActiveKey_IsLeftAlone()
        => IsAlreadyCurrent("""{ "ssn": "enc:v1:v1:AAAA" }""", """{ "ssn": "enc:v1:v1:AAAA" }""", activeKeyId: null)
            .ShouldBeTrue();

    [Fact]
    public void RetentionExpiry_IsCountedOnlyForElapsedWindows()
    {
        var method = typeof(InstanceDataEncryptionMaintenanceService)
            .GetMethod("CountExpiredRetentionValues", BindingFlags.NonPublic | BindingFlags.Static)!;

        var fields = new Dictionary<string, SensitiveFieldMetadata>
        {
            ["ssn"] = new() { Enabled = true, Purpose = "PII", RetentionDays = 30 },
            ["email"] = new() { Enabled = true, Purpose = "PII", RetentionDays = 3650 },
            ["name"] = new() { Enabled = true, Purpose = "PII" }
        };
        var data = new JsonData("""{ "ssn": "1234567", "email": "a@b.c", "name": "Jane" }""");

        var expired = (int)method.Invoke(null, [data, fields, DateTime.UtcNow.AddDays(-100)])!;
        expired.ShouldBe(1, "only the 30-day window has elapsed");

        var fresh = (int)method.Invoke(null, [data, fields, DateTime.UtcNow.AddDays(-1)])!;
        fresh.ShouldBe(0);
    }

    [Fact]
    public void RetentionExpiry_IgnoresPathsWithNoValue()
    {
        var method = typeof(InstanceDataEncryptionMaintenanceService)
            .GetMethod("CountExpiredRetentionValues", BindingFlags.NonPublic | BindingFlags.Static)!;

        var fields = new Dictionary<string, SensitiveFieldMetadata>
        {
            ["missing"] = new() { Enabled = true, Purpose = "PII", RetentionDays = 1 }
        };

        var expired = (int)method.Invoke(
            null, [new JsonData("""{ "name": "Jane" }"""), fields, DateTime.UtcNow.AddDays(-100)])!;

        expired.ShouldBe(0);
    }
}
