using System;
using Xunit;

namespace BBT.Workflow.Scripting;

/// <summary>
/// Katman 2 / Task 5 (B10c): pins the lazy-materialization contract of
/// <see cref="ScriptTransitionRequest"/>'s factory constructor — the shape
/// <c>ScriptContextBuilder</c> relies on to avoid parsing the persisted
/// transition body/header into dynamic <c>ExpandoObject</c> graphs on every <c>ScriptContext</c>
/// build when the script never reads <c>CurrentTransition</c>.
/// </summary>
public class ScriptTransitionRequestLazyTests
{
    [Fact]
    public void LazyCtor_DoesNotInvokeFactories_UntilPropertyAccessed()
    {
        var dataCalls = 0;
        var headerCalls = 0;

        var request = new ScriptTransitionRequest(
            () => { dataCalls++; return "data-value"; },
            () => { headerCalls++; return "header-value"; });

        Assert.Equal(0, dataCalls);
        Assert.Equal(0, headerCalls);
    }

    [Fact]
    public void LazyCtor_DataFactory_RunsExactlyOnce_OnFirstAccessOnly()
    {
        var calls = 0;
        var request = new ScriptTransitionRequest(
            () => { calls++; return "data-value"; },
            () => "header-value");

        var first = request.Data;
        var second = request.Data;

        Assert.Equal(1, calls);
        Assert.Equal("data-value", (string)first!);
        Assert.Equal("data-value", (string)second!);
    }

    [Fact]
    public void LazyCtor_HeaderFactory_RunsExactlyOnce_OnFirstAccessOnly()
    {
        var calls = 0;
        var request = new ScriptTransitionRequest(
            () => "data-value",
            () => { calls++; return "header-value"; });

        var first = request.Header;
        var second = request.Header;

        Assert.Equal(1, calls);
        Assert.Equal("header-value", (string)first!);
        Assert.Equal("header-value", (string)second!);
    }

    [Fact]
    public void LazyCtor_DataAndHeader_AreIndependentlyMaterialized()
    {
        var dataCalls = 0;
        var headerCalls = 0;
        var request = new ScriptTransitionRequest(
            () => { dataCalls++; return "data-value"; },
            () => { headerCalls++; return "header-value"; });

        _ = request.Data;

        Assert.Equal(1, dataCalls);
        Assert.Equal(0, headerCalls);
    }

    [Fact]
    public void EagerCtor_StillExposesGivenValues_Unchanged()
    {
        var request = new ScriptTransitionRequest("eager-data", "eager-header");

        Assert.Equal("eager-data", (string)request.Data!);
        Assert.Equal("eager-header", (string)request.Header!);
    }

    [Fact]
    public void LazyCtor_NullFactories_Throw()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new ScriptTransitionRequest(null!, () => "header"));
        Assert.Throws<ArgumentNullException>(() =>
            new ScriptTransitionRequest(() => "data", null!));
    }
}
