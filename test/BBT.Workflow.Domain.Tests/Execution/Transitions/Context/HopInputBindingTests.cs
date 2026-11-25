using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using BBT.Workflow.Scripting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Execution.Transitions.Context;

/// <summary>
/// One source of truth for the expression variable <c>input</c>: whichever pipeline step builds the hop's shared
/// <see cref="ScriptContext"/> first (resource lock, a task step, the agent, the automatic rule), the context carries
/// <see cref="TransitionExecutionContext.HopInput"/> — not the step's own idea of the payload.
/// </summary>
public class HopInputBindingTests
{
    private static ScriptContext Plain() => new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
        .SetBody(new Dictionary<string, object> { ["seat"] = "from-body" })
        .Build();

    [Fact]
    public async Task GetOrBuild_BindsTheHopInput_OnTheFirstBuild_AndReusesIt()
    {
        var context = new TransitionExecutionContext { HopInput = JsonDocument.Parse("""{"seat":"3B"}""").RootElement };

        var first = await context.GetOrBuildScriptContextAsync(_ => Task.FromResult(Plain()));
        var second = await context.GetOrBuildScriptContextAsync(_ => Task.FromResult(Plain()));

        second.ShouldBeSameAs(first);
        first.HasHopInput.ShouldBeTrue();
        ((JsonElement)first.HopInput!).GetProperty("seat").GetString().ShouldBe("3B");
    }

    [Fact]
    public async Task GetOrBuild_BindsNull_WhenTheHopHasNoRequestPayload()
    {
        var context = new TransitionExecutionContext { Data = new Dictionary<string, object> { ["seat"] = "x" } };

        var built = await context.GetOrBuildScriptContextAsync(_ => Task.FromResult(Plain()));

        built.HasHopInput.ShouldBeTrue();
        built.HopInput.ShouldBeNull();
    }

    [Fact]
    public async Task WithHopInput_BindsOnAStandaloneBuild()
    {
        var built = await Task.FromResult(Plain()).WithHopInput("payload");

        built.HasHopInput.ShouldBeTrue();
        built.HopInput.ShouldBe("payload");
    }

    [Fact]
    public void CreateBranchFor_CarriesTheHopInput()
    {
        var root = Plain();
        root.BindHopInput("payload");

        var branch = root.CreateParallelBranch();

        branch.HasHopInput.ShouldBeTrue();
        branch.HopInput.ShouldBe("payload");
    }
}
