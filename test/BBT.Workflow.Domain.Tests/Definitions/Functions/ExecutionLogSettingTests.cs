using System.Text.Json;
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Unit tests for <see cref="ExecutionLogSetting"/> — the per-function execution-journal opt-in value
/// object. Covers <c>FromCode</c> resolution (happy + corner cases), value equality, and JSON round-trip
/// through <see cref="IEquatableJsonConverter{T}"/>.
/// </summary>
public sealed class ExecutionLogSettingTests
{
    [Theory]
    [InlineData("ENABLED")]
    [InlineData("enabled")]
    [InlineData("Enabled")]
    [InlineData("  ENABLED  ")]
    public void FromCode_ResolvesEnabled_CaseAndWhitespaceInsensitive(string code)
    {
        ExecutionLogSetting.FromCode(code).ShouldBe(ExecutionLogSetting.Enabled);
    }

    [Theory]
    [InlineData("DISABLED")]
    [InlineData("disabled")]
    [InlineData(" Disabled ")]
    public void FromCode_ResolvesDisabled_CaseAndWhitespaceInsensitive(string code)
    {
        ExecutionLogSetting.FromCode(code).ShouldBe(ExecutionLogSetting.Disabled);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ON")]
    [InlineData("true")]
    [InlineData("ENABLE")]
    public void FromCode_UnknownCode_Throws(string code)
    {
        Should.Throw<System.ArgumentException>(() => ExecutionLogSetting.FromCode(code));
    }

    [Fact]
    public void Codes_AreTheCanonicalUpperCaseForms()
    {
        ExecutionLogSetting.Enabled.Code.ShouldBe("ENABLED");
        ExecutionLogSetting.Disabled.Code.ShouldBe("DISABLED");
    }

    [Fact]
    public void Equality_IsByCode()
    {
        ExecutionLogSetting.FromCode("ENABLED").ShouldBe(ExecutionLogSetting.Enabled);
        ExecutionLogSetting.Enabled.ShouldNotBe(ExecutionLogSetting.Disabled);
        ExecutionLogSetting.Enabled.GetHashCode().ShouldBe(ExecutionLogSetting.FromCode("enabled").GetHashCode());
    }

    [Fact]
    public void Json_SerializesToCode()
    {
        var json = JsonSerializer.Serialize(ExecutionLogSetting.Enabled);

        json.ShouldBe("\"ENABLED\"");
    }

    [Fact]
    public void Json_DeserializesFromCode()
    {
        var setting = JsonSerializer.Deserialize<ExecutionLogSetting>("\"DISABLED\"");

        setting.ShouldBe(ExecutionLogSetting.Disabled);
    }

    [Fact]
    public void Json_RoundTrips()
    {
        var json = JsonSerializer.Serialize(ExecutionLogSetting.Enabled);
        var back = JsonSerializer.Deserialize<ExecutionLogSetting>(json);

        back.ShouldBe(ExecutionLogSetting.Enabled);
    }
}
