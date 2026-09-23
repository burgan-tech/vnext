using System.Collections.Generic;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BBT.Workflow.Scripting;

/// <summary>
/// Pins the per-task ownership of <c>ScriptContext.TaskResponse</c> slots
/// (vnext-client-sdk-core#6 — "iki task in one function collide in TaskResponse").
/// <see cref="ScriptContext.SetStandardResponse"/> merges every response into the shared
/// <c>Body</c>, and <c>ExpandoObjectMergeStrategy</c> mutates its merge target in place — so a
/// slot that shares structure with <c>Body</c> is rewritten by the NEXT task's merge and every
/// slot ends up carrying the last task's payload. Each slot must own an isolated copy: written
/// once by its task, immune to later Body merges, and never a write-through into Body.
/// </summary>
public class TaskResponseSlotIsolationTests
{
    private static ScriptContext EmptyContext() =>
        new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>()).Build();

    private static ScriptContext ContextWithRequestBody() =>
        new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>())
            .SetBody(new { RequestField = "request-payload" })
            .Build();

    private static StandardTaskResponse Response(string source, int amount, int statusCode) => new()
    {
        IsSuccess = true,
        Data = new { Source = source, Amount = amount },
        StatusCode = statusCode
    };

    private static JsonElement AsJson(object? value) =>
        JsonSerializer.SerializeToElement(value, ScriptContext.JsonScriptBodyOptions);

    [Fact]
    public void SequentialResponses_EachSlotKeepsItsOwnPayload()
    {
        // The A1 repro: two tasks of one function complete in sequence on the shared context.
        var context = EmptyContext();

        context.SetStandardResponse(Response("task-one", 1, 200), "taskOne");
        context.SetStandardResponse(Response("task-two", 2, 201), "taskTwo");

        var slotOne = AsJson(context.TaskResponse["taskOne"]);
        var slotTwo = AsJson(context.TaskResponse["taskTwo"]);

        Assert.Equal("task-one", slotOne.GetProperty("data").GetProperty("source").GetString());
        Assert.Equal(1, slotOne.GetProperty("data").GetProperty("amount").GetInt32());
        Assert.Equal(200, slotOne.GetProperty("statusCode").GetInt32());

        Assert.Equal("task-two", slotTwo.GetProperty("data").GetProperty("source").GetString());
        Assert.Equal(201, slotTwo.GetProperty("statusCode").GetInt32());
    }

    [Fact]
    public void SequentialResponses_WithRequestBody_EachSlotKeepsItsOwnPayload()
    {
        // Same repro over a non-null Body (a function invoked with a request body): the first
        // response is then absorbed into Body subtree-by-reference instead of becoming Body itself.
        var context = ContextWithRequestBody();

        context.SetStandardResponse(Response("task-one", 1, 200), "taskOne");
        context.SetStandardResponse(Response("task-two", 2, 201), "taskTwo");

        var slotOne = AsJson(context.TaskResponse["taskOne"]);

        Assert.Equal("task-one", slotOne.GetProperty("data").GetProperty("source").GetString());
        Assert.Equal(200, slotOne.GetProperty("statusCode").GetInt32());
    }

    [Fact]
    public void Body_StillAccumulatesResponses_LastWinsOnCollidingFields()
    {
        // Existing Body semantics are unchanged by the slot isolation: Body keeps accumulating
        // every response, later fields overwriting earlier same-named ones.
        var context = EmptyContext();

        context.SetStandardResponse(Response("task-one", 1, 200), "taskOne");
        context.SetStandardResponse(Response("task-two", 2, 201), "taskTwo");

        var body = AsJson((object?)context.Body);

        Assert.Equal("task-two", body.GetProperty("data").GetProperty("source").GetString());
        Assert.Equal(201, body.GetProperty("statusCode").GetInt32());
    }

    [Fact]
    public void LaterBodyMerge_DoesNotCorruptExistingSlot()
    {
        // A plain SetBody after a task completed (e.g. the next task's input mapping merging its
        // request) must not reach into the completed task's slot either.
        var context = EmptyContext();

        context.SetStandardResponse(Response("task-one", 1, 200), "taskOne");
        context.SetBody(new { Data = new { Source = "input-mapping" } });

        var slotOne = AsJson(context.TaskResponse["taskOne"]);

        Assert.Equal("task-one", slotOne.GetProperty("data").GetProperty("source").GetString());
    }

    [Fact]
    public void SlotMutation_DoesNotLeakIntoBody()
    {
        // Ownership is two-way: a script mutating a TaskResponse entry in place must not
        // write through into Body.
        var context = EmptyContext();

        context.SetStandardResponse(Response("task-one", 1, 200), "taskOne");

        var slot = (IDictionary<string, object?>)context.TaskResponse["taskOne"]!;
        var slotData = (IDictionary<string, object?>)slot["data"]!;
        slotData["source"] = "mutated-by-script";

        var body = AsJson((object?)context.Body);

        Assert.Equal("task-one", body.GetProperty("data").GetProperty("source").GetString());
    }
}
