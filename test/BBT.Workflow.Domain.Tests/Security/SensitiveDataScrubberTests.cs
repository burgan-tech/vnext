using System.Collections.Generic;
using System.Text.Json;
using BBT.Workflow.Definitions.Schemas;
using BBT.Workflow.Security;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Security;

/// <summary>
/// Unit tests for <see cref="SensitiveDataScrubber"/> — the value-based redaction primitive.
/// </summary>
public sealed class SensitiveDataScrubberTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private static SensitiveFieldMetadata Redacted(string? pattern = null) => new()
    {
        Enabled = true,
        Purpose = "PII",
        RedactInLogs = true,
        MaskingPattern = pattern
    };

    [Fact]
    public void Create_WhenNothingAnnotated_ReturnsNone()
    {
        var scrubber = SensitiveDataScrubber.Create(
            Json("""{ "email": "jane@example.com" }"""),
            new Dictionary<string, SensitiveFieldMetadata>());

        scrubber.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void Create_WhenFieldIsNotMarkedForLogRedaction_ReturnsNone()
    {
        // encryptAtRest alone must not start scrubbing logs — the flags are independent.
        var fields = new Dictionary<string, SensitiveFieldMetadata>
        {
            ["email"] = new() { Enabled = true, Purpose = "PII", EncryptAtRest = true }
        };

        SensitiveDataScrubber.Create(Json("""{ "email": "jane@example.com" }"""), fields)
            .IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void Scrub_ReplacesValueWithItsMask()
    {
        var fields = new Dictionary<string, SensitiveFieldMetadata>
        {
            ["email"] = Redacted("{first}***@***.***")
        };
        var scrubber = SensitiveDataScrubber.Create(Json("""{ "email": "jane@example.com" }"""), fields);

        scrubber.Scrub("contacting jane@example.com now")
            .ShouldBe("contacting j***@***.*** now");
    }

    [Fact]
    public void Scrub_ReplacesEveryOccurrence()
    {
        var fields = new Dictionary<string, SensitiveFieldMetadata> { ["ssn"] = Redacted("***-**-{last4}") };
        var scrubber = SensitiveDataScrubber.Create(Json("""{ "ssn": "123-45-6789" }"""), fields);

        scrubber.Scrub("123-45-6789 vs 123-45-6789")
            .ShouldBe("***-**-6789 vs ***-**-6789");
    }

    [Fact]
    public void Scrub_FindsValuesInNestedObjects()
    {
        var fields = new Dictionary<string, SensitiveFieldMetadata> { ["customer.email"] = Redacted() };
        var scrubber = SensitiveDataScrubber.Create(
            Json("""{ "customer": { "email": "jane@example.com" } }"""),
            fields);

        scrubber.Scrub("jane@example.com").ShouldBe(SensitiveValueMasker.Redacted);
    }

    [Fact]
    public void Scrub_FansOutOverArrayItems()
    {
        // The array gap this feature's shared walker exists to close: every card's number must be
        // collected, not just the first.
        var fields = new Dictionary<string, SensitiveFieldMetadata>
        {
            ["cards[].number"] = Redacted("****{last4}")
        };
        var scrubber = SensitiveDataScrubber.Create(
            Json("""{ "cards": [ { "number": "4111111111111111" }, { "number": "5555444433332222" } ] }"""),
            fields);

        scrubber.Scrub("used 4111111111111111 then 5555444433332222")
            .ShouldBe("used ****1111 then ****2222");
    }

    [Fact]
    public void Scrub_ProtectsNumericValuesToo()
    {
        // A card or account number authored as a JSON number still lands in a log line as digits.
        var fields = new Dictionary<string, SensitiveFieldMetadata> { ["accountNumber"] = Redacted() };
        var scrubber = SensitiveDataScrubber.Create(Json("""{ "accountNumber": 1234567890 }"""), fields);

        scrubber.Scrub("account 1234567890").ShouldBe($"account {SensitiveValueMasker.Redacted}");
    }

    [Fact]
    public void Scrub_LeavesShortValuesAlone()
    {
        // A two-character value occurs incidentally everywhere; scrubbing it would shred the log
        // while protecting nothing. Documented as MinScrubbableLength.
        var fields = new Dictionary<string, SensitiveFieldMetadata> { ["code"] = Redacted() };
        var scrubber = SensitiveDataScrubber.Create(Json("""{ "code": "TR" }"""), fields);

        scrubber.IsEmpty.ShouldBeTrue();
        scrubber.Scrub("country TR is fine").ShouldBe("country TR is fine");
    }

    [Fact]
    public void Scrub_WhenOneValueContainsAnother_LongestWins()
    {
        var fields = new Dictionary<string, SensitiveFieldMetadata>
        {
            ["full"] = Redacted("[full]"),
            ["part"] = Redacted("[part]")
        };
        var scrubber = SensitiveDataScrubber.Create(
            Json("""{ "full": "abcdef123", "part": "abcdef" }"""),
            fields);

        scrubber.Scrub("abcdef123").ShouldBe("[full]");
    }

    [Fact]
    public void Scrub_WhenPathIsAbsentFromData_YieldsNothingToScrub()
    {
        var fields = new Dictionary<string, SensitiveFieldMetadata> { ["missing.path"] = Redacted() };

        SensitiveDataScrubber.Create(Json("""{ "email": "jane@example.com" }"""), fields)
            .IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public void Scrub_NullAndEmptyPassThrough()
    {
        var fields = new Dictionary<string, SensitiveFieldMetadata> { ["email"] = Redacted() };
        var scrubber = SensitiveDataScrubber.Create(Json("""{ "email": "jane@example.com" }"""), fields);

        scrubber.Scrub(null).ShouldBeNull();
        scrubber.Scrub(string.Empty).ShouldBe(string.Empty);
    }

    [Fact]
    public void ScrubArgument_PreservesTypeWhenNothingChanged()
    {
        var fields = new Dictionary<string, SensitiveFieldMetadata> { ["email"] = Redacted() };
        var scrubber = SensitiveDataScrubber.Create(Json("""{ "email": "jane@example.com" }"""), fields);

        // Structured sinks should still see a number, not a stringified one.
        scrubber.ScrubArgument(42).ShouldBe(42);
        scrubber.ScrubArgument(null).ShouldBeNull();
    }

    [Fact]
    public void ScrubArgument_ScrubsAJsonElementCarryingTheWholePayload()
    {
        // The realistic script case: logging context.Instance.Data wholesale.
        var data = Json("""{ "email": "jane@example.com", "state": "active" }""");
        var fields = new Dictionary<string, SensitiveFieldMetadata> { ["email"] = Redacted() };
        var scrubber = SensitiveDataScrubber.Create(data, fields);

        var scrubbed = scrubber.ScrubArgument(data)?.ToString();

        scrubbed.ShouldNotBeNull();
        scrubbed.ShouldNotContain("jane@example.com");
        scrubbed.ShouldContain("active");
    }

    [Fact]
    public void ScrubArguments_ReturnsSameArrayWhenNothingChanged()
    {
        var fields = new Dictionary<string, SensitiveFieldMetadata> { ["email"] = Redacted() };
        var scrubber = SensitiveDataScrubber.Create(Json("""{ "email": "jane@example.com" }"""), fields);

        object?[] args = [1, "harmless", null];
        scrubber.ScrubArguments(args).ShouldBeSameAs(args);
    }

    [Fact]
    public void ScrubArguments_CopiesOnlyWhenSomethingChanged()
    {
        var fields = new Dictionary<string, SensitiveFieldMetadata> { ["email"] = Redacted("[masked]") };
        var scrubber = SensitiveDataScrubber.Create(Json("""{ "email": "jane@example.com" }"""), fields);

        object?[] args = [1, "jane@example.com"];
        var scrubbed = scrubber.ScrubArguments(args);

        scrubbed.ShouldNotBeSameAs(args);
        scrubbed![0].ShouldBe(1);
        scrubbed[1].ShouldBe("[masked]");
        args[1].ShouldBe("jane@example.com", "the caller's array must not be mutated");
    }

    [Fact]
    public void None_IsAnIdentity()
    {
        SensitiveDataScrubber.None.IsEmpty.ShouldBeTrue();
        SensitiveDataScrubber.None.Scrub("jane@example.com").ShouldBe("jane@example.com");
        SensitiveDataScrubber.None.ScrubArgument("x").ShouldBe("x");
    }
}
