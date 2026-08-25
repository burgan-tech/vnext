using BBT.Workflow.Security;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Security;

/// <summary>
/// Unit tests for <see cref="SensitiveValueMasker"/>. The invariant that matters is that the raw
/// value can never survive: every rejected input degrades to the fixed placeholder.
/// </summary>
public sealed class SensitiveValueMaskerTests
{
    [Theory]
    // The two patterns the feature was specified with.
    [InlineData("jane.doe@example.com", "{first}***@***.***", "j***@***.***")]
    [InlineData("123-45-6789", "***-**-{last4}", "***-**-6789")]
    // Explicit counts, both edges.
    [InlineData("4111111111111111", "{first4}********{last4}", "4111********1111")]
    [InlineData("abcdef", "{first2}...{last2}", "ab...ef")]
    // A bare token reveals exactly one character.
    [InlineData("abcdef", "{first}****", "a****")]
    [InlineData("abcdef", "****{last}", "****f")]
    // A constant pattern reveals nothing and is legal.
    [InlineData("abcdef", "[redacted]", "[redacted]")]
    public void Mask_AppliesPattern(string value, string pattern, string expected)
        => SensitiveValueMasker.Mask(value, pattern).ShouldBe(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Mask_WhenValueIsBlank_ReturnsPlaceholder(string? value)
        => SensitiveValueMasker.Mask(value, "{last4}").ShouldBe(SensitiveValueMasker.Redacted);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Mask_WhenPatternIsMissing_ReturnsPlaceholder(string? pattern)
        => SensitiveValueMasker.Mask("123-45-6789", pattern).ShouldBe(SensitiveValueMasker.Redacted);

    [Theory]
    [InlineData("{middle}")]
    [InlineData("{first0}")]
    [InlineData("{last100}")]
    [InlineData("{First}")]
    [InlineData("***-{last")]
    public void Mask_WhenPatternIsInvalid_FailsClosedToPlaceholder(string pattern)
    {
        // Failing open would emit the pattern with the raw value spliced in — the exact leak the
        // masker exists to prevent.
        SensitiveValueMasker.Mask("123456789", pattern).ShouldBe(SensitiveValueMasker.Redacted);
        SensitiveValueMasker.TryValidatePattern(pattern, out var error).ShouldBeFalse();
        error.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Mask_WhenValueShorterThanRequestedCount_RevealsOnlyWhatExists()
        => SensitiveValueMasker.Mask("42", "***{last4}").ShouldBe("***42");

    [Fact]
    public void Mask_WhenPatternWouldRenderNothing_ReturnsPlaceholder()
    {
        // "{last4}" against an empty-after-trim value cannot happen (blank is short-circuited),
        // but a pattern of only tokens against a value that yields nothing must not emit "".
        SensitiveValueMasker.Mask("a", "{first0}").ShouldBe(SensitiveValueMasker.Redacted);
    }

    [Fact]
    public void Mask_NeverReturnsTheRawValue()
    {
        const string value = "4111111111111111";
        foreach (var pattern in new[] { "{first}***", "***{last4}", "[x]", "{bogus}", null, "" })
        {
            SensitiveValueMasker.Mask(value, pattern).ShouldNotBe(value);
        }
    }

    [Theory]
    [InlineData("{first}***@***.***")]
    [InlineData("***-**-{last4}")]
    [InlineData("no tokens at all")]
    [InlineData("{first99}{last1}")]
    public void TryValidatePattern_AcceptsSupportedPatterns(string pattern)
    {
        SensitiveValueMasker.TryValidatePattern(pattern, out var error).ShouldBeTrue();
        error.ShouldBeNull();
    }

    [Fact]
    public void TryValidatePattern_RejectsBraceWithoutToken()
    {
        // Far more likely a mistyped token than intended literal text.
        SensitiveValueMasker.TryValidatePattern("***{", out var error).ShouldBeFalse();
        error.ShouldNotBeNullOrWhiteSpace();
    }
}
