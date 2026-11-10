using BBT.Aether.Users;
using BBT.Workflow.Definitions.RowSecurity;

namespace BBT.Workflow.Authorization.RowSecurity;

/// <summary>
/// <see cref="IRowSecurityParameterSource"/> over the request inputs. The raw multi-valued dictionaries
/// win; the single-valued ones are the fallback for task-built inputs. Header and query names are
/// matched case-insensitively. <c>CurrentUser</c> fields come from Aether <see cref="ICurrentUser"/>.
/// </summary>
public sealed class RequestRowParameterSource : IRowSecurityParameterSource
{
    private readonly IReadOnlyDictionary<string, string[]>? _rawHeaders;
    private readonly IReadOnlyDictionary<string, string?>? _headers;
    private readonly IReadOnlyDictionary<string, string[]>? _rawQuery;
    private readonly IReadOnlyDictionary<string, string?>? _query;
    private readonly ICurrentUser _currentUser;

    /// <summary>Creates the source over one request's inputs.</summary>
    public RequestRowParameterSource(
        IReadOnlyDictionary<string, string[]>? rawHeaders,
        IReadOnlyDictionary<string, string?>? headers,
        IReadOnlyDictionary<string, string[]>? rawQuery,
        IReadOnlyDictionary<string, string?>? query,
        ICurrentUser currentUser)
    {
        _rawHeaders = rawHeaders;
        _headers = headers;
        _rawQuery = rawQuery;
        _query = query;
        _currentUser = currentUser;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Get(RowParameterPath path) => path.Source switch
    {
        RowParameterSource.Headers => Lookup(_rawHeaders, _headers, path.Name),
        RowParameterSource.QueryParameters => Lookup(_rawQuery, _query, path.Name),
        RowParameterSource.CurrentUser => Single(path.Name switch
        {
            "ActorUserName" => _currentUser.ActorUserName,
            "ActorUserId" => _currentUser.ActorUserId,
            "UserName" => _currentUser.UserName,
            "Position" => _currentUser.Position,
            _ => null
        }),
        _ => []
    };

    private static IReadOnlyList<string> Lookup(
        IReadOnlyDictionary<string, string[]>? raw, IReadOnlyDictionary<string, string?>? flat, string name)
    {
        if (raw is not null)
        {
            var values = FindIgnoreCase(raw, name);
            if (values is not null)
                return values;
        }

        return flat is null ? [] : Single(FindIgnoreCase(flat, name));
    }

    private static T? FindIgnoreCase<T>(IReadOnlyDictionary<string, T> dictionary, string name)
    {
        if (dictionary.TryGetValue(name, out var exact))
            return exact;
        foreach (var pair in dictionary)
        {
            if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        }

        return default;
    }

    private static IReadOnlyList<string> Single(string? value) => value is null ? [] : [value];
}
