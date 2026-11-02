using System.Diagnostics.CodeAnalysis;

namespace BBT.Workflow.Definitions.RowSecurity;

/// <summary>
/// Where a row-security parameter value is read from at request time.
/// </summary>
public enum RowParameterSource
{
    /// <summary><c>$.context.Headers.&lt;name&gt;</c>: a request header.</summary>
    Headers,

    /// <summary><c>$.context.QueryParameters.&lt;name&gt;</c>: a query-string parameter.</summary>
    QueryParameters,

    /// <summary><c>$.context.CurrentUser.&lt;field&gt;</c>: a field of the authenticated caller.</summary>
    CurrentUser
}

/// <summary>
/// A parsed row-security parameter path, e.g. <c>$.context.Headers.x-branch-code</c>.
/// </summary>
/// <param name="Source">The value source.</param>
/// <param name="Name">The header / query-parameter name, or the <c>CurrentUser</c> field.</param>
public sealed record RowParameterPath(RowParameterSource Source, string Name)
{
    /// <summary>Prefix marking a string as a parameter; compared ordinally, like <see cref="DynamicRoleGrant"/>.</summary>
    public const string Prefix = "$.context.";

    private const string HeadersSegment = "Headers";
    private const string QueryParametersSegment = "QueryParameters";
    private const string CurrentUserSegment = "CurrentUser";

    /// <summary>The <c>CurrentUser</c> fields a parameter may read.</summary>
    public static readonly IReadOnlySet<string> CurrentUserFields =
        new HashSet<string>(StringComparer.Ordinal) { "ActorUserName", "ActorUserId", "UserName", "Position" };

    /// <summary>
    /// True when <paramref name="literal"/> starts with <see cref="Prefix"/> (ordinal) and is therefore
    /// meant as a parameter — well-formed or not. Anything else is a literal value.
    /// </summary>
    public static bool LooksLikeParameter([NotNullWhen(true)] string? literal) =>
        literal is not null && literal.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// Parses <paramref name="text"/> as a parameter path.
    /// </summary>
    /// <param name="text">The authored string.</param>
    /// <param name="path">The parsed path on success; otherwise null.</param>
    /// <param name="errorCode">
    /// <see cref="RowSecurityErrorCodes.E07"/> when the text looks like a parameter but is malformed
    /// (unknown source such as <c>Route</c> or <c>Query</c>, empty name, unknown <c>CurrentUser</c>
    /// field); null on success and when the text is not a parameter at all.
    /// </param>
    /// <returns>True when <paramref name="path"/> was parsed.</returns>
    public static bool TryParse(string text, [NotNullWhen(true)] out RowParameterPath? path, out string? errorCode)
    {
        path = null;
        errorCode = null;

        if (!LooksLikeParameter(text))
            return false;

        var remainder = text[Prefix.Length..];
        var dot = remainder.IndexOf('.');
        if (dot <= 0)
        {
            errorCode = RowSecurityErrorCodes.E07;
            return false;
        }

        var sourceSegment = remainder[..dot];
        var name = remainder[(dot + 1)..];
        if (string.IsNullOrWhiteSpace(name))
        {
            errorCode = RowSecurityErrorCodes.E07;
            return false;
        }

        RowParameterSource source;
        switch (sourceSegment)
        {
            case HeadersSegment:
                source = RowParameterSource.Headers;
                break;
            case QueryParametersSegment:
                source = RowParameterSource.QueryParameters;
                break;
            case CurrentUserSegment when CurrentUserFields.Contains(name):
                source = RowParameterSource.CurrentUser;
                break;
            default:
                errorCode = RowSecurityErrorCodes.E07;
                return false;
        }

        path = new RowParameterPath(source, name);
        return true;
    }
}
