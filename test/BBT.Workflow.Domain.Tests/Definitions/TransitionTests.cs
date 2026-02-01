using System;
using System.Collections.Generic;
using System.Linq;
using BBT.Workflow.Definitions;
using Xunit;

namespace BBT.Workflow.Definitions;

public class TransitionTests : DomainTestBase<DomainEntryPoint>
{

    [Fact]
    public void Create_ShouldInitializeProperties()
    {
        // Arrange & Act
        var transition = Transition.Create("submit", "from-state", "to-state", TriggerType.Manual, "Patch");

        // Assert
        Assert.Equal("submit", transition.Key);
        Assert.Equal("from-state", transition.From);
        Assert.Equal("to-state", transition.Target);
        Assert.Equal(TriggerType.Manual, transition.TriggerType);
        Assert.Equal(VersionStrategy.IncreasePatch, transition.VersionStrategy);
        Assert.NotNull(transition.Labels);
        Assert.Empty(transition.Labels);
        Assert.NotNull(transition.OnExecutionTasks);
        Assert.Empty(transition.OnExecutionTasks);
        Assert.NotNull(transition.AvailableIn);
        Assert.Empty(transition.AvailableIn);
    }

    [Theory]
    [InlineData(TriggerType.Manual)]
    [InlineData(TriggerType.Automatic)]
    [InlineData(TriggerType.Scheduled)]
    [InlineData(TriggerType.Event)]
    public void Create_ShouldAcceptAllTriggerTypes(TriggerType triggerType)
    {
        // Act
        var transition = Transition.Create("test", "from", "to", triggerType, "Patch");

        // Assert
        Assert.Equal(triggerType, transition.TriggerType);
    }

    [Theory]
    [InlineData("Minor")]
    [InlineData("Major")]
    [InlineData("Patch")]
    public void Create_ShouldAcceptAllVersionStrategies(string versionStrategy)
    {
        // Act
        var transition = Transition.Create("test", "from", "to", TriggerType.Manual, versionStrategy);

        // Assert
        Assert.NotNull(transition.VersionStrategy);
        Assert.Equal(versionStrategy, transition.VersionStrategy.Code);
    }

    [Fact]
    public void Create_ShouldAllowNullFrom_ForSharedTransitions()
    {
        // Act
        var transition = Transition.Create("cancel", null, "cancelled", TriggerType.Manual, "Patch");

        // Assert
        Assert.Null(transition.From);
        Assert.Equal("cancelled", transition.Target);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Create_ShouldThrowException_WhenKeyIsInvalid(string? key)
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => 
            Transition.Create(key!, "from", "to", TriggerType.Manual, "Patch"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Create_ShouldThrowException_WhenTargetIsInvalid(string? target)
    {
        // Act & Assert
        Assert.Throws<ArgumentException>(() => 
            Transition.Create("key", "from", target!, TriggerType.Manual, "Patch"));
    }

    [Fact]
    public void Create_ShouldThrowException_WhenKeyExceedsMaxLength()
    {
        // Arrange
        var key = new string('a', TransitionConstants.MaxKeyLength + 1);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => 
            Transition.Create(key, "from", "to", TriggerType.Manual, "Patch"));
    }

    [Fact]
    public void Create_ShouldThrowException_WhenTargetExceedsMaxLength()
    {
        // Arrange
        var target = new string('a', TransitionConstants.MaxTargetLength + 1);

        // Act & Assert
        Assert.Throws<ArgumentException>(() => 
            Transition.Create("key", "from", target, TriggerType.Manual, "Patch"));
    }

    [Fact]
    public void AddLanguage_ShouldAddNewLanguage()
    {
        // Arrange
        var transition = Transition.Create("submit", "from", "to", TriggerType.Manual, "Patch");

        // Act
        transition.AddLanguage("Submit Button", "en");

        // Assert
        Assert.Single(transition.Labels);
        Assert.Equal("Submit Button", transition.Labels.First().Label);
        Assert.Equal("en", transition.Labels.First().Language);
    }

    [Fact]
    public void AddLanguage_ShouldReplaceExistingLanguage()
    {
        // Arrange
        var transition = Transition.Create("submit", "from", "to", TriggerType.Manual, "Patch");
        transition.AddLanguage("Original Label", "en");

        // Act
        transition.AddLanguage("Updated Label", "en");

        // Assert
        Assert.Single(transition.Labels);
        Assert.Equal("Updated Label", transition.Labels.First().Label);
    }

    [Fact]
    public void AddLanguage_ShouldAddMultipleLanguages()
    {
        // Arrange
        var transition = Transition.Create("submit", "from", "to", TriggerType.Manual, "Patch");

        // Act
        transition.AddLanguage("Submit", "en");
        transition.AddLanguage("Gönder", "tr");
        transition.AddLanguage("Senden", "de");

        // Assert
        Assert.Equal(3, transition.Labels.Count);
    }

    [Fact]
    public void AddAvailableIn_ShouldAddStateKey()
    {
        // Arrange
        var transition = Transition.Create("cancel", null, "cancelled", TriggerType.Manual, "Patch");

        // Act
        transition.AddAvailableIn("state-1");

        // Assert
        Assert.Single(transition.AvailableIn);
        Assert.Contains("state-1", transition.AvailableIn);
    }

    [Fact]
    public void AddAvailableIn_ShouldNotAddDuplicateStateKey()
    {
        // Arrange
        var transition = Transition.Create("cancel", null, "cancelled", TriggerType.Manual, "Patch");
        transition.AddAvailableIn("state-1");

        // Act
        transition.AddAvailableIn("state-1");

        // Assert
        Assert.Single(transition.AvailableIn);
    }

    [Fact]
    public void AddAvailableIn_ShouldAddMultipleStates()
    {
        // Arrange
        var transition = Transition.Create("cancel", null, "cancelled", TriggerType.Manual, "Patch");

        // Act
        transition.AddAvailableIn("state-1");
        transition.AddAvailableIn("state-2");
        transition.AddAvailableIn("state-3");

        // Assert
        Assert.Equal(3, transition.AvailableIn.Count);
        Assert.Contains("state-1", transition.AvailableIn);
        Assert.Contains("state-2", transition.AvailableIn);
        Assert.Contains("state-3", transition.AvailableIn);
    }

    [Fact]
    public void SetSchema_ShouldSetSchemaReference()
    {
        // Arrange
        var transition = Transition.Create("submit", "from", "to", TriggerType.Manual, "Patch");
        var schemaReference = new Reference("schema-1", "domain", "sys-schemas", "1.0.0");

        // Act
        transition.SetSchema(schemaReference);

        // Assert
        Assert.NotNull(transition.Schema);
        Assert.Equal("schema-1", transition.Schema.Key);
    }

    [Fact]
    public void SetRule_ShouldSetRuleScript()
    {
        // Arrange
        var transition = Transition.Create("submit", "from", "to", TriggerType.Automatic, "Patch");
        var location = "rule-location";
        var code = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("return true;"));

        // Act
        transition.SetRule(location, code);

        // Assert
        Assert.NotNull(transition.Rule);
        Assert.Equal(location, transition.Rule.Location);
        Assert.Equal(code, transition.Rule.Code);
    }

    [Fact]
    public void SetTimer_ShouldSetTimerScript()
    {
        // Arrange
        var transition = Transition.Create("timeout", "from", "to", TriggerType.Scheduled, "Patch");
        var location = "timer-location";
        var code = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("PT1H"));

        // Act
        transition.SetTimer(location, code);

        // Assert
        Assert.NotNull(transition.Timer);
        Assert.Equal(location, transition.Timer.Location);
        Assert.Equal(code, transition.Timer.Code);
    }

    [Fact]
    public void AddOnExecutionTask_ShouldAddTask()
    {
        // Arrange
        var transition = Transition.Create("submit", "from", "to", TriggerType.Manual, "Patch");
        var taskReference = new Reference("task-1", "domain", "sys-tasks", "1.0.0");
        var mapping = new ScriptCode("location", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("code")));
        var task = OnExecuteTask.Create(1, taskReference, mapping);

        // Act
        transition.AddOnExecutionTask(task);

        // Assert
        Assert.Single(transition.OnExecutionTasks);
        Assert.Equal("task-1", transition.OnExecutionTasks.First().Task.Key);
    }

    [Fact]
    public void AddOnExecutionTask_ShouldAddMultipleTasks()
    {
        // Arrange
        var transition = Transition.Create("submit", "from", "to", TriggerType.Manual, "Patch");
        var taskReference1 = new Reference("task-1", "domain", "sys-tasks", "1.0.0");
        var taskReference2 = new Reference("task-2", "domain", "sys-tasks", "1.0.0");
        var mapping = new ScriptCode("location", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("code")));
        var task1 = OnExecuteTask.Create(1, taskReference1, mapping);
        var task2 = OnExecuteTask.Create(2, taskReference2, mapping);

        // Act
        transition.AddOnExecutionTask(task1);
        transition.AddOnExecutionTask(task2);

        // Assert
        Assert.Equal(2, transition.OnExecutionTasks.Count);
    }

    [Fact(Skip = "CanExecute method removed - validation now handled by TransitionExecutionPolicy")]
    public void CanExecute_ShouldReturnTrue_WhenValidTransition()
    {
        // This test is obsolete - validation is now done through specification pattern
        // See TransitionExecutionPolicy and ITransitionSpecification implementations
    }

    [Fact]
    public void Labels_ShouldBeReadOnly()
    {
        // Arrange
        var transition = Transition.Create("submit", "from", "to", TriggerType.Manual, "Patch");

        // Act & Assert
        Assert.IsAssignableFrom<IReadOnlyCollection<LanguageLabel>>(transition.Labels);
    }

    [Fact]
    public void OnExecutionTasks_ShouldBeReadOnly()
    {
        // Arrange
        var transition = Transition.Create("submit", "from", "to", TriggerType.Manual, "Patch");

        // Act & Assert
        Assert.IsAssignableFrom<IReadOnlyCollection<OnExecuteTask>>(transition.OnExecutionTasks);
    }

    [Fact]
    public void Key_ShouldImplementIHasKey()
    {
        // Arrange & Act
        var transition = Transition.Create("submit", "from", "to", TriggerType.Manual, "Patch");

        // Assert
        Assert.IsAssignableFrom<IHasKey>(transition);
        Assert.Equal("submit", ((IHasKey)transition).Key);
    }

    [Fact]
    public void Create_ShouldAcceptMaxLengthKey()
    {
        // Arrange
        var key = new string('a', TransitionConstants.MaxKeyLength);

        // Act
        var transition = Transition.Create(key, "from", "to", TriggerType.Manual, "Patch");

        // Assert
        Assert.Equal(key, transition.Key);
    }

    [Fact]
    public void Create_ShouldAcceptMaxLengthTarget()
    {
        // Arrange
        var target = new string('a', TransitionConstants.MaxTargetLength);

        // Act
        var transition = Transition.Create("key", "from", target, TriggerType.Manual, "Patch");

        // Assert
        Assert.Equal(target, transition.Target);
    }

    [Fact]
    public void Create_ShouldAcceptMaxLengthFrom()
    {
        // Arrange
        var from = new string('a', TransitionConstants.MaxTargetLength);

        // Act
        var transition = Transition.Create("key", from, "to", TriggerType.Manual, "Patch");

        // Assert
        Assert.Equal(from, transition.From);
    }

    [Fact]
    public void AvailableIn_ShouldBeInitializedEmpty()
    {
        // Act
        var transition = Transition.Create("key", "from", "to", TriggerType.Manual, "Patch");

        // Assert
        Assert.NotNull(transition.AvailableIn);
        Assert.Empty(transition.AvailableIn);
    }

    [Fact]
    public void Schema_ShouldBeNull_ByDefault()
    {
        // Act
        var transition = Transition.Create("key", "from", "to", TriggerType.Manual, "Patch");

        // Assert
        Assert.Null(transition.Schema);
    }

    [Fact]
    public void Rule_ShouldBeNull_ByDefault()
    {
        // Act
        var transition = Transition.Create("key", "from", "to", TriggerType.Manual, "Patch");

        // Assert
        Assert.Null(transition.Rule);
    }

    [Fact]
    public void Timer_ShouldBeNull_ByDefault()
    {
        // Act
        var transition = Transition.Create("key", "from", "to", TriggerType.Manual, "Patch");

        // Assert
        Assert.Null(transition.Timer);
    }

    [Fact]
    public void Kind_ShouldBeNull_ByDefault()
    {
        // Act
        var transition = Transition.Create("key", "from", "to", TriggerType.Automatic, "Patch");

        // Assert
        Assert.Null(transition.TriggerKind);
    }

    [Fact]
    public void Kind_ShouldDeserialize_FromJson()
    {
        // Arrange
        var json = """
        {
            "key": "default-transition",
            "from": "state1",
            "target": "state2",
            "triggerType": "Automatic",
            "kind": "DefaultAutoTransition",
            "versionStrategy": "Patch",
            "labels": [],
            "onExecutionTasks": [],
            "view": null
        }
        """;

        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };

        // Act
        var transition = System.Text.Json.JsonSerializer.Deserialize<Transition>(json, options);

        // Assert
        Assert.NotNull(transition);
        Assert.Equal(TransitionKind.DefaultAutoTransition, transition.TriggerKind);
        Assert.Equal(TriggerType.Automatic, transition.TriggerType);
    }

    [Fact]
    public void Kind_ShouldBeNull_WhenNotInJson()
    {
        // Arrange
        var json = """
        {
            "key": "regular-transition",
            "from": "state1",
            "target": "state2",
            "triggerType": "Automatic",
            "versionStrategy": "Patch",
            "labels": [],
            "onExecutionTasks": [],
            "view": null
        }
        """;

        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };

        // Act
        var transition = System.Text.Json.JsonSerializer.Deserialize<Transition>(json, options);

        // Assert
        Assert.NotNull(transition);
        Assert.Null(transition.TriggerKind);
    }
}

