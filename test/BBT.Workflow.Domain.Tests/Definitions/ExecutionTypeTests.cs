using System.Text.Json;
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Unit tests for <see cref="ExecutionType"/> — the SYNC/ASYNC flow/transition execution-mode value
/// object (vnext#1003). Covers <c>FromCode</c> (happy + corner cases), equality, and JSON round-trip
/// through <see cref="IEquatableJsonConverter{T}"/>.
/// </summary>
public sealed class ExecutionTypeTests
{
    [Theory]
    [InlineData("SYNC")]
    [InlineData("sync")]
    [InlineData("Sync")]
    [InlineData("  SYNC  ")]
    public void FromCode_ResolvesSync(string code) =>
        ExecutionType.FromCode(code).ShouldBe(ExecutionType.Sync);

    [Theory]
    [InlineData("ASYNC")]
    [InlineData("async")]
    [InlineData(" Async ")]
    public void FromCode_ResolvesAsync(string code) =>
        ExecutionType.FromCode(code).ShouldBe(ExecutionType.Async);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SYNCHRONOUS")]
    [InlineData("true")]
    [InlineData("BACKGROUND")]
    public void FromCode_UnknownValue_Throws(string code) =>
        Should.Throw<System.ArgumentException>(() => ExecutionType.FromCode(code));

    [Fact]
    public void Codes_AreCanonicalUpperCase()
    {
        ExecutionType.Sync.Code.ShouldBe("SYNC");
        ExecutionType.Async.Code.ShouldBe("ASYNC");
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
        ExecutionType.FromCode("ASYNC").ShouldBe(ExecutionType.Async);
        ExecutionType.Sync.ShouldNotBe(ExecutionType.Async);
        ExecutionType.Async.GetHashCode().ShouldBe(ExecutionType.FromCode("async").GetHashCode());
    }

    [Fact]
    public void Json_SerializesToCode() =>
        JsonSerializer.Serialize(ExecutionType.Async).ShouldBe("\"ASYNC\"");

    [Fact]
    public void Json_DeserializesFromCode() =>
        JsonSerializer.Deserialize<ExecutionType>("\"SYNC\"").ShouldBe(ExecutionType.Sync);

    [Fact]
    public void Json_RoundTrips()
    {
        var json = JsonSerializer.Serialize(ExecutionType.Async);
        JsonSerializer.Deserialize<ExecutionType>(json).ShouldBe(ExecutionType.Async);
    }
}
