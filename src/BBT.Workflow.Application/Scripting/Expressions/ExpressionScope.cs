using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting.Caller;
using BBT.Workflow.Scripting.Rules;
using DynamicExpresso;

namespace BBT.Workflow.Scripting.Expressions;

/// <summary>Which expression family a <see cref="ExpressionScope"/> binds (see <see cref="ExpressionSignatures.Mapping"/>).</summary>
public enum ScopeKind
{
    /// <summary>A condition rule.</summary>
    Condition,

    /// <summary>A task input mapping (before the task runs).</summary>
    MappingInput,

    /// <summary>A task output mapping (after the task ran); adds <c>result</c>.</summary>
    MappingOutput
}

/// <summary>
/// The variables one declarative expression evaluation (or several entries of one mapping) sees, built
/// lazily: a variable is materialized only when an expression asks for it, at most once per scope, and
/// nothing is cloned. Lifecycle: <see cref="Create"/> per mapping/condition evaluation, then for each
/// compiled expression <see cref="PrepareAsync"/> (async: resolves the caller's roles when <c>user</c> is
/// used) followed by the synchronous <see cref="Bind(IReadOnlyList{Parameter}, IReadOnlySet{string})"/>.
/// Not thread-safe; never shared across evaluations.
/// </summary>
/// <remarks>
/// Variables: <c>data</c> the instance's latest data, <c>input</c> the transition request body (never the
/// phase-dependent <see cref="ScriptContext.Body"/>), <c>instance</c> the read-only instance view,
/// <c>now</c> one clock reading per scope, <c>user</c> the caller (fail-closed roles), <c>context</c> the
/// legacy <see cref="ExpressoRuleContext"/>, and in the output phase <c>result</c> the task's standard
/// response (<c>body</c>, <c>headers</c>, <c>statusCode</c>). <c>data</c>, <c>instance.Data</c>, a
/// <see cref="JsonElement"/> <c>input</c> and <c>result</c> are borrowed (<see cref="RuleJsonDynamic.Borrow"/>), never
/// cloned. The binding constraint is the documents they borrow from: the caller's result document (and any
/// <see cref="JsonElement"/> transition body) must stay undisposed until every value an expression returned has been
/// copied out (the instance-data element is backed by a non-disposable document). A value an expression returns may
/// itself be a borrowed view, so a caller materializes it into an owned copy before the result document goes away.
/// </remarks>
public sealed class ExpressionScope
{
    /// <summary>Name of the instance-data variable.</summary>
    public const string DataVariable = "data";

    /// <summary>Name of the transition-request-body variable.</summary>
    public const string InputVariable = "input";

    /// <summary>Name of the instance-view variable.</summary>
    public const string InstanceVariable = "instance";

    /// <summary>Name of the clock variable.</summary>
    public const string NowVariable = "now";

    /// <summary>Name of the caller variable.</summary>
    public const string UserVariable = "user";

    /// <summary>Name of the legacy rule-context variable.</summary>
    public const string ContextVariable = "context";

    /// <summary>Name of the task-response variable (output phase only).</summary>
    public const string ResultVariable = "result";

    private readonly ScriptContext _context;
    private readonly TimeProvider _clock;
    private readonly ICallerAccessor _caller;
    private readonly ScopeKind _kind;
    private readonly JsonElement? _result;

    private bool _dataBuilt;
    private RuleJsonDynamic? _data;
    private bool _inputBuilt;
    private RuleJsonDynamic? _input;
    private bool _instanceBuilt;
    private ExpressoInstanceView? _instance;
    private DateTimeOffset? _now;
    private ExpressoRuleContext? _ruleContext;
    private bool _resultBuilt;
    private RuleJsonDynamic? _resultView;
    private ExpressionUser? _user;
    private bool _userPrepared;
    private string[]? _roles;

    private ExpressionScope(ScriptContext context, TimeProvider clock, ICallerAccessor caller, ScopeKind kind, JsonElement? result)
    {
        _context = context;
        _clock = clock;
        _caller = caller;
        _kind = kind;
        _result = result;
    }

    /// <summary>
    /// Creates a scope over <paramref name="context"/>. Nothing is read until an expression asks for it.
    /// </summary>
    /// <param name="context">The execution context the variables are read from.</param>
    /// <param name="clock">Source of <c>now</c> (read once per scope).</param>
    /// <param name="caller">Caller whose roles back <c>user</c>; <see cref="NullCallerAccessor"/> on System hops.</param>
    /// <param name="kind">Expression family; <c>result</c> exists only for <see cref="ScopeKind.MappingOutput"/>.</param>
    /// <param name="result">The task's standard response (output phase); borrowed, so its document must outlive the scope.</param>
    public static ExpressionScope Create(
        ScriptContext context, TimeProvider clock, ICallerAccessor caller, ScopeKind kind, JsonElement? result = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(caller);
        return new ExpressionScope(context, clock, caller, kind, result);
    }

    /// <summary>
    /// Runs the asynchronous part of binding before a synchronous invoke: when <paramref name="usedIdentifiers"/>
    /// contains <c>user</c> and the caller is available, resolves the caller's roles (once per scope).
    /// Fails with <see cref="WorkflowErrors.CallerRolesUnavailable"/> when the provider could not answer — never
    /// an empty set. An unavailable caller (System hop) is not a failure here: <c>user.available</c> is false and
    /// reading <c>user.roles</c> / <c>user.hasRole</c> throws <see cref="CallerRolesUnavailableException"/>.
    /// </summary>
    public async Task<Result> PrepareAsync(IReadOnlySet<string> usedIdentifiers)
    {
        ArgumentNullException.ThrowIfNull(usedIdentifiers);
        if (_userPrepared || !usedIdentifiers.Contains(UserVariable))
            return Result.Ok();

        if (!_caller.IsAvailable)
        {
            _userPrepared = true;
            return Result.Ok();
        }

        try
        {
            var roles = await _caller.RolesAsync().ConfigureAwait(false);
            _roles = roles.ToArray();
            _userPrepared = true;
            return Result.Ok();
        }
        catch (CallerRolesUnavailableException)
        {
            // Only the provider's "could not answer" is mapped here; anything else the accessor throws (cancellation,
            // a bug) propagates to the caller, which fails the evaluation. Not marked prepared: a later PrepareAsync
            // asks again (the accessor itself keeps a failure sticky).
            return Result.Fail(WorkflowErrors.CallerRolesUnavailable());
        }
    }

    /// <summary>
    /// Values for <paramref name="parameters"/>, in order, each materialized on first request and reused
    /// afterwards. Synchronous and never blocking: <c>user</c> relies on <see cref="PrepareAsync"/>.
    /// </summary>
    /// <exception cref="ArgumentException">A parameter is not a variable of this scope's kind.</exception>
    public object?[] Bind(IReadOnlyList<Parameter> parameters) => Bind(parameters, usedIdentifiers: null);

    /// <summary>
    /// Values for every one of <paramref name="parameters"/> (the full arity of a compiled delegate), materializing
    /// only those in <paramref name="usedIdentifiers"/>; an unused slot gets its type's default (null, or a
    /// default <see cref="DateTimeOffset"/>). Pass <see cref="CompiledExpression{TDelegate}.UsedIdentifiers"/>.
    /// </summary>
    public object?[] Bind(IReadOnlyList<Parameter> parameters, IReadOnlySet<string>? usedIdentifiers)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var values = new object?[parameters.Count];
        for (var i = 0; i < parameters.Count; i++)
        {
            var parameter = parameters[i];
            values[i] = usedIdentifiers is null || usedIdentifiers.Contains(parameter.Name)
                ? Materialize(parameter.Name)
                : DefaultOf(parameter.Type);
        }

        return values;
    }

    private static readonly object BoxedDefaultDateTimeOffset = default(DateTimeOffset);

    private static object? DefaultOf(Type type) =>
        type == typeof(DateTimeOffset) ? BoxedDefaultDateTimeOffset
        : type.IsValueType ? Activator.CreateInstance(type)
        : null;

    private object? Materialize(string name) => name switch
    {
        DataVariable => Data(),
        InputVariable => Input(),
        InstanceVariable => InstanceView(),
        NowVariable => _now ??= _clock.GetUtcNow(),
        UserVariable => _user ??= new ExpressionUser(this),
        ContextVariable => _ruleContext ??= ExpressoRuleContextMapper.FromScriptContext(_context),
        ResultVariable when _kind == ScopeKind.MappingOutput => ResultView(),
        _ => throw new ArgumentException($"'{name}' is not a variable of a {_kind} expression scope.", nameof(name))
    };

    private RuleJsonDynamic? Data()
    {
        if (_dataBuilt)
            return _data;

        _dataBuilt = true;
        var latest = _context.Instance?.LatestData;
        _data = latest is null ? null : RuleJsonDynamic.Borrow(latest.Data.JsonElement);
        return _data;
    }

    private RuleJsonDynamic? Input()
    {
        if (_inputBuilt)
            return _input;

        _inputBuilt = true;
        object? body = _context.CurrentTransition?.Data;
        _input = body switch
        {
            null => null,
            JsonElement element => RuleJsonDynamic.Borrow(element),
            _ => RuleJsonDynamic.FromObject(body)
        };
        return _input;
    }

    private ExpressoInstanceView? InstanceView()
    {
        if (_instanceBuilt)
            return _instance;

        _instanceBuilt = true;
        _instance = ExpressoRuleContextMapper.MapInstance(_context.Instance, borrowData: true);
        return _instance;
    }

    private RuleJsonDynamic? ResultView()
    {
        if (_resultBuilt)
            return _resultView;

        _resultBuilt = true;
        _resultView = _result is { } element ? RuleJsonDynamic.Borrow(element) : null;
        return _resultView;
    }

    internal bool UserAvailable => _caller.IsAvailable;

    internal string[] UserRoles()
    {
        if (!_caller.IsAvailable)
            throw new CallerRolesUnavailableException(
                "Caller roles are not available here: this expression has no caller (a System hop). Check user.available first.");

        return _roles ?? throw new InvalidOperationException(
            "The caller's roles were not resolved: call ExpressionScope.PrepareAsync with the expression's used identifiers before Bind.");
    }
}

/// <summary>
/// The <c>user</c> variable of an <see cref="ExpressionScope"/>: the caller of the hop. Member names are lowercase
/// because expressions spell them that way (<c>user.available</c>, <c>user.roles</c>, <c>user.hasRole("x")</c>).
/// Fail-closed: <c>roles</c> and <c>hasRole</c> throw <see cref="CallerRolesUnavailableException"/> when there is no
/// caller — never an empty set; <c>available</c> never throws and is the guard.
/// </summary>
#pragma warning disable IDE1006 // lowercase members are the expression-language surface
public sealed class ExpressionUser
{
    private readonly ExpressionScope _scope;

    internal ExpressionUser(ExpressionScope scope) => _scope = scope;

    /// <summary>True when this hop has a caller whose roles can be read.</summary>
    public bool available => _scope.UserAvailable;

    /// <summary>The caller's roles (empty when the caller holds none).</summary>
    /// <exception cref="CallerRolesUnavailableException">No caller (<see cref="available"/> is false).</exception>
    public string[] roles => (string[])_scope.UserRoles().Clone();

    /// <summary>True when the caller holds <paramref name="role"/> (ordinal).</summary>
    /// <exception cref="CallerRolesUnavailableException">No caller (<see cref="available"/> is false).</exception>
    public bool hasRole(string role) => Array.IndexOf(_scope.UserRoles(), role) >= 0;
}
#pragma warning restore IDE1006
