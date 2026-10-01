using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BBT.Workflow.Authorization;
using BBT.Workflow.Definitions.Schemas;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Masking;

/// <summary>
/// Conformance suite for the SDK-backed masking engine — the condition under which the TasmanianDevil
/// dependency was accepted. Every vector here is an exact expected string: an SDK upgrade that changes any
/// output fails this suite before it can change what a caller is shown.
/// </summary>
public sealed class TasmanianDevilFieldMaskingEngineTests
{
    private readonly TasmanianDevilFieldMaskingEngine _sut = new();

    private static FieldMaskRule Mask(int keepFirst = 0, int keepLast = 0, string ch = "*") =>
        new(FieldMaskRule.MaskOperator, ch, keepFirst, keepLast, null, []);

    private static FieldMaskRule Replace(string? value) =>
        new(FieldMaskRule.ReplaceOperator, "*", 0, 0, value, []);

    [Theory]
    [InlineData("TR330006100519786457841326", 2, 4, "*", "TR********************1326")]
    [InlineData("12345678901", 0, 3, "*", "********901")]
    [InlineData("5321234567", 3, 2, "#", "532#####67")]
    [InlineData("secret", 0, 0, "*", "******")]
    [InlineData("Çağrı Şenöz", 1, 1, "*", "Ç*********z")]
    [InlineData("abc", 2, 2, "*", "abc")]   // keep window wider than the value → unchanged (documented)
    [InlineData("abcd", 2, 2, "*", "abcd")]
    [InlineData("", 0, 0, "*", "")]
    public void Mask_KeepsTheWindowAndMasksTheMiddle(string value, int keepFirst, int keepLast, string ch, string expected)
        => _sut.Apply(Mask(keepFirst, keepLast, ch), value).ShouldBe(expected);

    [Fact]
    public void Mask_NeverSplitsASurrogatePair_AndMasksAstralCharactersPerCodeUnit()
    {
        // keepFirst/keepLast count Unicode scalar values; the SDK then masks per UTF-16 unit.
        _sut.Apply(Mask(keepFirst: 1, keepLast: 1), "😀ab😀").ShouldBe("😀**😀");
        _sut.Apply(Mask(keepFirst: 1), "a😀b").ShouldBe("a***");
        _sut.Apply(Mask(), "😀").ShouldBe("**");
    }

    [Fact]
    public void Replace_WritesTheConfiguredValue()
        => _sut.Apply(Replace("[gizli]"), "4111111111111111").ShouldBe("[gizli]");

    [Fact]
    public void Replace_WithoutValue_NeverReturnsTheInput()
        => _sut.Apply(Replace(null), "4111111111111111").ShouldNotContain("4111");

    [Fact]
    public void Apply_IsDeterministic_BecauseTheCacheAndEtagAssumeIt()
    {
        var rule = Mask(keepLast: 4);
        Enumerable.Range(0, 50).Select(_ => _sut.Apply(rule, "TR330006100519786457841326"))
            .Distinct().Count().ShouldBe(1);
    }

    [Fact]
    public void Validate_AcceptsPhaseOneRules()
    {
        _sut.Validate(Mask(2, 4, "#")).ShouldBeEmpty();
        _sut.Validate(Replace("[x]")).ShouldBeEmpty();
    }

    [Fact]
    public void Validate_RejectsReplaceWithoutAValue()
        => _sut.Validate(Replace("")).ShouldHaveSingleItem().ShouldContain("non-empty params.value");

    [Fact]
    public void Validate_RejectsAMultiCharacterMaskingChar_ThroughTheSdk()
        => _sut.Validate(Mask(ch: "**")).ShouldHaveSingleItem().ShouldContain("masking_char");

    // ── x-encryption reaches the engine only by mistake ──────────────────────

    /// <summary>
    /// hash is applied on write and encrypt by the read filter; if either rule still reaches the engine it must come
    /// back fully masked, never in clear.
    /// </summary>
    [Theory]
    [InlineData(FieldMaskRule.HashOperator)]
    [InlineData(FieldMaskRule.EncryptOperator)]
    public void AnXEncryptionRule_IsFullyMasked(string op)
        => new TasmanianDevilFieldMaskingEngine().Apply(new FieldMaskRule(op, "*", 0, 0, null, []), "12345678901")
            .ShouldBe("***********");

}
