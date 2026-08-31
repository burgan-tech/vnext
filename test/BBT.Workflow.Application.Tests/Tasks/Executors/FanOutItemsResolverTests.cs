using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Text.Json;
using BBT.Workflow.Tasks.Executors;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Tasks.Executors;

public class FanOutItemsResolverTests
{
    private static readonly JsonElement Data = JsonDocument.Parse("""
    {
      "customer": { "id": "c-1" },
      "documents": [
        { "id": "doc-1", "url": "u1" },
        { "id": "doc-2", "url": "u2" }
      ],
      "batch": { "inner": { "items": [1, 2, 3] } },
      "notAnArray": { "id": "x" },
      "onlyKey": [
        { "key": "k-1" },
        { "key": "k-2" }
      ],
      "emptyList": []
    }
    """).RootElement;

    [Fact]
    public void Resolve_Should_Return_Items_With_Id_As_ItemKey()
    {
        var items = FanOutItemsResolver.Resolve(Data, "$.documents");
        items.Count.ShouldBe(2);
        items[0].Index.ShouldBe(0);
        items[0].ItemKey.ShouldBe("doc-1");
        items[1].ItemKey.ShouldBe("doc-2");
    }

    [Fact]
    public void Resolve_Should_Walk_Nested_Path()
    {
        var items = FanOutItemsResolver.Resolve(Data, "$.batch.inner.items");
        items.Count.ShouldBe(3);
        items[2].ItemKey.ShouldBe("2"); // primitive item → index as key
    }

    [Fact]
    public void Resolve_Should_Return_Empty_For_Missing_Path()
    {
        FanOutItemsResolver.Resolve(Data, "$.nope").ShouldBeEmpty();
    }

    [Fact]
    public void Resolve_Should_Throw_When_Path_Targets_Non_Array()
    {
        Should.Throw<InvalidOperationException>(() =>
            FanOutItemsResolver.Resolve(Data, "$.notAnArray"));
    }

    [Fact]
    public void Resolve_Should_Use_Key_Property_When_Id_Absent()
    {
        var items = FanOutItemsResolver.Resolve(Data, "$.onlyKey");
        items[0].ItemKey.ShouldBe("k-1");
        items[1].ItemKey.ShouldBe("k-2");
    }

    [Fact]
    public void Resolve_Should_Use_Index_When_Neither_Id_Nor_Key_Present()
    {
        var items = FanOutItemsResolver.Resolve(Data, "$.batch.inner.items");
        items[0].ItemKey.ShouldBe("0");
        items[1].ItemKey.ShouldBe("1");
    }

    [Fact]
    public void Resolve_Should_Return_Empty_For_Empty_Array()
    {
        FanOutItemsResolver.Resolve(Data, "$.emptyList").ShouldBeEmpty();
    }

    [Fact]
    public void Resolve_Should_Return_Empty_When_Path_Traverses_Into_Non_Object()
    {
        // "$.customer.id" resolves to a string; ".deeper" cannot navigate into it → treated as missing.
        FanOutItemsResolver.Resolve(Data, "$.customer.id.deeper").ShouldBeEmpty();
    }

    [Fact]
    public void Resolve_Should_Return_Item_Value_Convertible_Like_Instance_Data()
    {
        var items = FanOutItemsResolver.Resolve(Data, "$.documents");
        // Same ExpandoObject-based dynamic conversion used for Instance.Data (JsonElement.ToDynamic()).
        ((string)items[0].Value!.id).ShouldBe("doc-1");
        ((string)items[0].Value!.url).ShouldBe("u1");
    }

    [Fact]
    public void Resolve_Should_Fall_Through_To_Key_When_Id_Is_An_Empty_String()
    {
        // An empty id is not an identity. It falls through to 'key', and then to the index —
        // the same non-empty requirement the mapping-side projection applies.
        var data = JsonDocument.Parse("""
        { "items": [ { "id": "", "key": "k-0" }, { "id": "", "key": "" } ] }
        """).RootElement;

        var items = FanOutItemsResolver.Resolve(data, "$.items");

        items[0].ItemKey.ShouldBe("k-0");
        items[1].ItemKey.ShouldBe("1");
    }

    [Fact]
    public void Resolve_Should_Ignore_Non_String_Id_And_Key()
    {
        var data = JsonDocument.Parse("""{ "items": [ { "id": 42 } ] }""").RootElement;

        FanOutItemsResolver.Resolve(data, "$.items")[0].ItemKey.ShouldBe("0");
    }

    [Fact]
    public void Project_Should_Index_The_Selected_Values_In_Order()
    {
        var items = FanOutItemsResolver.Project(["a", "b", "c"]);

        items.Count.ShouldBe(3);
        items.Select(i => i.Index).ShouldBe([0, 1, 2]);
        items.Select(i => (string)i.Value!).ShouldBe(["a", "b", "c"]);
        // A bare scalar carries no id/key, so it is keyed by index.
        items.Select(i => i.ItemKey).ShouldBe(["0", "1", "2"]);
    }

    [Fact]
    public void Project_Should_Apply_The_Same_Key_Rule_To_Every_Value_Shape()
    {
        // The three shapes an item source can produce: a raw JsonElement, the ExpandoObject that
        // JsonElement.ToDynamic() yields, and a plain CLR object from a .csx ItemSelector.
        var element = JsonDocument.Parse("""{ "id": "from-json" }""").RootElement;

        IDictionary<string, object?> expando = new ExpandoObject();
        expando["id"] = "from-expando";

        var items = FanOutItemsResolver.Project([element, expando, new { id = "from-clr" }]);

        items.Select(i => i.ItemKey).ShouldBe(["from-json", "from-expando", "from-clr"]);
    }

    [Fact]
    public void Project_Should_Prefer_Id_Then_Key_Then_Index_For_Every_Shape()
    {
        IDictionary<string, object?> keyOnlyExpando = new ExpandoObject();
        keyOnlyExpando["key"] = "expando-key";

        IDictionary<string, object?> emptyIdExpando = new ExpandoObject();
        emptyIdExpando["id"] = string.Empty;
        emptyIdExpando["key"] = "expando-fallback";

        var items = FanOutItemsResolver.Project([
            new { id = "clr-id", key = "clr-key" },   // id wins over key
            new { key = "clr-key-only" },             // key when id is absent
            keyOnlyExpando,
            emptyIdExpando,                           // empty id is not an identity
            new { id = 7 },                           // non-string id is ignored
            new { name = "no-identity" }
        ]);

        items.Select(i => i.ItemKey)
            .ShouldBe(["clr-id", "clr-key-only", "expando-key", "expando-fallback", "4", "5"]);
    }

    [Fact]
    public void Project_Should_Key_Null_Values_By_Index()
    {
        var items = FanOutItemsResolver.Project([null, new { id = "b" }]);

        items[0].ItemKey.ShouldBe("0");
        // Cast first: extension methods cannot be dispatched on a null dynamic.
        ((object?)items[0].Value).ShouldBeNull();
        items[1].ItemKey.ShouldBe("b");
    }

    [Fact]
    public void Project_Should_Return_Empty_For_An_Empty_Sequence()
    {
        FanOutItemsResolver.Project([]).ShouldBeEmpty();
    }
}
