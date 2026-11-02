using Json.Schema;

namespace BBT.Workflow.Validation;

/// <summary>Build options shared by every place the runtime compiles a component schema for evaluation.</summary>
internal static class JsonSchemaBuild
{
    /// <summary>
    /// An isolated <see cref="SchemaRegistry"/> (several versions of one schema share a <c>$id</c>) and Draft 2020-12 as
    /// the dialect of a schema that names none. JsonSchema.Net 8 otherwise falls back to its v1 dialect, which refuses
    /// unknown keywords, so a schema carrying the runtime vocabulary (<c>x-storage</c>, <c>x-roles</c>, <c>x-masking</c>,
    /// …) without <c>$schema</c> published fine and then failed the first write with a 500. A schema that declares
    /// <c>$schema</c> keeps its own dialect.
    /// </summary>
    public static BuildOptions Options() => new()
    {
        SchemaRegistry = new SchemaRegistry(),
        Dialect = Dialect.Draft202012
    };
}
