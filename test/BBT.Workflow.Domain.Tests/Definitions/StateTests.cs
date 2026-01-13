using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace BBT.Workflow.Definitions;

public class StateTests
{
    [Fact]
    public void Create_ShouldInitializeProperties()
    {
        // Arrange & Act
        var state = State.Create("test-state", StateType.Intermediate, StateSubType.Success, "Patch");

        // Assert
        Assert.Equal("test-state", state.Key);
        Assert.Equal(StateType.Intermediate, state.StateType);
        Assert.Equal(VersionStrategy.IncreasePatch, state.VersionStrategy);
        Assert.NotNull(state.Labels);
        Assert.Empty(state.Labels);
        Assert.NotNull(state.Transitions);
        Assert.Empty(state.Transitions);
        Assert.NotNull(state.OnEntries);
        Assert.Empty(state.OnEntries);
        Assert.NotNull(state.OnExits);
        Assert.Empty(state.OnExits);
    }

    [Theory]
    [InlineData(StateType.Initial)]
    [InlineData(StateType.Intermediate)]
    [InlineData(StateType.Finish)]
    [InlineData(StateType.SubFlow)]
    public void Create_ShouldAcceptAllStateTypes(StateType stateType)
    {
        // Act
        var state = State.Create("test-state", stateType, StateSubType.Success, "Patch");

        // Assert
        Assert.Equal(stateType, state.StateType);
    }

    [Theory]
    [InlineData("Minor")]
    [InlineData("Major")]
    [InlineData("Patch")]
    public void Create_ShouldAcceptAllVersionStrategies(string versionStrategy)
    {
        // Act
        var state = State.Create("test-state", StateType.Intermediate, StateSubType.Success, versionStrategy);

        // Assert
        Assert.NotNull(state.VersionStrategy);
        Assert.Equal(versionStrategy, state.VersionStrategy.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Create_ShouldThrowException_WhenKeyIsInvalid(string? key)
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => 
            State.Create(key!, StateType.Intermediate, StateSubType.Success, VersionStrategy.IncreasePatch.Code));
    }

    [Fact]
    public void Create_ShouldThrowException_WhenKeyExceedsMaxLength()
    {
        // Arrange
        var key = new string('a', StateConstants.MaxKeyLength + 1);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => 
            State.Create(key, StateType.Intermediate, StateSubType.Success, VersionStrategy.IncreasePatch.Code));
    }

    [Fact]
    public void AddLanguage_ShouldAddNewLanguage()
    {
        // Arrange
        var state = State.Create("test-state", StateType.Intermediate, StateSubType.Success, "Patch");

        // Act
        state.AddLanguage("Test State", "en");

        // Assert
        Assert.Single(state.Labels);
        Assert.Equal("Test State", state.Labels.First().Label);
        Assert.Equal("en", state.Labels.First().Language);
    }

    [Fact]
    public void AddLanguage_ShouldReplaceExistingLanguage()
    {
        // Arrange
        var state = State.Create("test-state", StateType.Intermediate, StateSubType.Success, "Patch");
        state.AddLanguage("Original Label", "en");

        // Act
        state.AddLanguage("Updated Label", "en");

        // Assert
        Assert.Single(state.Labels);
        Assert.Equal("Updated Label", state.Labels.First().Label);
    }

    [Fact]
    public void AddLanguage_ShouldAddMultipleLanguages()
    {
        // Arrange
        var state = State.Create("test-state", StateType.Intermediate, StateSubType.Success, "Patch");

        // Act
        state.AddLanguage("English State", "en");
        state.AddLanguage("Turkish State", "tr");
        state.AddLanguage("German State", "de");

        // Assert
        Assert.Equal(3, state.Labels.Count);
    }

    [Fact]
    public void AddTransition_ShouldAddTransitionToState()
    {
        // Arrange
        var state = State.Create("test-state", StateType.Intermediate, StateSubType.Success, "Patch");
        var transition = Transition.Create("submit", "test-state", "next-state", TriggerType.Manual, "Patch");

        // Act
        state.AddTransition(transition);

        // Assert
        Assert.Single(state.Transitions);
        Assert.Equal("submit", state.Transitions.First().Key);
    }

    [Fact]
    public void AddTransition_ShouldAddMultipleTransitions()
    {
        // Arrange
        var state = State.Create("test-state", StateType.Intermediate, StateSubType.Success, "Patch");
        var transition1 = Transition.Create("submit", "test-state", "next", TriggerType.Manual, "Patch");
        var transition2 = Transition.Create("cancel", "test-state", "cancelled", TriggerType.Manual, "Patch");

        // Act
        state.AddTransition(transition1);
        state.AddTransition(transition2);

        // Assert
        Assert.Equal(2, state.Transitions.Count);
    }

    [Fact]
    public void FindTransition_ShouldReturnTransition_WhenExists()
    {
        // Arrange
        var state = State.Create("test-state", StateType.Intermediate, StateSubType.Success, "Patch");
        var transition = Transition.Create("submit", "test-state", "next-state", TriggerType.Manual, "Patch");
        state.AddTransition(transition);

        // Act
        var result = state.FindTransition("submit");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("submit", result.Key);
    }

    [Fact]
    public void FindTransition_ShouldReturnNull_WhenDoesNotExist()
    {
        // Arrange
        var state = State.Create("test-state", StateType.Intermediate, StateSubType.Success, "Patch");

        // Act
        var result = state.FindTransition("non-existent");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void AutoTransitions_ShouldReturnOnlyAutomaticTransitions()
    {
        // Arrange
        var state = State.Create("test-state", StateType.Intermediate, StateSubType.Success, "Patch");
        var autoTransition = Transition.Create("auto", "test-state", "next", TriggerType.Automatic, "Patch");
        var manualTransition = Transition.Create("manual", "test-state", "other", TriggerType.Manual, "Patch");
        state.AddTransition(autoTransition);
        state.AddTransition(manualTransition);

        // Act
        var result = state.AutoTransitions.ToList();

        // Assert
        Assert.Single(result);
        Assert.Equal("auto", result.First().Key);
    }

    [Fact]
    public void ScheduledTransitions_ShouldReturnOnlyScheduledTransitions()
    {
        // Arrange
        var state = State.Create("test-state", StateType.Intermediate, StateSubType.Success, "Patch");
        var scheduledTransition = Transition.Create("scheduled", "test-state", "next", TriggerType.Scheduled, "Patch");
        var manualTransition = Transition.Create("manual", "test-state", "other", TriggerType.Manual, "Patch");
        state.AddTransition(scheduledTransition);
        state.AddTransition(manualTransition);

        // Act
        var result = state.ScheduledTransitions.ToList();

        // Assert
        Assert.Single(result);
        Assert.Equal("scheduled", result.First().Key);
    }

    [Fact]
    public void TransitionKeys_ShouldReturnAllTransitionKeys()
    {
        // Arrange
        var state = State.Create("test-state", StateType.Intermediate, StateSubType.Success, "Patch");
        var transition1 = Transition.Create("submit", "test-state", "next", TriggerType.Manual, "Patch");
        var transition2 = Transition.Create("cancel", "test-state", "cancelled", TriggerType.Manual, "Patch");
        state.AddTransition(transition1);
        state.AddTransition(transition2);

        // Act
        var result = state.TransitionKeys();

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains("submit", result);
        Assert.Contains("cancel", result);
    }

    [Fact]
    public void SetView_ShouldSetViewReference()
    {
        // Arrange
        var state = State.Create("test-state", StateType.Intermediate, StateSubType.Success, "Patch");
        var viewRef = new Reference("view-1", "domain", "sys-views", "1.0.0");
        var viewDefinition = new ViewDefinition([], true, viewRef);

        // Act
        state.SetView(viewDefinition);

        // Assert
        Assert.NotNull(state.View);
        Assert.Equal("view-1", state.View.View.Key);
    }

    [Fact]
    public void SetSubFlow_ShouldSetSubFlowConfiguration()
    {
        // Arrange
        var state = State.Create("test-state", StateType.SubFlow, StateSubType.Success, "Patch");
        var reference = new Reference("sub-flow", "domain", "sys-flows", "1.0.0");
        var mapping = new ScriptCode("location", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("code")));
        var viewOverrides = new Dictionary<string, Reference>()
        {
            {"view-test",  new Reference("view-1", "domain", "sys-views", "1.0.0")}
        };

        // Act
        state.SetSubFlow("S", reference, mapping, viewOverrides);

        // Assert
        Assert.NotNull(state.SubFlow);
        Assert.Equal(SubFlowType.SubFlow, state.SubFlow.Type);
        Assert.Equal("sub-flow", state.SubFlow.Process.Key);
    }

    [Fact]
    public void AddOnEntry_ShouldAddOnEntryTask()
    {
        // Arrange
        var state = State.Create("test-state", StateType.Intermediate, StateSubType.Success, "Patch");
        var taskReference = new Reference("task-1", "domain", "sys-tasks", "1.0.0");
        var mapping = new ScriptCode("location", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("code")));
        var task = OnExecuteTask.Create(1, taskReference, mapping);

        // Act
        state.AddOnEntry(task);

        // Assert
        Assert.Single(state.OnEntries);
        Assert.Equal("task-1", state.OnEntries.First().Task.Key);
    }

    [Fact]
    public void AddOnExit_ShouldAddOnExitTask()
    {
        // Arrange
        var state = State.Create("test-state", StateType.Intermediate, StateSubType.Success, "Patch");
        var taskReference = new Reference("task-1", "domain", "sys-tasks", "1.0.0");
        var mapping = new ScriptCode("location", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("code")));
        var task = OnExecuteTask.Create(1, taskReference, mapping);

        // Act
        state.AddOnExit(task);

        // Assert
        Assert.Single(state.OnExits);
        Assert.Equal("task-1", state.OnExits.First().Task.Key);
    }

    [Fact]
    public void OnEntries_ShouldBeReadOnly()
    {
        // Arrange
        var state = State.Create("test-state", StateType.Intermediate, StateSubType.Success, "Patch");

        // Act & Assert
        Assert.IsAssignableFrom<IReadOnlyCollection<OnExecuteTask>>(state.OnEntries);
    }

    [Fact]
    public void OnExits_ShouldBeReadOnly()
    {
        // Arrange
        var state = State.Create("test-state", StateType.Intermediate, StateSubType.Success, "Patch");

        // Act & Assert
        Assert.IsAssignableFrom<IReadOnlyCollection<OnExecuteTask>>(state.OnExits);
    }

    [Fact]
    public void Transitions_ShouldBeReadOnly()
    {
        // Arrange
        var state = State.Create("test-state", StateType.Intermediate, StateSubType.Success, "Patch");

        // Act & Assert
        Assert.IsAssignableFrom<IReadOnlyCollection<Transition>>(state.Transitions);
    }

    [Fact]
    public void Labels_ShouldBeReadOnly()
    {
        // Arrange
        var state = State.Create("test-state", StateType.Intermediate, StateSubType.Success, "Patch");

        // Act & Assert
        Assert.IsAssignableFrom<IReadOnlyCollection<LanguageLabel>>(state.Labels);
    }

    [Fact]
    public void Key_ShouldImplementIHasKey()
    {
        // Arrange & Act
        var state = State.Create("test-state", StateType.Intermediate, StateSubType.Success, "Patch");

        // Assert
        Assert.IsAssignableFrom<IHasKey>(state);
        Assert.Equal("test-state", state.Key);
    }

    [Fact]
    public void Create_ShouldAcceptMaxLengthKey()
    {
        // Arrange
        var key = new string('a', StateConstants.MaxKeyLength);

        // Act
        var state = State.Create(key, StateType.Intermediate, StateSubType.Success, "Patch");

        // Assert
        Assert.Equal(key, state.Key);
    }

    [Fact]
    public void AutoTransitions_ShouldBeEmpty_WhenNoAutomaticTransitions()
    {
        // Arrange
        var state = State.Create("test-state", StateType.Intermediate, StateSubType.Success, "Patch");
        var manualTransition = Transition.Create("manual", "test-state", "next", TriggerType.Manual, "Patch");
        state.AddTransition(manualTransition);

        // Act
        var result = state.AutoTransitions.ToList();

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void ScheduledTransitions_ShouldBeEmpty_WhenNoScheduledTransitions()
    {
        // Arrange
        var state = State.Create("test-state", StateType.Intermediate, StateSubType.Success, "Patch");
        var manualTransition = Transition.Create("manual", "test-state", "next", TriggerType.Manual, "Patch");
        state.AddTransition(manualTransition);

        // Act
        var result = state.ScheduledTransitions.ToList();

        // Assert
        Assert.Empty(result);
    }

    [Fact]
    public void HasOnlyManualOrEventTransitions_ShouldReturnTrue_WhenNoTransitions()
    {
        // Arrange
        var state = State.Create("test-state", StateType.Intermediate, StateSubType.Success, "Patch");

        // Act & Assert
        Assert.True(state.HasOnlyManualOrEventTransitions);
    }

    [Fact]
    public void HasOnlyManualOrEventTransitions_ShouldReturnTrue_WhenOnlyManualTransitions()
    {
        // Arrange
        var state = State.Create("test-state", StateType.Intermediate, StateSubType.Success, "Patch");
        var manualTransition1 = Transition.Create("submit", "test-state", "next", TriggerType.Manual, "Patch");
        var manualTransition2 = Transition.Create("cancel", "test-state", "cancelled", TriggerType.Manual, "Patch");
        state.AddTransition(manualTransition1);
        state.AddTransition(manualTransition2);

        // Act & Assert
        Assert.True(state.HasOnlyManualOrEventTransitions);
    }

    [Fact]
    public void HasOnlyManualOrEventTransitions_ShouldReturnTrue_WhenOnlyEventTransitions()
    {
        // Arrange
        var state = State.Create("test-state", StateType.Intermediate, StateSubType.Success, "Patch");
        var eventTransition = Transition.Create("on-event", "test-state", "next", TriggerType.Event, "Patch");
        state.AddTransition(eventTransition);

        // Act & Assert
        Assert.True(state.HasOnlyManualOrEventTransitions);
    }

    [Fact]
    public void HasOnlyManualOrEventTransitions_ShouldReturnTrue_WhenMixOfManualAndEventTransitions()
    {
        // Arrange
        var state = State.Create("test-state", StateType.Intermediate, StateSubType.Success, "Patch");
        var manualTransition = Transition.Create("submit", "test-state", "next", TriggerType.Manual, "Patch");
        var eventTransition = Transition.Create("on-event", "test-state", "other", TriggerType.Event, "Patch");
        state.AddTransition(manualTransition);
        state.AddTransition(eventTransition);

        // Act & Assert
        Assert.True(state.HasOnlyManualOrEventTransitions);
    }

    [Fact]
    public void HasOnlyManualOrEventTransitions_ShouldReturnFalse_WhenHasAutomaticTransition()
    {
        // Arrange
        var state = State.Create("test-state", StateType.Intermediate, StateSubType.Success, "Patch");
        var manualTransition = Transition.Create("submit", "test-state", "next", TriggerType.Manual, "Patch");
        var autoTransition = Transition.Create("auto", "test-state", "other", TriggerType.Automatic, "Patch");
        state.AddTransition(manualTransition);
        state.AddTransition(autoTransition);

        // Act & Assert
        Assert.False(state.HasOnlyManualOrEventTransitions);
    }

    [Fact]
    public void HasOnlyManualOrEventTransitions_ShouldReturnFalse_WhenHasScheduledTransition()
    {
        // Arrange
        var state = State.Create("test-state", StateType.Intermediate, StateSubType.Success, "Patch");
        var manualTransition = Transition.Create("submit", "test-state", "next", TriggerType.Manual, "Patch");
        var scheduledTransition = Transition.Create("timeout", "test-state", "expired", TriggerType.Scheduled, "Patch");
        state.AddTransition(manualTransition);
        state.AddTransition(scheduledTransition);

        // Act & Assert
        Assert.False(state.HasOnlyManualOrEventTransitions);
    }

    [Fact]
    public void HasOnlyManualOrEventTransitions_ShouldReturnFalse_WhenOnlyAutomaticTransitions()
    {
        // Arrange
        var state = State.Create("test-state", StateType.Intermediate, StateSubType.Success, "Patch");
        var autoTransition = Transition.Create("auto", "test-state", "next", TriggerType.Automatic, "Patch");
        state.AddTransition(autoTransition);

        // Act & Assert
        Assert.False(state.HasOnlyManualOrEventTransitions);
    }
}

