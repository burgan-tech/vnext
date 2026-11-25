using System;
using System.Text.Json;
using BBT.Workflow.Scripting.Rules;
using Microsoft.CSharp.RuntimeBinder;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Scripting.Rules;

/// <summary>
/// A <see cref="RuleJsonDynamic.Borrow"/> view (the declarative mapping scope) reads a member of a non-object or an
/// index that does not apply as missing (null); the legacy views (<see cref="RuleJsonDynamic.FromJsonElement"/>,
/// <see cref="RuleJsonDynamic.FromObject"/>, i.e. every <c>context.*</c> rule) keep failing exactly as before.
/// </summary>
public sealed class RuleJsonDynamicBorrowLenienceTests
{
    private const string Json = """{"n":5,"s":"x","list":[1,2],"o":{"a":1}}""";

    [Fact]
    public void Borrow_MemberOfNonObject_And_InapplicableIndex_ReadAsNull()
    {
        using var doc = JsonDocument.Parse(Json);
        dynamic root = RuleJsonDynamic.Borrow(doc.RootElement);

        ((object?)root.n.deeper).ShouldBeNull();
        ((object?)root.s.deeper).ShouldBeNull();
        ((object?)root.list[5]).ShouldBeNull();
        ((object?)root.o[0]).ShouldBeNull();
        ((object?)root.missing).ShouldBeNull();
        ((int)root.list.Count).ShouldBe(2);
    }

    [Fact]
    public void Borrow_UnknownMethodCall_Throws_InsteadOfReadingAsMissing()
    {
        // A CLR method on a JSON value never runs: before, the call fell back to a member read (null) and invoking null
        // threw the binder's "null reference" — which a declarative mapping reads as a missing source and skips.
        using var doc = JsonDocument.Parse(Json);
        dynamic root = RuleJsonDynamic.Borrow(doc.RootElement);

        var substring = Should.Throw<InvalidOperationException>(() => (object?)root.s.Substring(40));
        substring.Message.ShouldBe(
            "'Substring' is not a method of this JSON String value; convert it first, e.g. .ToString().Substring(...)");
        Should.Throw<InvalidOperationException>(() => (object?)root.s.ToUpper()).Message.ShouldContain("'ToUpper'");
        Should.Throw<InvalidOperationException>(() => (object?)root.n.CompareTo(1)).Message.ShouldContain("Number");
        Should.Throw<InvalidOperationException>(() => (object?)root.o.Foo());
    }

    [Fact]
    public void Borrow_KnownCalls_And_MissingSources_Unchanged()
    {
        using var doc = JsonDocument.Parse(Json);
        dynamic root = RuleJsonDynamic.Borrow(doc.RootElement);

        ((bool)root.list.Contains(2)).ShouldBeTrue();
        ((bool)root.list.Contains(9)).ShouldBeFalse();
        ((string)root.s.ToString().ToUpper()).ShouldBe("X");
        ((double)root.n.AsDouble()).ShouldBe(5d);
        // A call through a missing intermediate is still the binder's null reference (a missing source).
        Should.Throw<RuntimeBinderException>(() => (object?)root.missing.id.ToUpper()).Message.ShouldContain("null reference");
        Should.Throw<RuntimeBinderException>(() => (object?)root.missing.AsDouble()).Message.ShouldContain("null reference");
        // A property read on a scalar keeps reading as missing (documented lenience; only method calls changed).
        ((object?)root.s.Length).ShouldBeNull();
    }

    [Fact]
    public void Legacy_FromJsonElement_UnknownMethodCall_KeepsTheBinderError()
    {
        using var doc = JsonDocument.Parse(Json);
        dynamic root = RuleJsonDynamic.FromJsonElement(doc.RootElement);

        Should.Throw<RuntimeBinderException>(() => (object?)root.s.Substring(40)).Message.ShouldContain("does not contain a definition");
        ((bool)root.list.Contains(2)).ShouldBeTrue();
    }

    [Fact]
    public void Legacy_FromJsonElement_MemberOfNonObject_And_InapplicableIndex_StillFail()
    {
        using var doc = JsonDocument.Parse(Json);
        dynamic root = RuleJsonDynamic.FromJsonElement(doc.RootElement);

        Should.Throw<RuntimeBinderException>(() => (object?)root.n.deeper);
        Should.Throw<RuntimeBinderException>(() => (object?)root.list[5]);
        Should.Throw<RuntimeBinderException>(() => (object?)root.o[0]);
        ((object?)root.missing).ShouldBeNull(); // unchanged: a missing property was already null
    }
}
