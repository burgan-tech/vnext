using System.Text.Json;
using BBT.Workflow.Definitions.Schemas;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions.Schemas;

public sealed class SchemaFilterMetadataResolverTests
{
    [Fact]
    public void Resolve_WhenEmptyObject_ReturnsNull()
    {
        var schema = JsonDocument.Parse("{}").RootElement;
        var result = SchemaFilterMetadataResolver.Resolve(schema);
        result.ShouldBeNull();
    }

    [Fact]
    public void Resolve_WhenNoProperties_ReturnsNull()
    {
        var schema = JsonDocument.Parse(@"{ ""type"": ""object"" }").RootElement;
        var result = SchemaFilterMetadataResolver.Resolve(schema);
        result.ShouldBeNull();
    }

    [Fact]
    public void Resolve_WhenPropertiesWithAllExtensions_ParsesCorrectly()
    {
        var schema = JsonDocument.Parse(@"{
            ""type"": ""object"",
            ""properties"": {
                ""createdAt"": {
                    ""type"": ""string"",
                    ""x-filterOperators"": [""eq"", ""gt"", ""gte"", ""lt"", ""lte""],
                    ""x-sortable"": true,
                    ""x-displayFormat"": ""dd.MM.yyyy HH:mm""
                },
                ""amount"": {
                    ""type"": ""number"",
                    ""x-filterOperators"": [""eq"", ""gt"", ""gte"", ""lt"", ""lte""],
                    ""x-sortable"": true
                },
                ""customerName"": {
                    ""type"": ""string"",
                    ""x-filterOperators"": [""eq"", ""contains"", ""startsWith"", ""endsWith""],
                    ""x-sortable"": true
                },
                ""isActive"": {
                    ""type"": ""boolean"",
                    ""x-filterOperators"": [""eq"", ""neq""]
                }
            }
        }").RootElement;

        var context = SchemaFilterMetadataResolver.Resolve(schema);
        context.ShouldNotBeNull();

        var createdAt = context.GetFieldMetadata("createdAt");
        createdAt.ShouldNotBeNull();
        createdAt.Type.ShouldBe("string");
        createdAt.FilterOperators.Count.ShouldBe(5);
        createdAt.Sortable.ShouldBeTrue();
        createdAt.DisplayFormat.ShouldBe("dd.MM.yyyy HH:mm");

        var amount = context.GetFieldMetadata("amount");
        amount.ShouldNotBeNull();
        amount.Type.ShouldBe("number");
        amount.Sortable.ShouldBeTrue();
        amount.DisplayFormat.ShouldBeNull();

        var customerName = context.GetFieldMetadata("customerName");
        customerName.ShouldNotBeNull();
        customerName.Type.ShouldBe("string");
        customerName.FilterOperators.ShouldContain("contains");
        customerName.FilterOperators.ShouldContain("startsWith");

        var isActive = context.GetFieldMetadata("isActive");
        isActive.ShouldNotBeNull();
        isActive.Type.ShouldBe("boolean");
        isActive.FilterOperators.Count.ShouldBe(2);
        isActive.Sortable.ShouldBeFalse();
    }

    [Fact]
    public void Resolve_WhenFieldHasNoExtensions_StillIncludedWithDefaults()
    {
        var schema = JsonDocument.Parse(@"{
            ""properties"": {
                ""plainField"": { ""type"": ""string"" }
            }
        }").RootElement;

        var context = SchemaFilterMetadataResolver.Resolve(schema);
        context.ShouldNotBeNull();

        var field = context.GetFieldMetadata("plainField");
        field.ShouldNotBeNull();
        field.Type.ShouldBe("string");
        field.IsFilterable.ShouldBeFalse();
        field.Sortable.ShouldBeFalse();
    }

    [Fact]
    public void Resolve_WhenNestedProperties_UsesDotNotation()
    {
        var schema = JsonDocument.Parse(@"{
            ""properties"": {
                ""address"": {
                    ""type"": ""object"",
                    ""properties"": {
                        ""city"": {
                            ""type"": ""string"",
                            ""x-filterOperators"": [""eq"", ""contains""],
                            ""x-sortable"": true
                        }
                    }
                }
            }
        }").RootElement;

        var context = SchemaFilterMetadataResolver.Resolve(schema);
        context.ShouldNotBeNull();

        var city = context.GetFieldMetadata("address.city");
        city.ShouldNotBeNull();
        city.Type.ShouldBe("string");
        city.IsFilterable.ShouldBeTrue();
    }

    [Fact]
    public void Resolve_WhenEmptyFilterOperators_FieldIsNotFilterable()
    {
        var schema = JsonDocument.Parse(@"{
            ""properties"": {
                ""readOnly"": {
                    ""type"": ""string"",
                    ""x-filterOperators"": []
                }
            }
        }").RootElement;

        var context = SchemaFilterMetadataResolver.Resolve(schema);
        context.ShouldNotBeNull();

        var field = context.GetFieldMetadata("readOnly");
        field.ShouldNotBeNull();
        field.IsFilterable.ShouldBeFalse();
    }
}
