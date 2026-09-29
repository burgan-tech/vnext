using System;
using System.Text.Json;
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Unit tests for <see cref="ExecutionType"/> — the S/A flow/transition execution-mode value
/// object (vnext#1003). Covers <c>FromCode</c> (happy + corner cases), equality, and JSON round-trip
/// through <see cref="IEquatableJsonConverter{T}"/>.
/// </summary>
public sealed class ExecutionTypeTests
{
    [Theory]
    [InlineData("S")]
    [InlineData("s")]
    [InlineData("  S  ")]
    public void FromCode_ResolvesSync(string code) =>
        ExecutionType.FromCode(code).ShouldBe(ExecutionType.Sync);

    [Theory]
    [InlineData("A")]
    [InlineData("a")]
    [InlineData(" A ")]
    public void FromCode_ResolvesAsync(string code) =>
        ExecutionType.FromCode(code).ShouldBe(ExecutionType.Async);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SYNC")]   // the old code is no longer valid
    [InlineData("ASYNC")]  // the old code is no longer valid
    [InlineData("SYNCHRONOUS")]
    [InlineData("true")]
    [InlineData("BACKGROUND")]
    public void FromCode_UnknownValue_Throws(string code) =>
        Should.Throw<System.ArgumentException>(() => ExecutionType.FromCode(code));

    [Fact]
    public void Codes_AreCanonicalUpperCase()
    {
        ExecutionType.Sync.Code.ShouldBe("S");
        ExecutionType.Async.Code.ShouldBe("A");
    }

    [Fact]
    public void IsAsync_IsTrueOnlyForAsync()
    {
        ExecutionType.Async.IsAsync.ShouldBeTrue();
        ExecutionType.Sync.IsAsync.ShouldBeFalse();
    }

    [Fact]
    public void Equality_IsByCode()
    {
        ExecutionType.FromCode("A").ShouldBe(ExecutionType.Async);
        ExecutionType.Sync.ShouldNotBe(ExecutionType.Async);
        ExecutionType.Async.GetHashCode().ShouldBe(ExecutionType.FromCode("a").GetHashCode());
    }

    [Fact]
    public void Json_SerializesToCode() =>
        JsonSerializer.Serialize(ExecutionType.Async).ShouldBe("\"A\"");

    [Fact]
    public void Json_DeserializesFromCode() =>
        JsonSerializer.Deserialize<ExecutionType>("\"S\"").ShouldBe(ExecutionType.Sync);

    [Fact]
    public void Json_RoundTrips()
    {
        var json = JsonSerializer.Serialize(ExecutionType.Async);
        JsonSerializer.Deserialize<ExecutionType>(json).ShouldBe(ExecutionType.Async);
    }

    // --- Publish-time rejection of a bad authored value (vnext#1003) ---------------------------------
    // These pin what a domain team sees when it ships a wrong executionType. The value object is
    // resolved through FromCode via reflection (IEquatableJsonConverter), which wraps a thrown
    // ArgumentException in TargetInvocationException. The converter must unwrap it, or the publish
    // endpoint's ComponentValidatorProcessor (which catches ArgumentException) cannot turn it into a
    // clean field-scoped validation error and the author gets an opaque HTTP 500.

    [Theory]
    [InlineData("\"MAYBE\"")]
    [InlineData("\"SYNC\"")]   // the old code is no longer valid
    [InlineData("\"ASYNC\"")]  // the old code is no longer valid
    [InlineData("\"SYNCHRONOUS\"")]
    [InlineData("\"true\"")]
    [InlineData("\"BACKGROUND\"")]
    public void Json_DeserializeUnknownValue_ThrowsArgumentException_NotReflectionWrapper(string json)
    {
        // Must be ArgumentException (what the publish validator catches), NOT TargetInvocationException.
        var ex = Should.Throw<ArgumentException>(() => JsonSerializer.Deserialize<ExecutionType>(json));
        ex.ShouldNotBeOfType<System.Reflection.TargetInvocationException>();
        ex.Message.ShouldContain("Unknown execution type"); // the message the author actually needs
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    public void Json_DeserializeEmptyOrWhitespace_ThrowsJsonException(string json) =>
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<ExecutionType>(json));

    [Theory]
    [InlineData("123")]
    [InlineData("true")]
    public void Json_DeserializeNonString_ThrowsJsonException(string json) =>
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize<ExecutionType>(json));

    [Fact]
    public void Json_DeserializeNull_YieldsNull() =>
        // JSON null is not a bad value — the converter's Read is never invoked; the property is simply
        // absent, so the flow/transition falls back to the caller's sync query parameter.
        JsonSerializer.Deserialize<ExecutionType>("null").ShouldBeNull();
}
