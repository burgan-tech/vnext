using System.Text.Json;
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Unit tests for ContextPathResolver.
/// </summary>
public sealed class ContextPathResolverTests
{
    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement;

    #region Simple property navigation

    [Fact]
    public void Resolve_SingleLevelProperty_ReturnsValue()
    {
        var root = Parse("""{"name": "alice"}""");
        var result = ContextPathResolver.Resolve(root, "name");
        result.ShouldBe(["alice"]);
    }

    [Fact]
    public void Resolve_NestedProperty_ReturnsValue()
    {
        var root = Parse("""{"customer": {"ownerUserId": "alice"}}""");
        var result = ContextPathResolver.Resolve(root, "customer.ownerUserId");
        result.ShouldBe(["alice"]);
    }

    [Fact]
    public void Resolve_DeeplyNestedProperty_ReturnsValue()
    {
        var root = Parse("""{"a": {"b": {"c": "deep"}}}""");
        var result = ContextPathResolver.Resolve(root, "a.b.c");
        result.ShouldBe(["deep"]);
    }

    #endregion

    #region ScriptContext-like root (Instance.Data.*)

    [Fact]
    public void Resolve_InstanceDataPath_ReturnsValue()
    {
        var root = Parse("""
            {
              "Instance": {
                "Id": "abc",
                "Data": {
                  "customer": { "ownerUserId": "alice" }
                }
              }
            }
            """);

        var result = ContextPathResolver.Resolve(root, "Instance.Data.customer.ownerUserId");
        result.ShouldBe(["alice"]);
    }

    [Fact]
    public void Resolve_InstanceCurrentState_ReturnsValue()
    {
        var root = Parse("""{"Instance": {"CurrentState": "waiting"}}""");
        var result = ContextPathResolver.Resolve(root, "Instance.CurrentState");
        result.ShouldBe(["waiting"]);
    }

    [Fact]
    public void Resolve_TransitionKey_ReturnsValue()
    {
        var root = Parse("""{"Transition": {"Key": "approve"}}""");
        var result = ContextPathResolver.Resolve(root, "Transition.Key");
        result.ShouldBe(["approve"]);
    }

    #endregion

    #region Array wildcard [*]

    [Fact]
    public void Resolve_ArrayWildcard_ReturnsAllMatchingValues()
    {
        var root = Parse("""
            {
              "assignedUsers": [
                {"userId": "alice"},
                {"userId": "bob"},
                {"userId": "charlie"}
              ]
            }
            """);

        var result = ContextPathResolver.Resolve(root, "assignedUsers[*].userId");
        result.Count.ShouldBe(3);
        result.ShouldContain("alice");
        result.ShouldContain("bob");
        result.ShouldContain("charlie");
    }

    [Fact]
    public void Resolve_NestedArrayWildcard_ReturnsAllMatchingValues()
    {
        var root = Parse("""
            {
              "Instance": {
                "Data": {
                  "approvers": [
                    {"role": "maker"},
                    {"role": "approver"}
                  ]
                }
              }
            }
            """);

        var result = ContextPathResolver.Resolve(root, "Instance.Data.approvers[*].role");
        result.Count.ShouldBe(2);
        result.ShouldContain("maker");
        result.ShouldContain("approver");
    }

    [Fact]
    public void Resolve_ArrayWildcardOnEmptyArray_ReturnsEmpty()
    {
        var root = Parse("""{"items": []}""");
        var result = ContextPathResolver.Resolve(root, "items[*].id");
        result.ShouldBeEmpty();
    }

    #endregion

    #region Case insensitivity

    [Fact]
    public void Resolve_PropertyNameCaseInsensitive_ReturnsValue()
    {
        var root = Parse("""{"CUSTOMER": {"OWNERUSERID": "alice"}}""");
        var result = ContextPathResolver.Resolve(root, "customer.ownerUserId");
        result.ShouldBe(["alice"]);
    }

    #endregion

    #region Missing / null paths

    [Fact]
    public void Resolve_MissingProperty_ReturnsEmpty()
    {
        var root = Parse("""{"customer": {"name": "alice"}}""");
        var result = ContextPathResolver.Resolve(root, "customer.ownerUserId");
        result.ShouldBeEmpty();
    }

    [Fact]
    public void Resolve_MissingIntermediateProperty_ReturnsEmpty()
    {
        var root = Parse("""{"other": "value"}""");
        var result = ContextPathResolver.Resolve(root, "customer.ownerUserId");
        result.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_NullOrEmptyPath_ReturnsEmpty(string? path)
    {
        var root = Parse("""{"x": "y"}""");
        var result = ContextPathResolver.Resolve(root, path!);
        result.ShouldBeEmpty();
    }

    [Fact]
    public void Resolve_NullJsonValue_ReturnsEmpty()
    {
        var root = Parse("""{"owner": null}""");
        var result = ContextPathResolver.Resolve(root, "owner");
        result.ShouldBeEmpty();
    }

    [Fact]
    public void Resolve_ObjectAtLeaf_ReturnsEmpty()
    {
        var root = Parse("""{"customer": {"id": "123"}}""");
        var result = ContextPathResolver.Resolve(root, "customer");
        result.ShouldBeEmpty(); // Object itself is not a leaf string value
    }

    #endregion

    #region Value type coercion

    [Fact]
    public void Resolve_NumberValue_ReturnsRawText()
    {
        var root = Parse("""{"amount": 42}""");
        var result = ContextPathResolver.Resolve(root, "amount");
        result.ShouldBe(["42"]);
    }

    [Fact]
    public void Resolve_BooleanTrue_ReturnsTrue()
    {
        var root = Parse("""{"active": true}""");
        var result = ContextPathResolver.Resolve(root, "active");
        result.ShouldBe(["true"]);
    }

    [Fact]
    public void Resolve_BooleanFalse_ReturnsFalse()
    {
        var root = Parse("""{"active": false}""");
        var result = ContextPathResolver.Resolve(root, "active");
        result.ShouldBe(["false"]);
    }

    #endregion
}
