namespace BBT.Workflow.Instances;

/// <summary>
/// Configuration for instance list filtering against workflow JSON data.
/// </summary>
public sealed class InstanceFilteringOptions
{
    /// <summary>
    /// Configuration section path (<c>Workflow:InstanceFiltering</c>).
    /// </summary>
    public const string SectionName = "Workflow:InstanceFiltering";

    /// <summary>
    /// When <c>true</c> (default), resolves <see cref="BBT.Workflow.Definitions.Schemas.SchemaFilterContext"/> from the workflow master schema
    /// and enforces <c>x-filterOperators</c> / <c>x-sortable</c> (including <c>::numeric</c> vs <c>::timestamptz</c> for comparisons).
    /// When <c>false</c>, behaves as if no schema context were supplied: permissive filtering and range operators use <c>::numeric</c> only.
    /// </summary>
    public bool EnforceMasterSchemaFiltering { get; set; } = false;
}
