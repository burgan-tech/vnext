namespace BBT.Workflow.Authorization.RowSecurity;

/// <summary>Limits applied to request-supplied row-security parameter values.</summary>
public sealed class RowSecurityOptions
{
    /// <summary>Configuration section.</summary>
    public const string Section = "Workflow:RowSecurity";

    /// <summary>Maximum number of elements one parameter may carry; more drops the rule.</summary>
    public int MaxElements { get; set; } = 100;

    /// <summary>Maximum length of one element; longer drops the rule.</summary>
    public int MaxElementLength { get; set; } = 1000;
}
