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
