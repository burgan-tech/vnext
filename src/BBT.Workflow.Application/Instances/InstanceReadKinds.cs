namespace BBT.Workflow.Instances;

/// <summary>
/// Which built-in read an <c>Instance.Read/{kind}</c> envelope covers.
/// <para>
/// A closed set, which is what lets the value sit in the span name instead of only in a tag. The
/// five that also name a subflow descent (<c>state</c>, <c>data</c>, <c>view</c>, <c>schema</c>,
/// <c>master</c>, <c>extensions</c>) deliberately use the SAME strings as
/// <c>TelemetryConstants.DescentFunctions</c>: one read can descend several levels, and a reader
/// correlating the envelope with the ladder underneath it should not have to translate between two
/// vocabularies for the same function.
/// </para>
/// </summary>
public static class InstanceReadKinds
{
    /// <summary>The long-polling state function.</summary>
    public const string State = "state";

    /// <summary>The data function.</summary>
    public const string Data = "data";

    /// <summary>The view function.</summary>
    public const string View = "view";

    /// <summary>The transition/state schema function.</summary>
    public const string Schema = "schema";

    /// <summary>The master-schema function.</summary>
    public const string Master = "master";

    /// <summary>The extensions function.</summary>
    public const string Extensions = "extensions";

    /// <summary>A single instance resource read.</summary>
    public const string Instance = "instance";

    /// <summary>The filtered/paged instance list, including group-by and aggregations.</summary>
    public const string List = "list";

    /// <summary>The transition history of one instance.</summary>
    public const string History = "history";

    /// <summary>The newest unresolved incident of one instance.</summary>
    public const string IncidentActive = "incidentActive";

    /// <summary>The paged incident history of one instance.</summary>
    public const string IncidentHistory = "incidentHistory";

    /// <summary>The subflow hierarchy tree — recursive, so its own duration says how deep it went.</summary>
    public const string Hierarchy = "hierarchy";

    /// <summary>The human-task inbox across workflows — fans out in parallel per workflow.</summary>
    public const string HumanTasks = "humanTasks";
}
