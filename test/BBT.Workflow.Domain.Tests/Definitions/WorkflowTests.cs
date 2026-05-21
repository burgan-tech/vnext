using System;
using System.Collections.Generic;
using System.Linq;
using BBT.Workflow.Definitions;
using Xunit;

namespace BBT.Workflow.Definitions;

public class WorkflowTests : DomainTestBase<DomainEntryPoint>
{
    [Fact]
    public void Create_ShouldInitializeProperties()
    {
        // Act
        var workflow = Workflow.Create();

        // Assert
        Assert.NotNull(workflow);
        Assert.NotNull(workflow.Labels);
        Assert.Empty(workflow.Labels);
        Assert.NotNull(workflow.Functions);
        Assert.Empty(workflow.Functions);
        Assert.NotNull(workflow.Features);
        Assert.Empty(workflow.Features);
        Assert.NotNull(workflow.States);
        Assert.Empty(workflow.States);
        Assert.NotNull(workflow.SharedTransitions);
        Assert.Empty(workflow.SharedTransitions);
        Assert.NotNull(workflow.Extensions);
        Assert.Empty(workflow.Extensions);
    }

    [Fact]
    public void SetReference_ShouldSetProperties()
    {
        // Arrange
        var workflow = Workflow.Create();
        var reference = new Reference("test-flow", "test-domain", "sys-flows", "1.0.0");

        // Act
        workflow.SetReference(reference);

        // Assert
        Assert.Equal("test-flow", workflow.Key);
        Assert.Equal("test-domain", workflow.Domain);
        Assert.Equal("1.0.0", workflow.Version);
    }

    [Fact]
    public void SetType_ShouldSetWorkflowType()
    {
        // Arrange
        var workflow = Workflow.Create();

        // Act
        workflow.SetType("F");

        // Assert
        Assert.Equal(WorkflowType.Flow, workflow.Type);
    }

    [Theory]
    [InlineData("C", false)]
    [InlineData("F", false)]
    [InlineData("S", true)]
    [InlineData("P", true)]
    public void IsSub_ShouldReturnCorrectValue_BasedOnType(string typeCode, bool expectedIsSub)
    {
        // Arrange
        var workflow = Workflow.Create();
        workflow.SetType(typeCode);

        // Act & Assert
        Assert.Equal(expectedIsSub, workflow.IsSub);
    }

    [Fact]
    public void AddLanguage_ShouldAddNewLanguage()
    {
        // Arrange
        var workflow = Workflow.Create();

        // Act
        workflow.AddLanguage("Test Workflow", "en");

        // Assert
        Assert.Single(workflow.Labels);
        Assert.Equal("Test Workflow", workflow.Labels.First().Label);
        Assert.Equal("en", workflow.Labels.First().Language);
    }

    [Fact]
    public void AddLanguage_ShouldReplaceExistingLanguage()
    {
        // Arrange
        var workflow = Workflow.Create();
        workflow.AddLanguage("Original Label", "en");

        // Act
        workflow.AddLanguage("Updated Label", "en");

        // Assert
        Assert.Single(workflow.Labels);
        Assert.Equal("Updated Label", workflow.Labels.First().Label);
    }

    [Fact]
    public void AddLanguage_ShouldAddMultipleLanguages()
    {
        // Arrange
        var workflow = Workflow.Create();

        // Act
        workflow.AddLanguage("English Label", "en");
        workflow.AddLanguage("Turkish Label", "tr");
        workflow.AddLanguage("German Label", "de");

        // Assert
        Assert.Equal(3, workflow.Labels.Count);
    }

    [Fact]
    public void SetTimeout_ShouldSetWorkflowTimeout()
    {
        // Arrange
        var workflow = Workflow.Create();
        var timeout = WorkflowTimeout.Create("timeout-key", "finish", "Patch", "none", "PT1H");

        // Act
        workflow.SetTimeout(timeout);

        // Assert
        Assert.NotNull(workflow.Timeout);
        Assert.Equal("timeout-key", workflow.Timeout.Key);
        Assert.Equal("finish", workflow.Timeout.Target);
    }

    [Fact]
    public void AddFunction_ShouldAddFunctionReference()
    {
        // Arrange
        var workflow = Workflow.Create();
        var reference = new Reference("func-1", "domain", "sys-functions", "1.0.0");

        // Act
        workflow.AddFunction(reference);

        // Assert
        Assert.Single(workflow.Functions);
        Assert.Contains(workflow.Functions, f => f.Key == "func-1");
    }

    [Fact]
    public void AddFeature_ShouldAddFeatureReference()
    {
        // Arrange
        var workflow = Workflow.Create();
        var reference = new Reference("feature-1", "domain", "sys-features", "1.0.0");

        // Act
        workflow.AddFeature(reference);

        // Assert
        Assert.Single(workflow.Features);
        Assert.Contains(workflow.Features, f => f.Key == "feature-1");
    }

    [Fact]
    public void AddExtension_ShouldAddExtensionReference()
    {
        // Arrange
        var workflow = Workflow.Create();
        var reference = new Reference("ext-1", "domain", "sys-extensions", "1.0.0");

        // Act
        workflow.AddExtension(reference);

        // Assert
        Assert.Single(workflow.Extensions);
    }

    [Fact]
    public void AddState_ShouldAddStateToWorkflow()
    {
        // Arrange
        var workflow = Workflow.Create();
        var state = State.Create("initial", StateType.Initial, StateSubType.Success, "Patch");

        // Act
        workflow.AddState(state);

        // Assert
        Assert.Single(workflow.States);
        Assert.Equal("initial", workflow.States.First().Key);
    }

    [Fact]
    public void AddSharedTransition_ShouldAddSharedTransition()
    {
        // Arrange
        var workflow = Workflow.Create();
        var transition = Transition.Create("cancel", null, "cancelled", TriggerType.Manual, "Patch");

        // Act
        workflow.AddSharedTransition(transition);

        // Assert
        Assert.Single(workflow.SharedTransitions);
        Assert.Equal("cancel", workflow.SharedTransitions.First().Key);
    }

    [Fact]
    public void SetStartTransition_ShouldSetStartTransition()
    {
        // Arrange
        var workflow = Workflow.Create();
        var transition = Transition.Create("start", null, "initial", TriggerType.Manual, "Patch");

        // Act
        workflow.SetStartTransition(transition);

        // Assert
        Assert.NotNull(workflow.StartTransition);
        Assert.Equal("start", workflow.StartTransition.Key);
        Assert.Equal("initial", workflow.StartTransition.Target);
    }

    [Fact]
    public void GetInitialState_ShouldReturnInitialState_WhenExists()
    {
        // Arrange
        var workflow = Workflow.Create();
        var initialState = State.Create("initial", StateType.Initial, StateSubType.Success, "Patch");
        workflow.AddState(initialState);

        // Act
        var result = workflow.GetInitialState();

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal("initial", result.Value?.Key);
        Assert.Equal(StateType.Initial, result.Value?.StateType);
    }

    [Fact]
    public void GetInitialState_ShouldReturnFailure_WhenNoInitialState()
    {
        // Arrange
        var workflow = WorkflowFactory.CreateDefault();

        // Act
        var result = workflow.GetInitialState();

        // Assert
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void GetState_ShouldReturnState_WhenExists()
    {
        // Arrange
        var workflow = Workflow.Create();
        var state = State.Create("test-state", StateType.Intermediate, StateSubType.Success, "Patch");
        workflow.AddState(state);

        // Act
        var result = workflow.GetState("test-state");

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal("test-state", result.Value?.Key);
    }

    [Fact]
    public void GetState_ShouldReturnFailure_WhenStateDoesNotExist()
    {
        // Arrange
        var workflow = WorkflowFactory.CreateDefault();

        // Act
        var result = workflow.GetState("non-existent");

        // Assert
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public void FindState_ShouldReturnState_WhenExists()
    {
        // Arrange
        var workflow = Workflow.Create();
        var state = State.Create("test-state", StateType.Intermediate, StateSubType.Success, "Patch");
        workflow.AddState(state);

        // Act
        var result = workflow.FindState("test-state");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("test-state", result.Key);
    }

    [Fact]
    public void FindState_ShouldReturnNull_WhenStateDoesNotExist()
    {
        // Arrange
        var workflow = Workflow.Create();

        // Act
        var result = workflow.FindState("non-existent");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void FindSharedTransition_ShouldReturnTransition_WhenExists()
    {
        // Arrange
        var workflow = Workflow.Create();
        var transition = Transition.Create("cancel", null, "cancelled", TriggerType.Manual, "Patch");
        workflow.AddSharedTransition(transition);

        // Act
        var result = workflow.FindSharedTransition("cancel");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("cancel", result.Key);
    }

    [Fact]
    public void FindSharedTransition_ShouldReturnNull_WhenDoesNotExist()
    {
        // Arrange
        var workflow = Workflow.Create();

        // Act
        var result = workflow.FindSharedTransition("non-existent");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void FindTransition_ShouldReturnSharedTransition()
    {
        // Arrange
        var workflow = Workflow.Create();
        var transition = Transition.Create("cancel", null, "cancelled", TriggerType.Manual, "Patch");
        workflow.AddSharedTransition(transition);

        // Act
        var result = workflow.FindTransition("cancel");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("cancel", result.Key);
    }

    [Fact]
    public void FindTransition_ShouldReturnStartTransition()
    {
        // Arrange
        var workflow = Workflow.Create();
        var transition = Transition.Create("start", null, "initial", TriggerType.Manual, "Patch");
        workflow.SetStartTransition(transition);

        // Act
        var result = workflow.FindTransition("start");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("start", result.Key);
    }

    [Fact]
    public void ResolveTransition_ShouldReturnStateTransition_First()
    {
        // Arrange
        var workflow = Workflow.Create();
        var state = State.Create("test-state", StateType.Intermediate, StateSubType.Success, "Patch");
        var stateTransition = Transition.Create("submit", "test-state", "next-state", TriggerType.Manual, "Patch");
        state.AddTransition(stateTransition);
        workflow.AddState(state);

        var sharedTransition = Transition.Create("submit", null, "other-state", TriggerType.Manual, "Patch");
        workflow.AddSharedTransition(sharedTransition);

        // Act
        var result = workflow.ResolveTransition("submit", state);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("next-state", result.Target);
    }

    [Fact]
    public void ResolveTransition_ShouldReturnSharedTransition_WhenNoStateTransition()
    {
        // Arrange
        var workflow = Workflow.Create();
        var state = State.Create("test-state", StateType.Intermediate, StateSubType.Success, "Patch");
        workflow.AddState(state);

        var sharedTransition = Transition.Create("cancel", null, "cancelled", TriggerType.Manual, "Patch");
        workflow.AddSharedTransition(sharedTransition);

        // Act
        var result = workflow.ResolveTransition("cancel", state);

        // Assert
        Assert.NotNull(result);
        Assert.Equal("cancelled", result.Target);
    }

    [Fact]
    public void CacheKey_ShouldGenerateCorrectFormat()
    {
        // Arrange
        var workflow = Workflow.Create();
        var reference = new Reference("test-flow", "test-domain", "sys-flows", "1.0.0");
        workflow.SetReference(reference);

        // Act
        var cacheKey = workflow.ComponentKey;

        // Assert
        Assert.Equal("Workflow:test-domain:sys-flows:test-flow:1.0.0", cacheKey);
    }
    
    [Fact]
    public void SemanticVersion_ShouldReturnVersionWithoutMetadata()
    {
        // Arrange
        var workflow = Workflow.Create();
        var reference = new Reference("test-flow", "test-domain", "sys-flows", "1.2.3+build.123");
        workflow.SetReference(reference);

        // Act
        var semanticVersion = workflow.SemanticVersion;

        // Assert
        Assert.Equal("1.2.3", semanticVersion);
    }

    [Fact]
    public void SemanticVersion_ShouldReturnFullVersion_WhenNoMetadata()
    {
        // Arrange
        var workflow = Workflow.Create();
        var reference = new Reference("test-flow", "test-domain", "sys-flows", "1.2.3");
        workflow.SetReference(reference);

        // Act
        var semanticVersion = workflow.SemanticVersion;

        // Assert
        Assert.Equal("1.2.3", semanticVersion);
    }

    [Fact]
    public void GetAvailableUserTransitionKeys_ShouldReturnManualAndEventTransitions()
    {
        // Arrange
        var workflow = Workflow.Create();
        var state = State.Create("test-state", StateType.Intermediate, StateSubType.Success, "Patch");
        
        var manualTransition = Transition.Create("submit", "test-state", "next", TriggerType.Manual, "Patch");
        var eventTransition = Transition.Create("event", "test-state", "other", TriggerType.Event, "Patch");
        var autoTransition = Transition.Create("auto", "test-state", "auto-next", TriggerType.Automatic, "Patch");
        
        state.AddTransition(manualTransition);
        state.AddTransition(eventTransition);
        state.AddTransition(autoTransition);
        workflow.AddState(state);

        // Act
        var result = workflow.GetAvailableUserTransitionKeys(state);

        // Assert
        Assert.Equal(2, result.Count);
        Assert.Contains("submit", result);
        Assert.Contains("event", result);
        Assert.DoesNotContain("auto", result);
    }

    [Fact]
    public void GetAvailableUserTransitionKeys_ShouldIncludeSharedTransitions()
    {
        // Arrange
        var workflow = Workflow.Create();
        var state = State.Create("test-state", StateType.Intermediate, StateSubType.Success, "Patch");
        workflow.AddState(state);

        var sharedTransition = Transition.Create("cancel", null, "cancelled", TriggerType.Manual, "Patch");
        sharedTransition.AddAvailableIn("test-state");
        workflow.AddSharedTransition(sharedTransition);

        // Act
        var result = workflow.GetAvailableUserTransitionKeys(state);

        // Assert
        Assert.Contains("cancel", result);
    }

    [Fact]
    public void GetAvailableUserTransitionKeys_ShouldIncludeSharedTransition_WhenAvailableInIsEmpty()
    {
        // Arrange
        var workflow = Workflow.Create();
        var state = State.Create("review", StateType.Intermediate, StateSubType.None, "Patch");
        workflow.AddState(state);

        var anotherState = State.Create("pending", StateType.Intermediate, StateSubType.None, "Patch");
        workflow.AddState(anotherState);

        var sharedTransition = Transition.Create("cancel", null, "cancelled", TriggerType.Manual, "Patch");
        workflow.AddSharedTransition(sharedTransition);

        // Act — should be available from any state when AvailableIn is empty
        var resultReview = workflow.GetAvailableUserTransitionKeys(state);
        var resultPending = workflow.GetAvailableUserTransitionKeys(anotherState);

        // Assert
        Assert.Contains("cancel", resultReview);
        Assert.Contains("cancel", resultPending);
    }

    [Fact]
    public void GetAvailableUserTransitionKeys_ShouldExcludeSharedTransition_WhenAvailableInDoesNotContainCurrentState()
    {
        // Arrange
        var workflow = Workflow.Create();
        var reviewState = State.Create("review", StateType.Intermediate, StateSubType.None, "Patch");
        workflow.AddState(reviewState);

        var pendingState = State.Create("pending", StateType.Intermediate, StateSubType.None, "Patch");
        workflow.AddState(pendingState);

        var sharedTransition = Transition.Create("cancel", null, "cancelled", TriggerType.Manual, "Patch");
        sharedTransition.AddAvailableIn("review");
        workflow.AddSharedTransition(sharedTransition);

        // Act
        var resultReview = workflow.GetAvailableUserTransitionKeys(reviewState);
        var resultPending = workflow.GetAvailableUserTransitionKeys(pendingState);

        // Assert
        Assert.Contains("cancel", resultReview);
        Assert.DoesNotContain("cancel", resultPending);
    }

    [Fact]
    public void GetAvailableSharedTransitionKeysOnly_ShouldReturnAll_WhenAvailableInIsEmpty()
    {
        // Arrange
        var workflow = Workflow.Create();
        var stateA = State.Create("state-a", StateType.Intermediate, StateSubType.None, "Patch");
        var stateB = State.Create("state-b", StateType.Intermediate, StateSubType.None, "Patch");
        workflow.AddState(stateA);
        workflow.AddState(stateB);

        var sharedTransition = Transition.Create("notes", null, "$self", TriggerType.Manual, "Patch");
        workflow.AddSharedTransition(sharedTransition);

        // Act
        var resultA = workflow.GetAvailableSharedTransitionKeysOnly(stateA);
        var resultB = workflow.GetAvailableSharedTransitionKeysOnly(stateB);

        // Assert
        Assert.Contains("notes", resultA);
        Assert.Contains("notes", resultB);
    }

    [Fact]
    public void GetAvailableSharedTransitionKeysOnly_ShouldFilterByAvailableIn_WhenPopulated()
    {
        // Arrange
        var workflow = Workflow.Create();
        var stateA = State.Create("state-a", StateType.Intermediate, StateSubType.None, "Patch");
        var stateB = State.Create("state-b", StateType.Intermediate, StateSubType.None, "Patch");
        workflow.AddState(stateA);
        workflow.AddState(stateB);

        var sharedTransition = Transition.Create("notes", null, "$self", TriggerType.Manual, "Patch");
        sharedTransition.AddAvailableIn("state-a");
        workflow.AddSharedTransition(sharedTransition);

        // Act
        var resultA = workflow.GetAvailableSharedTransitionKeysOnly(stateA);
        var resultB = workflow.GetAvailableSharedTransitionKeysOnly(stateB);

        // Assert
        Assert.Contains("notes", resultA);
        Assert.DoesNotContain("notes", resultB);
    }

    [Fact]
    public void FindTransitionInContext_ShouldSearchAllTransitions()
    {
        // Arrange
        var workflow = Workflow.Create();
        workflow.SetStartTransition(Transition.Create("start-tran", "", "init", TriggerType.Manual, "Patch"));
        var state = State.Create("test-state", StateType.Intermediate, StateSubType.Success, "Patch");
        var stateTransition = Transition.Create("state-trans", "test-state", "next", TriggerType.Manual, "Patch");
        state.AddTransition(stateTransition);
        workflow.AddState(state);

        // Act
        var result = workflow.FindTransitionInContext("state-trans");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("state-trans", result.Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void SetReference_ShouldThrowException_WhenKeyIsInvalid(string? key)
    {
        // Arrange
        var workflow = Workflow.Create();
        var reference = new Reference(key!, "domain", "flow", "1.0.0");

        // Act & Assert
        Assert.Throws<ArgumentException>(() => workflow.SetReference(reference));
    }

    [Fact]
    public void SetReference_ShouldThrowException_WhenKeyExceedsMaxLength()
    {
        // Arrange
        var workflow = Workflow.Create();
        var key = new string('a', WorkflowConstants.MaxKeyLength + 1);
        var reference = new Reference(key, "domain", "flow", "1.0.0");

        // Act & Assert
        Assert.Throws<ArgumentException>(() => workflow.SetReference(reference));
    }
}

