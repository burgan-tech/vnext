using System;
using System.Text.Json;
using BBT.Workflow.Scripting.Rules;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Scripting.Rules;

public sealed class RuleJsonDynamicBorrowTests
{
    [Fact]
    public void Borrow_ReadsLikeFromJsonElement()
    {
        using var doc = JsonDocument.Parse("""{"customer":{"name":"Ada","tags":["a","b"]}}""");
        dynamic root = RuleJsonDynamic.Borrow(doc.RootElement);

        ((string)root.customer.name.ToString()).ShouldBe("Ada");
        ((bool)root.customer.tags.Contains("b")).ShouldBeTrue();
    }

    [Fact]
    public void Borrow_DoesNotClone_ElementDiesWithItsDocument()
    {
        var doc = JsonDocument.Parse("""{"a":1}""");
        var borrowed = RuleJsonDynamic.Borrow(doc.RootElement);
        doc.Dispose();

        Should.Throw<ObjectDisposedException>(() => borrowed.ToString());
    }

    [Fact]
    public void Borrow_NestedMembersAreBorrowedToo()
    {
        var doc = JsonDocument.Parse("""{"a":{"b":2}}""");
        dynamic root = RuleJsonDynamic.Borrow(doc.RootElement);
        RuleJsonDynamic nested = root.a;
        doc.Dispose();

        Should.Throw<ObjectDisposedException>(() => nested.ToString());
    }

    [Fact]
    public void FromJsonElement_StillClones_SurvivesDocumentDisposal()
    {
        var doc = JsonDocument.Parse("""{"a":{"b":2}}""");
        dynamic root = RuleJsonDynamic.FromJsonElement(doc.RootElement);
        RuleJsonDynamic nested = root.a;
        doc.Dispose();

        nested.ToString().ShouldBe("""{"b":2}""");
    }
}
