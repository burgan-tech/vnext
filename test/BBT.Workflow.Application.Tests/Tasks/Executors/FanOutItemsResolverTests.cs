using System;
using System.Text.Json;
using BBT.Workflow.Tasks.Executors.FanOut;
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
}
