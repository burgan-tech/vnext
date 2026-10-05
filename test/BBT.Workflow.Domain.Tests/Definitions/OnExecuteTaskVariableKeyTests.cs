using System.Text.Json;
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Definitions;

/// <summary>
/// An entry's response slot in ScriptContext.TaskResponse is its authored variableKey, else the
/// legacy ToVariableName(task.key). Entries at one order run in parallel, so the slot is what keeps
/// two runs of the same task apart.
/// </summary>
public class OnExecuteTaskVariableKeyTests
{
    private static OnExecuteTask Parse(string variableKeyJson) =>
        JsonSerializer.Deserialize<OnExecuteTask>($$"""
        {
            "order": 1,
            "task": {"key": "subprocess-task-send-notification", "domain": "d", "flow": "sys-tasks", "version": "1.0.0"},
            "mapping": { "location": "./src/X.csx", "code": "cmV0dXJuIHRydWU7" }
            {{variableKeyJson}}
        }
        """, JsonSerializerConstants.JsonOptions)!;

    [Fact]
    public void ResponseVariableKey_WithoutVariableKey_FallsBackToTaskKeyVariableName()
    {
        var task = Parse(string.Empty);

        task.VariableKey.ShouldBeNull();
        task.ResponseVariableKey.ShouldBe("subprocessTaskSendNotification");
    }

    [Fact]
    public void ResponseVariableKey_WithVariableKey_UsesItVerbatim()
    {
        var task = Parse(""", "variableKey": "primaryChild" """);

        task.VariableKey.ShouldBe("primaryChild");
        task.ResponseVariableKey.ShouldBe("primaryChild");
    }

    [Fact]
    public void Serialize_DoesNotEmitTheComputedSlot()
    {
        var json = JsonSerializer.Serialize(Parse(""", "variableKey": "primaryChild" """), JsonSerializerConstants.JsonOptions);

        json.ShouldContain("\"variableKey\":\"primaryChild\"");
        json.ShouldNotContain("responseVariableKey", Case.Insensitive);
    }

    [Theory]
    [InlineData("primaryChild", true)]
    [InlineData("_child2", true)]
    [InlineData("primary-child", false)]
    [InlineData("1child", false)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    public void IsValidVariableKey_FollowsIdentifierPattern(string value, bool expected) =>
        OnExecuteTask.IsValidVariableKey(value).ShouldBe(expected);

    [Fact]
    public void IsValidVariableKey_RejectsOver100Characters() =>
        OnExecuteTask.IsValidVariableKey(new string('a', 101)).ShouldBeFalse();
}
