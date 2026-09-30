using System;
using System.Linq;
using System.Text.Json;
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Unit tests for <see cref="ArrayMergeStrategy"/> — the per-transition R/M array-combination value
/// object (vnext-client-sdk-core#58, AB-18). Covers <c>FromCode</c>, equality, the JSON round-trip
/// through <see cref="IEquatableJsonConverter{T}"/>, and the binding onto <see cref="Transition"/>.
/// Modelled on <see cref="ExecutionTypeTests"/>, which pins the same contract for <c>executionType</c>.
/// </summary>
public sealed class ArrayMergeStrategyTests
{
    [Theory]
    [InlineData("R")]
    [InlineData("r")]
    [InlineData("  R  ")]
    public void FromCode_ResolvesReplace(string code) =>
        ArrayMergeStrategy.FromCode(code).ShouldBe(ArrayMergeStrategy.Replace);

    [Theory]
    [InlineData("M")]
    [InlineData("m")]
    [InlineData(" M ")]
    public void FromCode_ResolvesMerge(string code) =>
        ArrayMergeStrategy.FromCode(code).ShouldBe(ArrayMergeStrategy.Merge);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("merge")]     // the description, not the code
    [InlineData("REPLACE")]   // ditto
    [InlineData("X")]
    [InlineData("true")]
    public void FromCode_UnknownValue_Throws(string code) =>
        Should.Throw<ArgumentException>(() => ArrayMergeStrategy.FromCode(code))
            .Message.ShouldContain("Unknown array merge strategy");

    [Fact]
    public void Codes_AreCanonicalUpperCase()
    {
        ArrayMergeStrategy.Replace.Code.ShouldBe("R");
        ArrayMergeStrategy.Merge.Code.ShouldBe("M");
    }

    [Fact]
    public void IsMerge_IsTrueOnlyForMerge()
    {
        ArrayMergeStrategy.Merge.IsMerge.ShouldBeTrue();
        ArrayMergeStrategy.Replace.IsMerge.ShouldBeFalse();
    }

    [Fact]
    public void Equality_IsByCode()
    {
        ArrayMergeStrategy.FromCode("M").ShouldBe(ArrayMergeStrategy.Merge);
        ArrayMergeStrategy.Replace.ShouldNotBe(ArrayMergeStrategy.Merge);
        ArrayMergeStrategy.Merge.GetHashCode().ShouldBe(ArrayMergeStrategy.FromCode("m").GetHashCode());
    }

    [Fact]
    public void Json_SerializesToCode() =>
        JsonSerializer.Serialize(ArrayMergeStrategy.Merge).ShouldBe("\"M\"");

    [Fact]
    public void Json_RoundTrips()
    {
        var json = JsonSerializer.Serialize(ArrayMergeStrategy.Merge);
        JsonSerializer.Deserialize<ArrayMergeStrategy>(json).ShouldBe(ArrayMergeStrategy.Merge);
    }

    /// <summary>
    /// The publish-rejection contract. A mistyped value must surface as <see cref="ArgumentException"/>
    /// carrying the real message — that is what the publish endpoint's <c>ComponentValidatorProcessor</c>
    /// catches and turns into a clean field-scoped 400. Reflection wraps it in
    /// <c>TargetInvocationException</c>, so the converter has to unwrap; without that the author gets an
    /// opaque HTTP 500 instead. It matters more here than for most settings: R and M are opposite
    /// data-retention trade-offs, so a value that silently fell back to the default would quietly restore
    /// the very data loss the author set out to prevent.
    /// </summary>
    [Theory]
    [InlineData("\"merge\"")]
    [InlineData("\"REPLACE\"")]
    [InlineData("\"X\"")]
    [InlineData("\"true\"")]
    public void Json_DeserializeUnknownValue_ThrowsArgumentException_NotReflectionWrapper(string json)
    {
        var ex = Should.Throw<ArgumentException>(() => JsonSerializer.Deserialize<ArrayMergeStrategy>(json));
        ex.ShouldNotBeOfType<System.Reflection.TargetInvocationException>();
        ex.Message.ShouldContain("Unknown array merge strategy");
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    public void Json_DeserializeEmptyOrWhitespace_ThrowsJsonException(string json) =>
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<ArrayMergeStrategy>(json));

    [Theory]
    [InlineData("123")]
    [InlineData("true")]
    public void Json_DeserializeNonString_ThrowsJsonException(string json) =>
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<ArrayMergeStrategy>(json));

    [Fact]
    public void Json_DeserializeNull_YieldsNull() =>
        // JSON null is not a bad value — the converter's Read is never invoked and the transition simply
        // has no arrayMerge, which the write path reads as today's wholesale replace.
        JsonSerializer.Deserialize<ArrayMergeStrategy>("null").ShouldBeNull();

    // --- Binding onto the transition --------------------------------------------------------------
    // Without these, the rejection tests above would still pass if `arrayMerge` were never wired into
    // Transition's [JsonConstructor] at all — the property would just stay null forever.

    [Theory]
    [InlineData("R", false)]
    [InlineData("M", true)]
    public void Transition_BindsTheAuthoredCode(string authored, bool expectedIsMerge) =>
        DeserializeTransition($$"""{"key":"go","target":"done","arrayMerge":"{{authored}}"}""")
            .ArrayMerge.ShouldNotBeNull().IsMerge.ShouldBe(expectedIsMerge);

    /// <summary>
    /// Absent is the non-breaking default, and it is what every definition in every domain looks like
    /// today: nothing bound, so the write path keeps its historical wholesale replace.
    /// </summary>
    [Fact]
    public void Transition_LeavesArrayMergeNull_WhenNotDeclared() =>
        DeserializeTransition("""{"key":"go","target":"done"}""")
            .ArrayMerge.ShouldBeNull();

    [Fact]
    public void Transition_RoundTripsTheCode()
    {
        var transition = DeserializeTransition("""{"key":"go","target":"done","arrayMerge":"M"}""");

        JsonSerializer.Serialize(transition, JsonSerializerConstants.JsonOptions)
            .ShouldContain("\"arrayMerge\":\"M\"");
    }

    /// <summary>A transition that never declared it must not start emitting one.</summary>
    [Fact]
    public void Transition_OmitsArrayMergeFromJson_WhenNotDeclared() =>
        JsonSerializer.Serialize(
                DeserializeTransition("""{"key":"go","target":"done"}"""),
                JsonSerializerConstants.JsonOptions)
            .ShouldNotContain("arrayMerge");

    private static Transition DeserializeTransition(string json) =>
        JsonSerializer.Deserialize<Transition>(json, JsonSerializerConstants.JsonOptions)!;
}
