using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.Logging;
using Moq;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Scripting;

/// <summary>
/// Pins the Katman 2 / Task 4 copy-on-write branch contract (B6): a parallel branch shares the
/// parent's <c>Body</c> by reference until its first Body write, gets container copies (values
/// shared) of the response dictionaries, never writes into a parent-shared structure, and its
/// Dispose clears only what it owns.
/// </summary>
public class ScriptContextCowBranchTests
{
    private static ScriptContext ParentContext()
    {
        var context = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>())
            .SetBody(new { Name = "initial", Nested = new { Value = 1 } })
            .SetHeaders(new Dictionary<string, string> { ["clientid"] = "mobile" })
            .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
            .Build();

        context.SetStandardResponse(new StandardTaskResponse
        {
            Data = new { Seed = "parent" },
            IsSuccess = true
        }, "seedTask");

        return context;
    }

    private static string BodyJson(ScriptContext context) =>
        JsonSerializer.Serialize((object?)context.Body, ScriptContext.JsonScriptBodyOptions);

    [Fact]
    public void Branch_NoWrites_SharesBodyByReference()
    {
        var parent = ParentContext();

        var branch = parent.CreateParallelBranch();

        Assert.Same((object)parent.Body!, (object)branch.Body!);
    }

    [Fact]
    public void BranchFor_RetargetsTransition_ParentKeepsItsOwn()
    {
        // ScheduleTransitionsStep evaluates several transition-scoped timer scripts against ONE
        // built context: each branch must see ITS transition while the parent stays untouched.
        var parent = ParentContext();
        var scheduled = Transition.Create("auto-cancel", "waiting", "cancelled", TriggerType.Scheduled, "Patch");

        var branch = parent.CreateBranchFor(scheduled);

        Assert.Same(scheduled, branch.Transition);
        Assert.NotSame(scheduled, parent.Transition);
        // Still a COW branch: unwritten payload is shared by reference.
        Assert.Same((object)parent.Body!, (object)branch.Body!);
    }

    [Fact]
    public void Branch_WriteToBody_CopiesOnFirstWrite_ParentUntouched()
    {
        var parent = ParentContext();
        var parentBodyBefore = BodyJson(parent);
        var parentBodyRef = (object)parent.Body!;

        var branch = parent.CreateParallelBranch();
        branch.SetStandardResponse(new StandardTaskResponse
        {
            Data = new { BranchOnly = true },
            IsSuccess = true
        }, "branchTask");

        Assert.NotSame(parentBodyRef, (object)branch.Body!);
        Assert.Same(parentBodyRef, (object)parent.Body!);
        Assert.Equal(parentBodyBefore, BodyJson(parent));
        // The branch actually observed its own write.
        Assert.Contains("branchOnly", BodyJson(branch));
    }

    [Fact]
    public void Branch_TaskResponseAdd_IsolatedFromParent()
    {
        var parent = ParentContext();

        var branch = parent.CreateParallelBranch();
        branch.SetStandardResponse(new StandardTaskResponse { Data = new { X = 1 } }, "branchTask");
        branch.SetOutputResponse(new { Y = 2 }, "branchOutput");

        branch.TaskResponse.ContainsKey("branchTask").ShouldBeTrue();
        branch.OutputResponse.ContainsKey("branchOutput").ShouldBeTrue();
        parent.TaskResponse.ContainsKey("branchTask").ShouldBeFalse();
        parent.OutputResponse.ContainsKey("branchOutput").ShouldBeFalse();
        // Pre-existing entries came across.
        branch.TaskResponse.ContainsKey("seedTask").ShouldBeTrue();
    }

    [Fact]
    public async Task ConcurrentBranches_WriteIndependently()
    {
        var parent = ParentContext();
        var parentBodyBefore = BodyJson(parent);
        var parentBodyRef = (object)parent.Body!;

        var branches = Enumerable.Range(0, 8).Select(_ => parent.CreateParallelBranch()).ToArray();

        await Task.WhenAll(branches.Select((branch, i) => Task.Run(() =>
            branch.SetStandardResponse(new StandardTaskResponse
            {
                Data = new { Index = i },
                IsSuccess = true
            }, $"task{i}"))));

        for (var i = 0; i < branches.Length; i++)
        {
            Assert.NotSame(parentBodyRef, (object)branches[i].Body!);
            Assert.Contains($"\"index\":{i}", BodyJson(branches[i]));
            branches[i].TaskResponse.ContainsKey($"task{i}").ShouldBeTrue();
        }

        Assert.Same(parentBodyRef, (object)parent.Body!);
        Assert.Equal(parentBodyBefore, BodyJson(parent));
        parent.TaskResponse.Keys.ShouldBe(new[] { "seedTask" });
    }

    [Fact]
    public void Branch_Dispose_DoesNotClearSharedParts()
    {
        var parent = ParentContext();
        var parentBodyRef = (object)parent.Body!;
        var parentBodyBefore = BodyJson(parent);

        var branch = parent.CreateParallelBranch();
        branch.Dispose();

        Assert.Same(parentBodyRef, (object)parent.Body!);
        Assert.Equal(parentBodyBefore, BodyJson(parent));
        parent.TaskResponse.ContainsKey("seedTask").ShouldBeTrue();
        parent.MetaData.ShouldNotBeNull();
        Assert.Null(branch.Body);
        Assert.Empty(branch.TaskResponse);
    }

    [Fact]
    public void MergeParallelBranch_NewSlot_IsMergedIntoResponsesAndBody()
    {
        var parent = ParentContext();

        var branch = parent.CreateParallelBranch();
        branch.SetStandardResponse(new StandardTaskResponse
        {
            Data = new { BranchResult = "ok" },
            IsSuccess = true
        }, "branchTask");

        parent.MergeParallelBranch(branch);

        parent.TaskResponse.ContainsKey("branchTask").ShouldBeTrue();
        Assert.Contains("branchResult", BodyJson(parent));
    }

    /// <summary>
    /// A later round re-writing a slot an earlier round left behind is an overwrite, exactly as a
    /// sequential run would be — the merge must not compare it against the inherited value.
    /// </summary>
    [Fact]
    public void MergeParallelBranches_InheritedSlotRewrittenByOneBranch_Overwrites()
    {
        var parent = ParentContext(); // seedTask already present

        var rewriter = parent.CreateParallelBranch();
        rewriter.SetStandardResponse(new StandardTaskResponse { Data = new { Seed = "rewritten" }, IsSuccess = true }, "seedTask");
        var sibling = parent.CreateParallelBranch();
        sibling.SetStandardResponse(new StandardTaskResponse { Data = new { Other = 1 }, IsSuccess = true }, "otherTask");

        Should.NotThrow(() => parent.MergeParallelBranches([rewriter, sibling]));

        JsonSerializer.Serialize((object?)parent.TaskResponse["seedTask"], ScriptContext.JsonScriptBodyOptions)
            .ShouldContain("rewritten");
        parent.TaskResponse.ContainsKey("otherTask").ShouldBeTrue();
    }

    [Fact]
    public void MergeParallelBranches_UntouchedInheritedSlot_KeepsItsValueByReference()
    {
        var parent = ParentContext();
        // object-typed so ReferenceEquals binds statically (a dynamic operand makes the call
        // dynamic, and Shouldly's extension method cannot bind on a dynamic receiver).
        object? before = parent.TaskResponse["seedTask"];

        var branch = parent.CreateParallelBranch();
        branch.SetStandardResponse(new StandardTaskResponse { Data = new { X = 1 }, IsSuccess = true }, "otherTask");

        parent.MergeParallelBranches([branch]);

        ReferenceEquals(before, (object?)parent.TaskResponse["seedTask"]).ShouldBeTrue();
    }

    [Fact]
    public void MergeParallelBranches_TwoBranchesWriteOneSlotDifferently_Conflicts()
    {
        var parent = ParentContext();

        var first = parent.CreateParallelBranch();
        first.SetStandardResponse(new StandardTaskResponse { Data = new { V = 1 }, IsSuccess = true }, "sharedTask");
        var second = parent.CreateParallelBranch();
        second.SetStandardResponse(new StandardTaskResponse { Data = new { V = 2 }, IsSuccess = true }, "sharedTask");

        var ex = Assert.Throws<InvalidOperationException>(() => parent.MergeParallelBranches([first, second]));
        ex.Message.ShouldBe("Parallel tasks produced conflicting output for key 'sharedTask'.");
    }

    [Fact]
    public void MergeParallelBranches_TwoBranchesRewriteInheritedSlotDifferently_Conflicts()
    {
        var parent = ParentContext();

        var first = parent.CreateParallelBranch();
        first.SetStandardResponse(new StandardTaskResponse { Data = new { Seed = "a" }, IsSuccess = true }, "seedTask");
        var second = parent.CreateParallelBranch();
        second.SetStandardResponse(new StandardTaskResponse { Data = new { Seed = "b" }, IsSuccess = true }, "seedTask");

        Assert.Throws<InvalidOperationException>(() => parent.MergeParallelBranches([first, second]));
    }

    [Fact]
    public void MergeParallelBranches_InheritedMetadataRewrittenByOneBranch_Overwrites_TwoWritersConflict()
    {
        var parent = ParentContext();
        parent.MetaData["trace"] = "parent";

        var rewriter = parent.CreateParallelBranch();
        rewriter.MetaData["trace"] = "branch";
        parent.MergeParallelBranches([rewriter]);
        ((string)parent.MetaData["trace"]).ShouldBe("branch");

        var a = parent.CreateParallelBranch();
        a.MetaData["trace"] = "a";
        var b = parent.CreateParallelBranch();
        b.MetaData["trace"] = "b";
        var ex = Assert.Throws<InvalidOperationException>(() => parent.MergeParallelBranches([a, b]));
        ex.Message.ShouldBe("Parallel tasks produced conflicting metadata for key 'trace'.");
    }

    /// <summary>
    /// B7: JsonEquivalent's JsonElement fast-path (JsonElement.DeepEquals) must reach the same
    /// no-conflict verdict as the legacy serialize-and-compare path for two structurally identical
    /// documents — including when their property order differs, since DeepEquals compares object
    /// members regardless of declaration order. Both branches of one round write the slot as a raw
    /// JsonElement directly on their dictionaries (bypassing CloneDynamic, which would otherwise
    /// round-trip a JsonElement into an ExpandoObject) so the fast path — which requires BOTH
    /// operands to still be JsonElement — is actually exercised between the two branches, not the
    /// legacy fallback.
    /// </summary>
    [Fact]
    public void MergeParallelBranch_JsonElementValues_StructurallyEquivalent_NoConflict()
    {
        var parent = ParentContext();
        var first = parent.CreateParallelBranch();
        first.TaskResponse["sharedTask"] = JsonDocument.Parse("""{"a":1,"b":[1,2,3]}""").RootElement;
        var second = parent.CreateParallelBranch();
        // Same content, different property order — DeepEquals must still consider these equal.
        second.TaskResponse["sharedTask"] = JsonDocument.Parse("""{"b":[1,2,3],"a":1}""").RootElement;

        Should.NotThrow(() => parent.MergeParallelBranches([first, second]));
    }

    /// <summary>
    /// B7: the fast-path must still detect genuine differences between two branches of one round —
    /// the conflict contract is not weakened by swapping the comparison mechanism.
    /// </summary>
    [Fact]
    public void MergeParallelBranch_JsonElementValues_StructurallyDifferent_Conflicts()
    {
        var parent = ParentContext();
        var first = parent.CreateParallelBranch();
        first.TaskResponse["sharedTask"] = JsonDocument.Parse("""{"a":1}""").RootElement;
        var second = parent.CreateParallelBranch();
        second.TaskResponse["sharedTask"] = JsonDocument.Parse("""{"a":2}""").RootElement;

        Assert.Throws<InvalidOperationException>(() => parent.MergeParallelBranches([first, second]));
    }

    /// <summary>
    /// The COW safety argument rests on Body writes funneling through <c>MergeToBody</c> and the
    /// dictionaries being container-copied at branch creation. If the public mutation surface of
    /// <see cref="ScriptContext"/> grows, this snapshot fails and the new writer must be reviewed
    /// for branch ownership (EnsureBodyOwned / container copy) before the list is updated.
    /// </summary>
    [Fact]
    public void PublicWriterSurface_IsPinned()
    {
        var writers = typeof(ScriptContext)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        writers.ShouldBe(new[]
        {
            // CreateBranchFor: CreateParallelBranch + Transition retarget, for evaluating several
            // transition-scoped scripts (scheduled-transition timers) against one built context.
            "CreateBranchFor",
            "CreateParallelBranch",
            "Dispose",
            "DisposeAsync",
            "MergeParallelBranch",
            "MergeParallelBranches",
            "RefreshInstance",
            "SetBody",
            "SetOutputResponse",
            "SetStandardResponse"
        });
    }

    [Fact]
    public void MergeParallelBranches_SlotConflict_LeavesTheParentUntouched()
    {
        var parent = ParentContext();
        object? seedBefore = parent.TaskResponse["seedTask"];
        var bodyBefore = BodyJson(parent);

        var writesNew = parent.CreateParallelBranch();
        writesNew.SetStandardResponse(new StandardTaskResponse { Data = new { Fresh = 1 }, IsSuccess = true }, "freshTask");
        var first = parent.CreateParallelBranch();
        first.SetStandardResponse(new StandardTaskResponse { Data = new { V = 1 }, IsSuccess = true }, "sharedTask");
        var second = parent.CreateParallelBranch();
        second.SetStandardResponse(new StandardTaskResponse { Data = new { V = 2 }, IsSuccess = true }, "sharedTask");

        Assert.Throws<InvalidOperationException>(() => parent.MergeParallelBranches([writesNew, first, second]));

        Assert.False(parent.TaskResponse.ContainsKey("freshTask"));
        Assert.False(parent.TaskResponse.ContainsKey("sharedTask"));
        Assert.True(ReferenceEquals(seedBefore, (object?)parent.TaskResponse["seedTask"]));
        Assert.Equal(bodyBefore, BodyJson(parent));
    }
}
