using System.Diagnostics;
using System.Net;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Aether.Users;
using BBT.Workflow.Authorization.Configuration;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Authorization;

/// <summary>
/// Resolves the caller's operation set from morph-idm.
/// <para>
/// One GET to the <c>get-roles</c> function per DI scope, carrying the caller's <c>act_sub</c>,
/// <c>sub</c> and <c>position</c>. The <c>role</c> header is deliberately never sent: with it the
/// endpoint switches to its authorize behaviour and answers yes/no for that one role, whereas the
/// runtime needs the whole set so the existing grant engine can evaluate <c>transition.roles</c>,
/// <c>availableIn</c> narrowing, <c>queryRoles</c> and schema <c>x-roles</c> exactly as it always has.
/// </para>
/// <para>
/// <b>It never fails.</b> A caller with neither <c>act_sub</c> nor <c>client_id</c> is not asked about
/// at all; a non-success status, a timeout, a transport error and an unparseable body are each logged
/// at Error and tagged by kind; <c>204</c>, a blank body and an empty roles array are logged at
/// Warning — and every one of them resolves to an EMPTY role set. The request continues and the
/// grant engine decides on that set: an allowlist grant cannot match, and a role-bound deny refuses a
/// role-less caller (<c>TransitionAuthorizationManager.IsUnprovableRoleBoundDeny</c>). An outage
/// therefore narrows what a caller sees and never widens it, without turning every read into a 403.
/// </para>
/// <para>
/// The outcome is memoized for the scope. No distributed cache is layered on top: the endpoint caches
/// itself, and a second cache here would only add a second place for a stale operation set to hide.
/// </para>
/// </summary>
public sealed class MorphIdmCallerRoleResolver : ICallerRoleResolver
{
    private readonly HttpClient _httpClient;
    private readonly ICurrentUser _currentUser;
    private readonly MorphIdmOptions _options;
    private readonly ILogger<MorphIdmCallerRoleResolver> _logger;

    /// <summary>
    /// The scope's single in-flight or completed resolution. A <see cref="Lazy{T}"/> over the task
    /// rather than a flag plus a field: several surfaces resolve concurrently on the same scope (the
    /// human-task and subflow reads both fan out), and a flag would let two of them race into two
    /// provider calls.
    /// </summary>
    private readonly Lazy<Task<Resolution>> _resolution;

    /// <summary>
    /// Headers captured from the first caller, used only as a fallback source of <c>position</c> in
    /// scopes with no ambient HTTP request (background transition execution), where nothing has
    /// populated <c>ICurrentUser.Position</c>. The rest of the identity comes from
    /// <c>ICurrentUser</c>, which is scope-wide and identical at every call site.
    /// </summary>
    private IReadOnlyDictionary<string, string?>? _fallbackHeaders;

    public MorphIdmCallerRoleResolver(
        HttpClient httpClient,
        ICurrentUser currentUser,
        IOptions<CallerRoleProviderOptions> options,
        ILogger<MorphIdmCallerRoleResolver> logger)
    {
        _httpClient = httpClient;
        _currentUser = currentUser;
        _options = options.Value.MorphIdm;
        _logger = logger;

        // CancellationToken.None on purpose: the memoized task is shared by every surface in the scope,
        // so honouring the first caller's token would let one abandoned read cancel the role set out
        // from under the others. The HttpClient timeout is what bounds this call.
        _resolution = new Lazy<Task<Resolution>>(
            () => FetchAsync(_fallbackHeaders, CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// No. Under this provider the resolved set is the caller's operation set as morph-idm reports it,
    /// and a <c>204</c> ("no operations") is that service's decision — not a gap for the caller to fill
    /// with a <c>role</c> request parameter. Same rule as never forwarding the <c>role</c> header, on
    /// the other channel.
    /// </summary>
    public bool AllowsRoleParameterFallback => false;

    /// <inheritdoc />
    public async Task<Result<string[]?>> ResolveRolesAsync(
        IReadOnlyDictionary<string, string?>? headers,
        CancellationToken cancellationToken = default)
    {
        if (_resolution.IsValueCreated)
        {
            var memoized = _resolution.Value;
            if (memoized.IsCompletedSuccessfully)
            {
                _logger.CallerRolesServedFromRequestScopeMemo(
                    CallerRoleProviderOptions.MorphIdmProvider, memoized.Result.Roles.Length);
            }

            return Result<string[]?>.Ok((await RecordMemoHitAsync(memoized)).Roles);
        }

        _fallbackHeaders = headers;
        return Result<string[]?>.Ok((await _resolution.Value).Roles);
    }

    /// <summary>
    /// Emits the span for a surface that was served the memo. The span is short by construction —
    /// it measures nothing but the memo read — and that is the point: its presence, and its
    /// <c>memo.hit=true</c> tag, are what make the shared call visible. Without it a request where
    /// six surfaces asked once looks exactly like one where a single surface asked. It repeats the
    /// original outcome's tags, so a memoized failure stays an Error on every surface.
    /// </summary>
    private static async Task<Resolution> RecordMemoHitAsync(Task<Resolution> memoized)
    {
        using var activity = AuthorizationActivityHelper.StartResolveRoles(
            CallerRoleProviderOptions.MorphIdmProvider);

        var resolution = await memoized;

        if (resolution.FailureKind is not null)
            AuthorizationActivityHelper.SetFailedFromMemo(activity, resolution.FailureKind, resolution.StatusCode);
        else if (resolution.Skipped)
            AuthorizationActivityHelper.SetSkipped(activity, memoHit: true);
        else
            AuthorizationActivityHelper.SetResolved(
                activity, resolution.Roles.Length, memoHit: true, resolution.EmptyReason);

        return resolution;
    }

    private async Task<Resolution> FetchAsync(
        IReadOnlyDictionary<string, string?>? headers,
        CancellationToken cancellationToken)
    {
        var subject = _currentUser.UserName;
        var actor = _currentUser.ActorUserName;
        var clientId = HeaderValue(headers, AetherClaimTypes.ClientId);
        // Position now rides on ICurrentUser, populated by the framework's HeaderCurrentUserResolver
        // from the `position` claim header. The forwarded-header fallback stays for scopes with no
        // ambient HTTP request — a background transition job resolving roles carries the caller's
        // headers as a dictionary, and nothing has populated ICurrentUser there.
        var position = _currentUser.Position ?? HeaderValue(headers, AetherClaimTypes.Position);

        using var activity = AuthorizationActivityHelper.StartResolveRoles(
            CallerRoleProviderOptions.MorphIdmProvider);
        AuthorizationActivityHelper.SetCaller(activity, subject, actor, position);

        // Nobody to ask about: an anonymous or device token carries neither the acting user nor the
        // client. Calling would only spend a round trip on an answer that cannot be about anyone.
        if (string.IsNullOrWhiteSpace(actor) && string.IsNullOrWhiteSpace(clientId))
        {
            _logger.CallerRoleProviderCallSkippedNoIdentity(CallerRoleProviderOptions.MorphIdmProvider, subject);
            AuthorizationActivityHelper.SetSkipped(activity, memoHit: false);
            return Resolution.Skip;
        }

        var stopwatch = Stopwatch.GetTimestamp();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _options.GetRolesPath);
            AddHeader(request, AetherClaimTypes.ActorSub, actor);
            AddHeader(request, AetherClaimTypes.UserName, subject);
            AddHeader(request, AetherClaimTypes.Position, position);
            AddHeader(request, AetherClaimTypes.ClientId, clientId);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var statusCode = (int)response.StatusCode;

            // A known "this caller has no operation set" answer. Empty, and never a fall-through to
            // any other source: that would silently re-grant what the provider just declined to grant.
            if (response.StatusCode == HttpStatusCode.NoContent)
                return Empty(activity, TelemetryConstants.AuthEmptyReasons.NoContent, subject, actor, position);

            if (!response.IsSuccessStatusCode)
            {
                var reason = response.ReasonPhrase ?? "non-success status";
                return Failed(activity, null, TelemetryConstants.AuthFailureKinds.HttpStatus, statusCode, reason);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
                return Empty(activity, TelemetryConstants.AuthEmptyReasons.EmptyBody, subject, actor, position);

            var roles = ParseRoles(body);
            if (roles is null)
            {
                const string reason = "response carried no recognizable roles array";
                _logger.CallerRoleProviderResponseUnparseable(
                    CallerRoleProviderOptions.MorphIdmProvider, statusCode, reason);
                AuthorizationActivityHelper.SetFailed(activity, reason, TelemetryConstants.AuthFailureKinds.Parse, statusCode);
                return Resolution.Failure(TelemetryConstants.AuthFailureKinds.Parse, statusCode);
            }

            if (roles.Length == 0)
                return Empty(activity, TelemetryConstants.AuthEmptyReasons.EmptyArray, subject, actor, position);

            _logger.CallerRolesResolvedFromProvider(
                CallerRoleProviderOptions.MorphIdmProvider, roles.Length, Stopwatch.GetElapsedTime(stopwatch).TotalMilliseconds);
            AuthorizationActivityHelper.SetResolved(activity, roles.Length, memoHit: false);
            return Resolution.Of(roles);
        }
        catch (TaskCanceledException ex)
        {
            // CancellationToken.None is passed in, so a cancellation here is the HttpClient timeout.
            return Failed(activity, ex, TelemetryConstants.AuthFailureKinds.Timeout, null, ex.Message);
        }
        catch (Exception ex)
        {
            return Failed(activity, ex, TelemetryConstants.AuthFailureKinds.Transport, null, ex.Message);
        }
    }

    private Resolution Empty(Activity? activity, string emptyReason, string? subject, string? actor, string? position)
    {
        _logger.CallerRoleProviderReturnedNoContent(
            CallerRoleProviderOptions.MorphIdmProvider, emptyReason, subject, actor, position);
        AuthorizationActivityHelper.SetResolved(activity, 0, memoHit: false, emptyReason);
        return Resolution.EmptyAnswer(emptyReason);
    }

    private Resolution Failed(Activity? activity, Exception? exception, string failureKind, int? statusCode, string reason)
    {
        _logger.CallerRoleProviderCallFailed(
            exception, CallerRoleProviderOptions.MorphIdmProvider, failureKind, statusCode, reason);
        AuthorizationActivityHelper.SetFailed(activity, reason, failureKind, statusCode);
        return Resolution.Failure(failureKind, statusCode);
    }

    /// <summary>
    /// The memoized outcome of the scope's one resolution. The roles are what every surface receives;
    /// the rest exists only so a memo-hit span can repeat the original outcome's tags.
    /// </summary>
    private sealed record Resolution(
        string[] Roles,
        bool Skipped = false,
        string? EmptyReason = null,
        string? FailureKind = null,
        int? StatusCode = null)
    {
        public static readonly Resolution Skip = new([], Skipped: true);
        public static Resolution Of(string[] roles) => new(roles);
        public static Resolution EmptyAnswer(string reason) => new([], EmptyReason: reason);
        public static Resolution Failure(string kind, int? statusCode) => new([], FailureKind: kind, StatusCode: statusCode);
    }

    /// <summary>
    /// Reads the roles array from any of the shapes the endpoint is known to answer with:
    /// <c>roles</c>, <c>data.roles</c>, or <c>getRoles.data.roles</c>. Returns null when none is present,
    /// which is logged as a parse failure rather than as an empty answer — an unparseable body is a
    /// provider defect worth an Error, while <c>204</c> and an empty array are a caller holding nothing.
    /// Both resolve to an empty role set.
    /// </summary>
    private static string[]? ParseRoles(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            return ReadArray(root, "roles")
                   ?? ReadArray(Child(root, "data"), "roles")
                   ?? ReadArray(Child(Child(root, "getRoles"), "data"), "roles");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? Child(JsonElement? element, string property) =>
        element is { ValueKind: JsonValueKind.Object } parent
        && parent.TryGetProperty(property, out var child)
            ? child
            : null;

    private static string[]? ReadArray(JsonElement? element, string property)
    {
        if (element is not { ValueKind: JsonValueKind.Object } parent)
            return null;
        if (!parent.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
            return null;

        return array.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.Trim())
            .ToArray();
    }

    private static void AddHeader(HttpRequestMessage request, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            request.Headers.TryAddWithoutValidation(name, value);
    }

    private static string? HeaderValue(IReadOnlyDictionary<string, string?>? headers, string key) =>
        headers is not null && headers.TryGetValue(key, out var value) ? value : null;
}
