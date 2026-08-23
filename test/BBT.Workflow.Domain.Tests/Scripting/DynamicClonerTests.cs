using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Text.Json;
using Xunit;

namespace BBT.Workflow.Scripting;

/// <summary>
/// Pins the Katman 2 / Task 4 structural-clone contract (B6/B10d): <see cref="DynamicCloner"/>
/// must be JSON-equivalent to the legacy JSON round-trip clone for every ToDynamic-shaped graph,
/// while never sharing a mutable container between source and clone.
/// </summary>
public class DynamicClonerTests
{
    /// <summary>Legacy clone path, byte-for-byte what CloneDynamic did: serialize + parse + expando.</summary>
    private static dynamic? LegacyClone(object? value)
    {
        if (value == null)
            return null;

        var serialized = JsonSerializer.Serialize(value, ScriptContext.JsonScriptBodyOptions);
        using var document = JsonDocument.Parse(serialized);
        return document.RootElement.ToDynamic();
    }

    private static dynamic? FromJson(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ToDynamic();
    }

    public static IEnumerable<object[]> Corpus()
    {
        yield return new object[] { """{"a":1,"b":"x"}""" };
        yield return new object[] { """{"a":{"b":{"c":[1,2,3]}},"d":null}""" };
        yield return new object[] { """{"arr":[{"k":1},{"k":2},[true,false],"s",null]}""" };
        yield return new object[] { """{"tr":"şğüİı","esc":"a\"b\\c","emoji":"🙂"}""" };
        yield return new object[] { """{"n1":1,"n2":1.5,"n3":-42,"n4":1e5}""" };
        yield return new object[] { """{"empty":{},"emptyArr":[]}""" };
        yield return new object[] { """[1,{"nested":[{"deep":"leaf"}]},2]""" };
        yield return new object[] { """{"bool":true,"other":false}""" };
    }

    [Theory]
    [MemberData(nameof(Corpus))]
    public void DeepClone_IsJsonEquivalent_ToLegacyRoundTripClone(string json)
    {
        var source = FromJson(json);

        var structural = DynamicCloner.DeepClone((object?)source);
        var legacy = LegacyClone((object?)source);

        Assert.Equal(
            JsonSerializer.Serialize((object?)legacy, ScriptContext.JsonScriptBodyOptions),
            JsonSerializer.Serialize(structural, ScriptContext.JsonScriptBodyOptions));
        // And both stay equivalent to the original document content.
        Assert.Equal(
            JsonSerializer.Serialize((object?)source, ScriptContext.JsonScriptBodyOptions),
            JsonSerializer.Serialize(structural, ScriptContext.JsonScriptBodyOptions));
    }

    [Fact]
    public void DeepClone_MutatingClone_DoesNotAffectSource()
    {
        var source = FromJson("""{"a":{"b":1},"arr":[{"k":1}],"leaf":"x"}""");
        var sourceJsonBefore = JsonSerializer.Serialize((object?)source, ScriptContext.JsonScriptBodyOptions);

        var clone = (IDictionary<string, object?>)DynamicCloner.DeepClone((object?)source)!;
        ((IDictionary<string, object?>)clone["a"]!)["b"] = 99;
        ((IDictionary<string, object?>)((List<object?>)clone["arr"]!)[0]!)["k"] = 99;
        clone["injected"] = "yes";

        Assert.Equal(
            sourceJsonBefore,
            JsonSerializer.Serialize((object?)source, ScriptContext.JsonScriptBodyOptions));
    }

    [Fact]
    public void DeepClone_MutatingSource_DoesNotAffectClone()
    {
        var source = FromJson("""{"a":{"b":1},"arr":[1,2]}""");
        var clone = DynamicCloner.DeepClone((object?)source);
        var cloneJsonBefore = JsonSerializer.Serialize(clone, ScriptContext.JsonScriptBodyOptions);

        var sourceDict = (IDictionary<string, object?>)source!;
        ((IDictionary<string, object?>)sourceDict["a"]!)["b"] = 42;
        ((List<object?>)sourceDict["arr"]!).Add(3);

        Assert.Equal(cloneJsonBefore, JsonSerializer.Serialize(clone, ScriptContext.JsonScriptBodyOptions));
    }

    [Fact]
    public void DeepClone_ContainersAreCopied_LeavesAreShared()
    {
        var source = FromJson("""{"nested":{"s":"shared-string"},"arr":[1]}""");
        var sourceDict = (IDictionary<string, object?>)source!;

        var clone = (IDictionary<string, object?>)DynamicCloner.DeepClone((object?)source)!;

        Assert.NotSame(sourceDict, clone);
        Assert.NotSame(sourceDict["nested"], clone["nested"]);
        Assert.NotSame(sourceDict["arr"], clone["arr"]);
        // Immutable leaf shared by reference.
        Assert.Same(
            ((IDictionary<string, object?>)sourceDict["nested"]!)["s"],
            ((IDictionary<string, object?>)clone["nested"]!)["s"]);
    }

    [Fact]
    public void DeepClone_ObjectArray_IsCopied_NotShared()
    {
        // ExpandoObjectJsonConverter.ReadArray materializes object?[] — the cloner must not share it.
        var expando = new ExpandoObject();
        ((IDictionary<string, object?>)expando)["arr"] = new object?[] { 1, "x", null };

        var clone = (IDictionary<string, object?>)DynamicCloner.DeepClone(expando)!;

        Assert.NotSame(((IDictionary<string, object?>)expando)["arr"], clone["arr"]);
        var clonedArray = Assert.IsType<object?[]>(clone["arr"]);
        ((object?[])((IDictionary<string, object?>)expando)["arr"]!)[0] = 99;
        Assert.Equal(1, clonedArray[0]);
    }

    [Fact]
    public void DeepClone_NullAndLeaves_PassThrough()
    {
        Assert.Null(DynamicCloner.DeepClone(null));
        Assert.Equal("x", DynamicCloner.DeepClone("x"));
        Assert.Equal(42L, DynamicCloner.DeepClone(42L));
        Assert.Equal(true, DynamicCloner.DeepClone(true));
    }

    [Fact]
    public void DeepClone_CyclicExpando_ThrowsInsteadOfStackOverflow()
    {
        // Script-yapımı döngü: legacy JSON round-trip IgnoreCycles ile sessizce düşürüyordu;
        // yapısal klon sınırsız recursion'da StackOverflow (= process ölümü) yapardı. Bekçi bunu
        // tanılanabilir bir exception'a çevirir.
        dynamic cyclic = new System.Dynamic.ExpandoObject();
        ((IDictionary<string, object?>)cyclic)["self"] = cyclic;

        var ex = Assert.Throws<InvalidOperationException>(() => DynamicCloner.DeepClone(cyclic));
        Assert.Contains("cycle", ex.Message);
    }
}
