namespace BBT.Workflow.Schemas;

public sealed class AttributeIndexOptions
{
    public const string SectionName = "AttributeIndexes";
    public bool Enabled { get; set; }
    public string[] DisabledFlows { get; set; } = [];
    public int CatalogCacheSeconds { get; set; } = 30;
}
