namespace BBT.Workflow.Instances;

/// <summary>Reversible read routing; disabling a route does not remove physical indexes.</summary>
public sealed class InstanceQueryOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "InstanceQueries";
    /// <summary>Select only identities before hydrating the requested page.</summary>
    public bool IdentityPaging { get; set; } = true;
    /// <summary>Use the unique latest-data join in identity selection.</summary>
    public bool LatestJoin { get; set; } = true;
    /// <summary>Physical schema names that continue to use the previous entity-list route.</summary>
    public string[] DisabledSchemas { get; set; } = [];
}
