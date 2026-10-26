using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using BBT.Aether.Results;
using BBT.Aether.Users;
using BBT.Workflow.Authorization.Configuration;
using BBT.Workflow.CurrentUser;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BBT.Workflow.Authorization;

/// <summary>
/// Resolves the caller's operation set — from the request's <c>role</c> header when it carries one,
/// otherwise from morph-idm.
/// <para>
/// <b>A <c>role</c> header takes precedence</b> (2026-09-25 committee decision). When the request
/// carries one — <c>ICurrentUser.Roles</c>, which the framework parses from it, else the forwarded
/// header dictionary in a scope with no HTTP request — those roles are the caller's set and morph-idm
/// is not called. There is no merge: the header replaces the service's answer. A blank header counts
/// as none.
/// </para>
/// <para>
/// Without a header: one GET to the <c>get-roles</c> function per DI scope, carrying the caller's <c>act_sub</c>,
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
/// grant engine decides on that set: an allowlist grant cannot match, and a role-bound leaf is Unknown
/// for a role-less caller (a deny refuses unless an identity leaf rules it out). An outage
/// therefore narrows what a caller sees and never widens it, without turning every read into a 403.
/// </para>
/// <para>
/// The outcome is memoized for the scope <b>per credential</b> — the acting user, subject,
/// <c>client_id</c>, <c>position</c> and <c>role</c> header the resolution reads. A same-domain trigger
/// task that reads under a different credential in the same DI scope (<c>ICurrentUser.Change</c>) gets
/// its own resolution instead of the pipeline caller's. No distributed cache is layered on top: the
/// endpoint caches itself, and a second cache here would only add a second place for a stale operation
/// set to hide.
/// </para>
/// <para>
/// <see cref="ResolveRoleSetAsync"/> reads the same memo and reports how the set came about
/// (<see cref="CallerRoleOutcome"/>), so row security can tell a failed resolution from a caller who
/// holds nothing; <see cref="ResolveRolesAsync"/> is unchanged and still never fails.
/// </para>
/// </summary>
public sealed class MorphIdmCallerRoleResolver : ICallerRoleResolver
{
    private readonly HttpClient _httpClient;
    private readonly ICurrentUser _currentUser;
    private readonly MorphIdmOptions _options;
    private readonly ILogger<MorphIdmCallerRoleResolver> _logger;

    /// <summary>
    /// The scope's in-flight or completed resolutions, one per credential. A <see cref="Lazy{T}"/>
    /// over the task rather than a flag plus a field: several surfaces resolve concurrently on the
    /// same scope (the human-task and subflow reads both fan out), and a flag would let two of them
    /// race into two provider calls for the same credential.
    /// </summary>
    private readonly ConcurrentDictionary<CredentialKey, Lazy<Task<Resolution>>> _resolutions = new();

    /// <summary>
    /// The first non-null header dictionary a caller passed. A surface that passes no headers (several
    /// read paths pass <c>null</c>) is resolved against it, so it lands on the same credential as the
    /// surfaces that did pass them — the <c>client_id</c>, <c>position</c> fallback and <c>role</c>
    /// header otherwise read differently between surfaces of one request. A surface that passes its
    /// own headers (a trigger task's credential) is always keyed on those.
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
    }

    /// <summary>
    /// <see cref="RoleParameterMode.AsRoleHeader"/>: <c>authorize</c>'s <c>role</c> parameter is
    /// handed to this resolver as the request's <c>role</c> header when the request has none, so it is
    /// answered by the same header precedence — it is the role set and morph-idm is not asked.
    /// </summary>
    public RoleParameterMode RoleParameterMode => RoleParameterMode.AsRoleHeader;

    /// <inheritdoc />
    public async Task<Result<string[]?>> ResolveRolesAsync(
        IReadOnlyDictionary<string, string?>? headers,
        CancellationToken cancellationToken = default) =>
        Result<string[]?>.Ok((await ResolveAsync(headers)).Roles);

    /// <summary>
    /// The same memoized resolution as <see cref="ResolveRolesAsync"/>, with how it came about: a
    /// <c>role</c> header is <see cref="CallerRoleOutcome.FromHeader"/>, a caller with no identity to
    /// ask about is <see cref="CallerRoleOutcome.Skipped"/>, <c>204</c> / a blank body / an empty array
    /// are <see cref="CallerRoleOutcome.Empty"/>, and a non-success status, timeout, transport error or
    /// unparseable body is <see cref="CallerRoleOutcome.Failed"/> with its coarse kind (never exception
    /// text). Never throws for a resolution failure.
    /// </summary>
    /// <param name="headers">Request headers; may be null.</param>
    /// <param name="cancellationToken">Not honoured by the shared memoized call (see the class remarks).</param>
    public async Task<CallerRoleResolution> ResolveRoleSetAsync(
        IReadOnlyDictionary<string, string?>? headers,
        CancellationToken cancellationToken = default) =>
        ToOutcome(await ResolveAsync(headers));

    private static CallerRoleResolution ToOutcome(Resolution resolution)
    {
        if (resolution.FromHeader)
            return new CallerRoleResolution(resolution.Roles, CallerRoleOutcome.FromHeader);
        if (resolution.Skipped)
            return new CallerRoleResolution([], CallerRoleOutcome.Skipped);
        if (resolution.FailureKind is not null)
            return new CallerRoleResolution([], CallerRoleOutcome.Failed, resolution.FailureKind);
        if (resolution.EmptyReason is not null || resolution.Roles.Length == 0)
            return new CallerRoleResolution([], CallerRoleOutcome.Empty);
        return new CallerRoleResolution(resolution.Roles, CallerRoleOutcome.Resolved);
    }

    /// <summary>
    /// Serves the credential's memoized resolution, starting it on the first ask. The memo-hit log and
    /// span are emitted per key exactly as before the memo was keyed.
    /// </summary>
    private async Task<Resolution> ResolveAsync(IReadOnlyDictionary<string, string?>? headers)
    {
        if (headers is not null)
            Interlocked.CompareExchange(ref _fallbackHeaders, headers, null);

        var effectiveHeaders = headers ?? _fallbackHeaders;
        var key = BuildKey(effectiveHeaders);

        if (_resolutions.TryGetValue(key, out var existing))
            return await ServeMemoAsync(existing);

        // CancellationToken.None on purpose: the memoized task is shared by every surface asking for
        // this credential, so honouring the first caller's token would let one abandoned read cancel
        // the role set out from under the others. The HttpClient timeout is what bounds this call.
        var created = new Lazy<Task<Resolution>>(
            () => FetchAsync(key, effectiveHeaders, CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var winner = _resolutions.GetOrAdd(key, created);

        return ReferenceEquals(winner, created)
            ? await winner.Value
            : await ServeMemoAsync(winner);
    }

    private async Task<Resolution> ServeMemoAsync(Lazy<Task<Resolution>> entry)
    {
        var memoized = entry.Value;
        if (memoized.IsCompletedSuccessfully)
        {
            _logger.CallerRolesServedFromRequestScopeMemo(
                CallerRoleProviderOptions.MorphIdmProvider, memoized.Result.Roles.Length);
        }

        return await RecordMemoHitAsync(memoized);
    }

    /// <summary>
    /// The credential a resolution is about — exactly the fields <see cref="FetchAsync"/> reads, so two
    /// asks share a memo entry only when they would have sent the same request (or been answered by
    /// the same <c>role</c> header).
    /// </summary>
    private CredentialKey BuildKey(IReadOnlyDictionary<string, string?>? headers) =>
        new(
            Actor: _currentUser.ActorUserName,
            Subject: _currentUser.UserName,
            ClientId: HeaderValue(headers, AetherClaimTypes.ClientId),
            Position: _currentUser.Position ?? HeaderValue(headers, AetherClaimTypes.Position),
            RoleHeader: RolesFromRequestHeader(headers) is { Length: > 0 } roles ? string.Join(",", roles) : null);

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

        if (resolution.FromHeader)
            AuthorizationActivityHelper.SetFromHeader(activity, resolution.Roles.Length, memoHit: true);
        else if (resolution.FailureKind is not null)
            AuthorizationActivityHelper.SetFailedFromMemo(activity, resolution.FailureKind, resolution.StatusCode);
        else if (resolution.Skipped)
            AuthorizationActivityHelper.SetSkipped(activity, memoHit: true);
        else
            AuthorizationActivityHelper.SetResolved(
                activity, resolution.Roles.Length, memoHit: true, resolution.EmptyReason);

        return resolution;
    }

    private async Task<Resolution> FetchAsync(
        CredentialKey credential,
        IReadOnlyDictionary<string, string?>? headers,
        CancellationToken cancellationToken)
    {
        var subject = credential.Subject;
        var actor = credential.Actor;
        var clientId = credential.ClientId;
        // Position now rides on ICurrentUser, populated by the framework's HeaderCurrentUserResolver
        // from the `position` claim header. The forwarded-header fallback stays for scopes with no
        // ambient HTTP request — a background transition job resolving roles carries the caller's
        // headers as a dictionary, and nothing has populated ICurrentUser there (see BuildKey).
        var position = credential.Position;

        using var activity = AuthorizationActivityHelper.StartResolveRoles(
            CallerRoleProviderOptions.MorphIdmProvider);
        AuthorizationActivityHelper.SetCaller(activity, subject, actor, position);

        // A request `role` header takes precedence: its roles are the caller's set and the identity
        // service is not asked (2026-09-25 committee decision). Read exactly as the default provider
        // reads it — ICurrentUser.Roles, which the framework parses from the header, else the
        // forwarded dictionary in a scope with no HTTP request. Blank entries do not count as a header.
        var headerRoles = RolesFromRequestHeader(headers);
        if (headerRoles.Length > 0)
        {
            _logger.CallerRolesTakenFromRequestHeader(CallerRoleProviderOptions.MorphIdmProvider, headerRoles.Length);
            AuthorizationActivityHelper.SetFromHeader(activity, headerRoles.Length, memoHit: false);
            return Resolution.Header(headerRoles);
        }

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

    private string[] RolesFromRequestHeader(IReadOnlyDictionary<string, string?>? headers) =>
        _currentUser.ResolveCallerRoles(headers) is { Length: > 0 } roles
            ? roles.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).ToArray()
            : [];

    /// <summary>
    /// The memoized outcome of one credential's resolution. The roles are what every surface receives;
    /// the rest lets a memo-hit span repeat the original outcome's tags and lets
    /// <see cref="ResolveRoleSetAsync"/> report the outcome.
    /// </summary>
    private sealed record Resolution(
        string[] Roles,
        bool Skipped = false,
        string? EmptyReason = null,
        string? FailureKind = null,
        int? StatusCode = null,
        bool FromHeader = false)
    {
        public static Resolution Header(string[] roles) => new(roles, FromHeader: true);
        public static readonly Resolution Skip = new([], Skipped: true);
        public static Resolution Of(string[] roles) => new(roles);
        public static Resolution EmptyAnswer(string reason) => new([], EmptyReason: reason);
        public static Resolution Failure(string kind, int? statusCode) => new([], FailureKind: kind, StatusCode: statusCode);
    }

    /// <summary>
    /// The memo key: the identity fields a resolution reads. Held only in memory for the scope; never
    /// logged or tagged (the resolver's existing caller tags are unchanged).
    /// </summary>
    private readonly record struct CredentialKey(
        string? Actor,
        string? Subject,
        string? ClientId,
        string? Position,
        string? RoleHeader);

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
