namespace BBT.Workflow.Definitions.RowSecurity;

/// <summary>
/// Error codes reported for an invalid <c>attributes.rowSecurity</c> declaration (vnext#1107).
/// </summary>
/// <remarks>
/// One table for every producer: the filter-template parser (<see cref="RowFilterTemplateParser"/>)
/// reports <see cref="E06"/>, <see cref="E07"/>, <see cref="E09"/>, <see cref="E10"/>,
/// <see cref="E11"/> and <see cref="E13"/>; the publish-time validators report the rest. Messages that
/// accompany these codes are English and never echo authored values.
/// </remarks>
public static class RowSecurityErrorCodes
{
    /// <summary><c>$PreviousUser</c> / <c>$PreviousBehalfOfUser</c> used as a rule role.</summary>
    public const string E01 = "RS-E01";

    /// <summary>A dynamic role grant (<c>$user.</c> / <c>$userBehalfOf.</c> / <c>$role.</c>) used as a rule role.</summary>
    public const string E02 = "RS-E02";

    /// <summary>
    /// An <c>allOf</c> / <c>anyOf</c> combinator used as a rule role. <b>Reserved, not emitted by the runtime:</b>
    /// <c>rules[].roles</c> binds as a string list, so a combinator object is refused at JSON binding
    /// (<c>400 App:900006</c>) before publish validation runs; <c>vnext-schema</c> validation reports it first.
    /// Kept so the code stays allocated to this case.
    /// </summary>
    public const string E03 = "RS-E03";

    /// <summary>Static roles mixed with a reserved role in one rule.</summary>
    public const string E04 = "RS-E04";

    /// <summary>A reserved role other than <c>$InstanceStarter</c> / <c>$InstanceBehalfOfStarter</c>.</summary>
    public const string E05 = "RS-E05";

    /// <summary>An instance-starter shorthand used on a column it does not belong to.</summary>
    public const string E06 = "RS-E06";

    /// <summary>A malformed <c>$.context.</c> parameter, unknown source or unknown <c>CurrentUser</c> field.</summary>
    public const string E07 = "RS-E07";

    /// <summary>A filter path that is, or sits under, an <c>x-encryption: encrypt</c> field.</summary>
    public const string E08 = "RS-E08";

    /// <summary>An invalid operator or an invalid filter JSON shape.</summary>
    public const string E09 = "RS-E09";

    /// <summary>A <c>between</c> operand that is not an array of exactly two values.</summary>
    public const string E10 = "RS-E10";

    /// <summary>A <c>split</c> on an operator that does not take an array.</summary>
    public const string E11 = "RS-E11";

    /// <summary><c>rules</c> missing or empty, or an unknown member on <c>rowSecurity</c> or a rule.</summary>
    public const string E12 = "RS-E12";

    /// <summary><c>sort</c>, <c>orderBy</c>, <c>groupBy</c> or <c>aggregations</c> inside a rule filter.</summary>
    public const string E13 = "RS-E13";

    /// <summary>A filter on an <c>x-encryption: hash</c> field.</summary>
    public const string E14 = "RS-E14";

    /// <summary>A filter on an <c>x-masking</c> path.</summary>
    public const string E15 = "RS-E15";

    /// <summary><c>rowSecurity</c> declared on a <c>sys-*</c> flow.</summary>
    public const string E16 = "RS-E16";

    /// <summary>A parameter on <c>like</c>/<c>match</c>/<c>startswith</c>/<c>endswith</c> or inside <c>includes</c>.</summary>
    public const string E17 = "RS-E17";

    /// <summary>A <c>QueryParameters</c> leaf on an <c>x-roles</c>/<c>x-masking</c>/<c>x-encryption</c> path.</summary>
    public const string E18 = "RS-E18";
}
