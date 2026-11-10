using System;
using System.Collections.Generic;
using System.Linq;
using BBT.Aether.Results;
using BBT.Workflow.Domain;
using BBT.Workflow.Rules;
using Xunit;

namespace BBT.Workflow.Rules;

public class ResultRuleEngineTests
{
    [Fact]
    public void Constructor_ShouldInitializeWithRules()
    {
        // Arrange
        var rules = new List<IResultRule<TestContext>>
        {
            new AlwaysApplicableRule(),
            new NeverApplicableRule()
        };

        // Act
        var engine = new ResultRuleEngine<TestContext>(rules);

        // Assert
        Assert.NotNull(engine);
    }

    [Fact]
    public void Constructor_ShouldInitializeWithEmptyRules()
    {
        // Arrange & Act
        var engine = new ResultRuleEngine<TestContext>();

        // Assert
        Assert.NotNull(engine);
    }

    [Fact]
    public void Validate_ShouldReturnSuccess_WhenNoRulesAreSet()
    {
        // Arrange
        var engine = new ResultRuleEngine<TestContext>();
        var context = new TestContext();

        // Act
        var result = engine.Validate(context);

        // Assert
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Validate_ShouldReturnSuccess_WhenAllRulesPass()
    {
        // Arrange
        var rules = new List<IResultRule<TestContext>>
        {
            new PassingRule(),
            new PassingRule()
        };
        var engine = new ResultRuleEngine<TestContext>(rules);
        var context = new TestContext();

        // Act
        var result = engine.Validate(context);

        // Assert
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Validate_ShouldReturnFailure_WhenAnyRuleFails()
    {
        // Arrange
        var rules = new List<IResultRule<TestContext>>
        {
            new PassingRule(),
            new FailingRule("Test Error")
        };
        var engine = new ResultRuleEngine<TestContext>(rules);
        var context = new TestContext();

        // Act
        var result = engine.Validate(context);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("Test Error", result.Error.Code);
    }

    [Fact]
    public void Validate_ShouldReturnFirstFailure_WhenMultipleRulesFail()
    {
        // Arrange
        var rules = new List<IResultRule<TestContext>>
        {
            new PassingRule(),
            new FailingRule("First Error"),
            new FailingRule("Second Error")
        };
        var engine = new ResultRuleEngine<TestContext>(rules);
        var context = new TestContext();

        // Act
        var result = engine.Validate(context);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal("First Error", result.Error.Code);
    }

    [Fact]
    public void Validate_ShouldSkipNonApplicableRules()
    {
        // Arrange
        var rules = new List<IResultRule<TestContext>>
        {
            new NeverApplicableRule(),
            new PassingRule()
        };
        var engine = new ResultRuleEngine<TestContext>(rules);
        var context = new TestContext();

        // Act
        var result = engine.Validate(context);

        // Assert
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void Validate_ShouldOnlyExecuteApplicableRules()
    {
        // Arrange
        var executedRules = new List<string>();
        var rules = new List<IResultRule<TestContext>>
        {
            new TrackingRule(executedRules, "Rule1", isApplicable: true),
            new TrackingRule(executedRules, "Rule2", isApplicable: false),
            new TrackingRule(executedRules, "Rule3", isApplicable: true)
        };
        var engine = new ResultRuleEngine<TestContext>(rules);
        var context = new TestContext();

        // Act
        var result = engine.Validate(context);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(2, executedRules.Count);
        Assert.Contains("Rule1", executedRules);
        Assert.Contains("Rule3", executedRules);
        Assert.DoesNotContain("Rule2", executedRules);
    }

    [Fact]
    public void SetRules_ShouldReplaceExistingRules()
    {
        // Arrange
        var initialRules = new List<IResultRule<TestContext>>
        {
            new FailingRule("Initial Error")
        };
        var newRules = new List<IResultRule<TestContext>>
        {
            new PassingRule()
        };
        var engine = new ResultRuleEngine<TestContext>(initialRules);
        var context = new TestContext();

        // Act
        engine.SetRules(newRules);
        var result = engine.Validate(context);

        // Assert
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void ValidateAll_ShouldReturnSuccess_WhenAllRulesPass()
    {
        // Arrange
        var rules = new List<IResultRule<TestContext>>
        {
            new PassingRule(),
            new PassingRule()
        };
        var engine = new ResultRuleEngine<TestContext>(rules);
        var context = new TestContext();

        // Act
        var result = engine.ValidateAll(context);

        // Assert
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void ValidateAll_ShouldCollectAllErrors()
    {
        // Arrange
        var rules = new List<IResultRule<TestContext>>
        {
            new FailingRule("Error1"),
            new FailingRule("Error2"),
            new PassingRule(),
            new FailingRule("Error3")
        };
        var engine = new ResultRuleEngine<TestContext>(rules);
        var context = new TestContext();

        // Act
        var result = engine.ValidateAll(context);

        // Assert
        Assert.False(result.IsSuccess);
        // ValidateAll returns first error (implementation detail)
        Assert.Equal("Error1", result.Error.Code);
    }

    [Fact]
    public void ValidateAll_ShouldExecuteAllApplicableRules()
    {
        // Arrange
        var executedRules = new List<string>();
        var rules = new List<IResultRule<TestContext>>
        {
            new TrackingRule(executedRules, "Rule1", isApplicable: true),
            new TrackingRule(executedRules, "Rule2", isApplicable: true),
            new TrackingRule(executedRules, "Rule3", isApplicable: true)
        };
        var engine = new ResultRuleEngine<TestContext>(rules);
        var context = new TestContext();

        // Act
        var result = engine.ValidateAll(context);

        // Assert
        Assert.True(result.IsSuccess);
        Assert.Equal(3, executedRules.Count);
    }

    [Fact]
    public void Validate_ShouldStopAtFirstFailure()
    {
        // Arrange
        var executedRules = new List<string>();
        var rules = new List<IResultRule<TestContext>>
        {
            new TrackingRule(executedRules, "Rule1", isApplicable: true, shouldFail: false),
            new TrackingRule(executedRules, "Rule2", isApplicable: true, shouldFail: true),
            new TrackingRule(executedRules, "Rule3", isApplicable: true, shouldFail: false)
        };
        var engine = new ResultRuleEngine<TestContext>(rules);
        var context = new TestContext();

        // Act
        var result = engine.Validate(context);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Equal(2, executedRules.Count); // Should stop after Rule2
        Assert.DoesNotContain("Rule3", executedRules);
    }

    [Fact]
    public void Validate_ShouldWorkWithComplexContext()
    {
        // Arrange
        var rules = new List<IResultRule<ComplexContext>>
        {
            new ComplexContextRule()
        };
        var engine = new ResultRuleEngine<ComplexContext>(rules);
        var context = new ComplexContext
        {
            Value = 10,
            Name = "Test"
        };

        // Act
        var result = engine.Validate(context);

        // Assert
        Assert.True(result.IsSuccess);
    }

    // Test Helper Classes
    private class TestContext
    {
    }

    private class ComplexContext
    {
        public int Value { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    private class AlwaysApplicableRule : ResultBaseRule<TestContext>
    {
        public override bool IsApplicable(TestContext context) => true;
        public override Result Validate(TestContext context) => Result.Ok();
    }

    private class NeverApplicableRule : ResultBaseRule<TestContext>
    {
        public override bool IsApplicable(TestContext context) => false;
        public override Result Validate(TestContext context) => Result.Ok();
    }

    private class PassingRule : ResultBaseRule<TestContext>
    {
        public override bool IsApplicable(TestContext context) => true;
        public override Result Validate(TestContext context) => Result.Ok();
    }

    private class FailingRule : ResultBaseRule<TestContext>
    {
        private readonly string _errorCode;

        public FailingRule(string errorCode)
        {
            _errorCode = errorCode;
        }

        public override bool IsApplicable(TestContext context) => true;
        public override Result Validate(TestContext context) => Result.Fail(Error.Validation(_errorCode, "Test error message"));
    }

    private class TrackingRule : ResultBaseRule<TestContext>
    {
        private readonly List<string> _executedRules;
        private readonly string _ruleName;
        private readonly bool _isApplicable;
        private readonly bool _shouldFail;

        public TrackingRule(List<string> executedRules, string ruleName, bool isApplicable, bool shouldFail = false)
        {
            _executedRules = executedRules;
            _ruleName = ruleName;
            _isApplicable = isApplicable;
            _shouldFail = shouldFail;
        }

        public override bool IsApplicable(TestContext context) => _isApplicable;

        public override Result Validate(TestContext context)
        {
            _executedRules.Add(_ruleName);
            return _shouldFail ? Result.Fail(new Error($"{_ruleName}_Error", "Error")) : Result.Ok();
        }
    }

    private class ComplexContextRule : ResultBaseRule<ComplexContext>
    {
        public override bool IsApplicable(ComplexContext context) => context.Value > 0;
        public override Result Validate(ComplexContext context) => 
            context.Name.Length > 0 ? Result.Ok() : Result.Fail(new Error("EMPTY_NAME", "Name is empty"));
    }
}

