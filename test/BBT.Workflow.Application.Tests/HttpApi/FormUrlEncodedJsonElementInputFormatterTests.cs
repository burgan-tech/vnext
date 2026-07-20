using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using BBT.Workflow.Formatters;
using BBT.Workflow.Instances;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.HttpApi;

/// <summary>
/// Unit tests for <see cref="FormUrlEncodedJsonElementInputFormatter"/>: it accepts
/// <c>application/x-www-form-urlencoded</c> bodies, projects them into a <see cref="JsonElement"/>
/// (supporting bracket-notation nesting and arrays, with string leaf values), and leaves
/// <c>application/json</c> to the default formatter.
/// </summary>
public class FormUrlEncodedJsonElementInputFormatterTests
{
    private const string FormContentType = "application/x-www-form-urlencoded";

    private static readonly EmptyModelMetadataProvider MetadataProvider = new();

    private static InputFormatterContext BuildContext(
        string? contentType,
        string body,
        Type modelType,
        string? payloadMode = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.ContentType = contentType;
        if (payloadMode is not null)
        {
            httpContext.Request.Headers["x-vnext-payload-mode"] = payloadMode;
        }

        var bytes = Encoding.UTF8.GetBytes(body);
        httpContext.Request.Body = new MemoryStream(bytes);
        httpContext.Request.ContentLength = bytes.Length;

        var metadata = MetadataProvider.GetMetadataForType(modelType);

        return new InputFormatterContext(
            httpContext,
            modelName: string.Empty,
            modelState: new ModelStateDictionary(),
            metadata: metadata,
            readerFactory: (stream, encoding) => new StreamReader(stream, encoding),
            treatEmptyInputAsDefaultValue: true);
    }

    private static async Task<JsonElement> ReadElementAsync(
        FormUrlEncodedJsonElementInputFormatter formatter,
        string body,
        string? payloadMode = null)
    {
        var context = BuildContext(FormContentType, body, typeof(JsonElement?), payloadMode);
        var result = await formatter.ReadRequestBodyAsync(context, Encoding.UTF8);
        result.HasError.ShouldBeFalse();
        result.Model.ShouldNotBeNull();
        return (JsonElement)result.Model!;
    }

    [Fact]
    public void CanRead_FormUrlEncodedContentType_ReturnsTrue()
    {
        var formatter = new FormUrlEncodedJsonElementInputFormatter();
        var context = BuildContext(FormContentType, "a=1", typeof(JsonElement?));

        formatter.CanRead(context).ShouldBeTrue();
    }

    [Fact]
    public void CanRead_JsonContentType_ReturnsFalse()
    {
        var formatter = new FormUrlEncodedJsonElementInputFormatter();
        var context = BuildContext("application/json", "{}", typeof(JsonElement?));

        formatter.CanRead(context).ShouldBeFalse();
    }

    [Fact]
    public async Task ReadRequestBodyAsync_RawFlatFields_ProjectsJsonLiteralAndStringLeaves()
    {
        var formatter = new FormUrlEncodedJsonElementInputFormatter();

        var element = await ReadElementAsync(formatter, "amount=100&currency=TRY");

        element.ValueKind.ShouldBe(JsonValueKind.Object);
        element.GetProperty("amount").ValueKind.ShouldBe(JsonValueKind.Number);
        element.GetProperty("amount").GetInt32().ShouldBe(100);
        element.GetProperty("currency").GetString().ShouldBe("TRY");
    }

    [Theory]
    [InlineData("age=30", "age", JsonValueKind.Number)]
    [InlineData("active=true", "active", JsonValueKind.True)]
    [InlineData("active=false", "active", JsonValueKind.False)]
    [InlineData("value=null", "value", JsonValueKind.Null)]
    public async Task ReadRequestBodyAsync_RawJsonLiteral_UsesJsonType(
        string body,
        string property,
        JsonValueKind expectedKind)
    {
        var element = await ReadElementAsync(new FormUrlEncodedJsonElementInputFormatter(), body);

        element.GetProperty(property).ValueKind.ShouldBe(expectedKind);
    }

    [Fact]
    public async Task ReadRequestBodyAsync_QuotedScalar_PreservesString()
    {
        var element = await ReadElementAsync(
            new FormUrlEncodedJsonElementInputFormatter(),
            "code=%2200123%22");

        element.GetProperty("code").ValueKind.ShouldBe(JsonValueKind.String);
        element.GetProperty("code").GetString().ShouldBe("00123");
    }

    [Fact]
    public async Task ReadRequestBodyAsync_LargeNumericKey_PreservedAsString()
    {
        var formatter = new FormUrlEncodedJsonElementInputFormatter();

        var element = await ReadElementAsync(
            formatter,
            "key=50044086191232280074134",
            payloadMode: "standard");

        element.GetProperty("key").ValueKind.ShouldBe(JsonValueKind.String);
        element.GetProperty("key").GetString().ShouldBe("50044086191232280074134");
    }

    [Fact]
    public async Task ReadRequestBodyAsync_StandardEnvelope_PreservesEnvelopeStringsAndTypesAttributes()
    {
        var element = await ReadElementAsync(
            new FormUrlEncodedJsonElementInputFormatter(),
            "key=50044086191232280074134&stage=123&tags[]=1&attributes[age]=30",
            payloadMode: "standard");

        element.GetProperty("key").ValueKind.ShouldBe(JsonValueKind.String);
        element.GetProperty("stage").ValueKind.ShouldBe(JsonValueKind.String);
        element.GetProperty("tags")[0].ValueKind.ShouldBe(JsonValueKind.String);
        element.GetProperty("attributes").GetProperty("age").ValueKind.ShouldBe(JsonValueKind.Number);
    }

    [Fact]
    public async Task ReadRequestBodyAsync_StandardEnvelope_PreservesEnvelopeStringsCaseInsensitively()
    {
        var element = await ReadElementAsync(
            new FormUrlEncodedJsonElementInputFormatter(),
            "Key=50044086191232280074134&Stage=123&Tags[]=1&Attributes[age]=30",
            payloadMode: "standard");

        element.GetProperty("Key").ValueKind.ShouldBe(JsonValueKind.String);
        element.GetProperty("Stage").ValueKind.ShouldBe(JsonValueKind.String);
        element.GetProperty("Tags")[0].ValueKind.ShouldBe(JsonValueKind.String);
        element.GetProperty("Attributes").GetProperty("age").ValueKind.ShouldBe(JsonValueKind.Number);
    }

    [Fact]
    public async Task ReadRequestBodyAsync_BracketNotation_BuildsNestedObject()
    {
        var formatter = new FormUrlEncodedJsonElementInputFormatter();

        // attributes[session]=... & attributes[customer][ownerUserId]=...
        var element = await ReadElementAsync(
            formatter,
            "attributes[session]=423432&attributes[customer][ownerUserId]=2321321");

        var attributes = element.GetProperty("attributes");
        attributes.ValueKind.ShouldBe(JsonValueKind.Object);
        attributes.GetProperty("session").GetInt32().ShouldBe(423432);
        attributes.GetProperty("customer").GetProperty("ownerUserId").GetInt32().ShouldBe(2321321);
    }

    [Fact]
    public async Task ReadRequestBodyAsync_RepeatedKey_ProjectsToJsonArray()
    {
        var formatter = new FormUrlEncodedJsonElementInputFormatter();

        var element = await ReadElementAsync(formatter, "tags=a&tags=b");

        var tags = element.GetProperty("tags");
        tags.ValueKind.ShouldBe(JsonValueKind.Array);
        tags.GetArrayLength().ShouldBe(2);
        tags[0].GetString().ShouldBe("a");
        tags[1].GetString().ShouldBe("b");
    }

    [Fact]
    public async Task ReadRequestBodyAsync_EmptyBracketNotation_ProjectsToJsonArray()
    {
        var formatter = new FormUrlEncodedJsonElementInputFormatter();

        var element = await ReadElementAsync(formatter, "tags[]=a&tags[]=b");

        var tags = element.GetProperty("tags");
        tags.ValueKind.ShouldBe(JsonValueKind.Array);
        tags.GetArrayLength().ShouldBe(2);
        tags[0].GetString().ShouldBe("a");
        tags[1].GetString().ShouldBe("b");
    }

    [Fact]
    public async Task ReadRequestBodyAsync_IndexedObjectArray_BuildsJsonArray()
    {
        var element = await ReadElementAsync(
            new FormUrlEncodedJsonElementInputFormatter(),
            "items[0][name]=A&items[1][name]=B");

        var items = element.GetProperty("items");
        items.ValueKind.ShouldBe(JsonValueKind.Array);
        items[0].GetProperty("name").GetString().ShouldBe("A");
        items[1].GetProperty("name").GetString().ShouldBe("B");
    }

    [Fact]
    public async Task ReadRequestBodyAsync_CompleteOutOfOrderIndexedArray_BuildsJsonArray()
    {
        var element = await ReadElementAsync(
            new FormUrlEncodedJsonElementInputFormatter(),
            "items[1][name]=B&items[0][name]=A");

        var items = element.GetProperty("items");
        items[0].GetProperty("name").GetString().ShouldBe("A");
        items[1].GetProperty("name").GetString().ShouldBe("B");
    }

    [Theory]
    [InlineData("items[][name]=A")]
    [InlineData("items[1][name]=A")]
    [InlineData("a=x&a[b]=y")]
    [InlineData("a[b=x")]
    [InlineData("items[-1]=A")]
    [InlineData("items[1025]=A")] // index above MaxArrayIndex — rejected at parse, no padding allocation
    [InlineData("items[2000000000]=A")] // guards against large sparse-array memory allocation
    [InlineData("payload=%7B%22x%22%3A1%7D")]
    public async Task ReadRequestBodyAsync_InvalidOrAmbiguousShape_ReturnsFormatterFailure(string body)
    {
        var formatter = new FormUrlEncodedJsonElementInputFormatter();
        var context = BuildContext(FormContentType, body, typeof(JsonElement?));

        var result = await formatter.ReadRequestBodyAsync(context, Encoding.UTF8);

        result.HasError.ShouldBeTrue();
        context.ModelState.IsValid.ShouldBeFalse();
        context.ModelState.ErrorCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task ReadRequestBodyAsync_EmptyBody_ProjectsToEmptyJsonObject()
    {
        var formatter = new FormUrlEncodedJsonElementInputFormatter();

        var element = await ReadElementAsync(formatter, string.Empty);

        element.ValueKind.ShouldBe(JsonValueKind.Object);
        element.EnumerateObject().MoveNext().ShouldBeFalse();
    }

    // ---- Model-binding parity with JSON. TransitionDataInput mirrors CreateInstanceDto exactly
    //      (Key/Tags/Attributes/Stage), so it stands in for both the Start and Transition payloads.

    [Fact]
    public async Task ProjectedElement_StandardModelWithNestedAttributes_DeserializesIntoDto()
    {
        var formatter = new FormUrlEncodedJsonElementInputFormatter();

        // Mirrors the reported payload sent as form-urlencoded bracket notation.
        var body = "key=50044086191232280074134&stage=Initial"
                   + "&attributes[session]=423432"
                   + "&attributes[customer][ownerUserId]=2321321";

        var element = await ReadElementAsync(formatter, body);
        var dto = JsonSerializer.Deserialize<TransitionDataInput>(element, JsonSerializerOptions.Web);

        dto.ShouldNotBeNull();
        dto!.Key.ShouldBe("50044086191232280074134");
        dto.Stage.ShouldBe("Initial");
        dto.Attributes!.Value.GetProperty("session").GetInt32().ShouldBe(423432);
        dto.Attributes!.Value.GetProperty("customer").GetProperty("ownerUserId").GetInt32().ShouldBe(2321321);
    }

    [Fact]
    public async Task ProjectedElement_ModelTags_DeserializeIntoStringArray()
    {
        var formatter = new FormUrlEncodedJsonElementInputFormatter();

        var element = await ReadElementAsync(formatter, "key=k1&tags[]=a&tags[]=b");
        var dto = JsonSerializer.Deserialize<TransitionDataInput>(element, JsonSerializerOptions.Web);

        dto.ShouldNotBeNull();
        dto!.Key.ShouldBe("k1");
        dto.Tags.ShouldBe(new[] { "a", "b" });
    }

    [Fact]
    public async Task ProjectedElement_ModelSingleTag_DeserializesIntoSingleElementStringArray()
    {
        var formatter = new FormUrlEncodedJsonElementInputFormatter();

        var element = await ReadElementAsync(
            formatter,
            "key=k1&tags=a&attributes[name]=Ali");
        var dto = JsonSerializer.Deserialize<TransitionDataInput>(element, JsonSerializerOptions.Web);

        dto.ShouldNotBeNull();
        dto!.Tags.ShouldBe(new[] { "a" });
    }

    [Fact]
    public async Task ProjectedElement_FreeFormPayload_UsableAsAttributesBody()
    {
        var formatter = new FormUrlEncodedJsonElementInputFormatter();

        var element = await ReadElementAsync(formatter, "amount=100&currency=TRY");

        // No top-level "attributes" ⇒ free-form ⇒ controller wraps the whole body as Attributes.
        element.TryGetProperty("attributes", out _).ShouldBeFalse();
        var dto = new TransitionDataInput(element);
        dto.Attributes!.Value.GetProperty("amount").GetInt32().ShouldBe(100);
        dto.Attributes!.Value.GetProperty("currency").GetString().ShouldBe("TRY");
    }
}
