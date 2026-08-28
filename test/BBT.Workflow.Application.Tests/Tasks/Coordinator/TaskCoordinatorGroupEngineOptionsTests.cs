using System;
using System.Collections.Generic;
using BBT.Workflow.Definitions;
using BBT.Workflow.Scripting;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Tasks.Coordinator;

/// <summary>
/// Direct unit tests of <see cref="TaskCoordinator.ResolveGroupEngineOptions"/> — the helper that
/// gives each occurrence of a repeated task key within one Order group a distinct
/// <see cref="TaskEngineExecutionOptions.JournalTaskKey"/> suffix (see
/// <see cref="TaskCoordinatorDuplicateTaskKeyTests"/> for the end-to-end coordinator behavior this
/// backs). Exercised directly (via <c>InternalsVisibleTo</c>) because one of its rules — never
/// overwrite a <see cref="TaskEngineExecutionOptions.JournalTaskKey"/> the caller already set, the
/// way FanOut sets its own "key#index" — has no way to be triggered through
/// <see cref="TaskCoordinator"/>'s public API: the coordinator always builds its base options from
/// <see cref="TaskEngineExecutionOptions.Default"/>/<see cref="TaskEngineExecutionOptions.FreshTransitionRecord"/>,
/// both of which carry a null <c>JournalTaskKey</c>. This test pins the rule at the layer that
/// actually enforces it, as a defensive guarantee for any future caller that passes options with a
/// key already set.
/// </summary>
public sealed class TaskCoordinatorGroupEngineOptionsTests
{
    [Fact]
    public void ResolveGroupEngineOptions_RepeatedKey_SuffixesEveryOccurrenceByPosition()
    {
        var groupTasks = new List<OnExecuteTask>
        {
            OnExecuteTask.Create(0, WorkflowTaskFactory.CreateHttpTask("script-task"), ScriptCode.FromNative(string.Empty)),
            OnExecuteTask.Create(0, WorkflowTaskFactory.CreateHttpTask("script-task"), ScriptCode.FromNative(string.Empty)),
            OnExecuteTask.Create(0, WorkflowTaskFactory.CreateHttpTask("http-task"), ScriptCode.FromNative(string.Empty)),
        };

        var resolved = TaskCoordinator.ResolveGroupEngineOptions(groupTasks, TaskEngineExecutionOptions.Default);

        resolved.Count.ShouldBe(3);
        resolved[0].JournalTaskKey.ShouldBe("script-task#0");
        resolved[1].JournalTaskKey.ShouldBe("script-task#1");
        // The unique key in the same group keeps its bare key.
        resolved[2].JournalTaskKey.ShouldBeNull();
    }

    [Fact]
    public void ResolveGroupEngineOptions_NoRepeatedKey_ReturnsBaseOptionsUnchangedByReference()
    {
        var groupTasks = new List<OnExecuteTask>
        {
            OnExecuteTask.Create(0, WorkflowTaskFactory.CreateHttpTask("first"), ScriptCode.FromNative(string.Empty)),
            OnExecuteTask.Create(0, WorkflowTaskFactory.CreateHttpTask("second"), ScriptCode.FromNative(string.Empty)),
        };
        var baseOptions = TaskEngineExecutionOptions.Default;

        var resolved = TaskCoordinator.ResolveGroupEngineOptions(groupTasks, baseOptions);

        // No churn for the common case: the SAME instance is reused, not a `with`-cloned copy.
        ReferenceEquals(resolved[0], baseOptions).ShouldBeTrue();
        ReferenceEquals(resolved[1], baseOptions).ShouldBeTrue();
    }

    [Fact]
    public void ResolveGroupEngineOptions_GroupOfOne_ReturnsBaseOptionsUnchanged()
    {
        var groupTasks = new List<OnExecuteTask>
        {
            OnExecuteTask.Create(1, WorkflowTaskFactory.CreateHttpTask("remote-task"), ScriptCode.FromNative(string.Empty)),
        };
        var baseOptions = TaskEngineExecutionOptions.FreshTransitionRecord;

        var resolved = TaskCoordinator.ResolveGroupEngineOptions(groupTasks, baseOptions);

        resolved.Count.ShouldBe(1);
        ReferenceEquals(resolved[0], baseOptions).ShouldBeTrue();
    }

    /// <summary>
    /// A <see cref="TaskEngineExecutionOptions.JournalTaskKey"/> the caller already set (the FanOut
    /// shape, e.g. "fan-out-docs#3") must never be clobbered by this helper's own suffixing —
    /// even for a repeated key.
    /// </summary>
    [Fact]
    public void ResolveGroupEngineOptions_PreSetJournalTaskKey_IsNeverOverwritten()
    {
        var groupTasks = new List<OnExecuteTask>
        {
            OnExecuteTask.Create(0, WorkflowTaskFactory.CreateHttpTask("script-task"), ScriptCode.FromNative(string.Empty)),
            OnExecuteTask.Create(0, WorkflowTaskFactory.CreateHttpTask("script-task"), ScriptCode.FromNative(string.Empty)),
        };
        var presetOptions = TaskEngineExecutionOptions.Default with { JournalTaskKey = "fan-out-docs#3" };

        var resolved = TaskCoordinator.ResolveGroupEngineOptions(groupTasks, presetOptions);

        resolved[0].JournalTaskKey.ShouldBe("fan-out-docs#3");
        resolved[1].JournalTaskKey.ShouldBe("fan-out-docs#3");
    }

    /// <summary>
    /// A <see cref="TaskEngineExecutionOptions.ResponseVariableKey"/> the caller already set (the
    /// extension path's per-task override) must survive this helper untouched — including for a
    /// repeated task key, where the <see cref="TaskEngineExecutionOptions.JournalTaskKey"/>
    /// disambiguation must still happen ALONGSIDE it, not instead of it. Mirrors
    /// <see cref="ResolveGroupEngineOptions_PreSetJournalTaskKey_IsNeverOverwritten"/> for the new
    /// field: the helper's `options with { JournalTaskKey = ... }` only ever touches that one
    /// property, so every other caller-set field (this one included) rides through unchanged.
    /// </summary>
    [Fact]
    public void ResolveGroupEngineOptions_PreSetResponseVariableKey_IsNeverOverwritten_EvenWithDuplicateTaskKeys()
    {
        var groupTasks = new List<OnExecuteTask>
        {
            OnExecuteTask.Create(0, WorkflowTaskFactory.CreateHttpTask("script-task"), ScriptCode.FromNative(string.Empty)),
            OnExecuteTask.Create(0, WorkflowTaskFactory.CreateHttpTask("script-task"), ScriptCode.FromNative(string.Empty)),
        };
        var presetOptions = TaskEngineExecutionOptions.Default with { ResponseVariableKey = "extension-output-key" };

        var resolved = TaskCoordinator.ResolveGroupEngineOptions(groupTasks, presetOptions);

        // ResponseVariableKey survives untouched on every occurrence...
        resolved[0].ResponseVariableKey.ShouldBe("extension-output-key");
        resolved[1].ResponseVariableKey.ShouldBe("extension-output-key");
        // ...while the JournalTaskKey duplicate-suffix logic still runs alongside it.
        resolved[0].JournalTaskKey.ShouldBe("script-task#0");
        resolved[1].JournalTaskKey.ShouldBe("script-task#1");
    }

    /// <summary>
    /// The helper must never INVENT a <see cref="TaskEngineExecutionOptions.ResponseVariableKey"/>
    /// on its own — it is opt-in only. A group with no caller-set value (the overwhelming common
    /// case: every onExecute/onEntry/onExit task) keeps it null even when the JournalTaskKey
    /// disambiguation fires for a duplicate key.
    /// </summary>
    [Fact]
    public void ResolveGroupEngineOptions_NoResponseVariableKeySet_NeverInventsOne()
    {
        var groupTasks = new List<OnExecuteTask>
        {
            OnExecuteTask.Create(0, WorkflowTaskFactory.CreateHttpTask("script-task"), ScriptCode.FromNative(string.Empty)),
            OnExecuteTask.Create(0, WorkflowTaskFactory.CreateHttpTask("script-task"), ScriptCode.FromNative(string.Empty)),
        };

        var resolved = TaskCoordinator.ResolveGroupEngineOptions(groupTasks, TaskEngineExecutionOptions.Default);

        resolved[0].ResponseVariableKey.ShouldBeNull();
        resolved[1].ResponseVariableKey.ShouldBeNull();
    }
}
