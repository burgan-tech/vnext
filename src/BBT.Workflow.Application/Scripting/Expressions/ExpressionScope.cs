using System.Text.Json;
using BBT.Aether.Results;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting.Caller;
using BBT.Workflow.Scripting.Rules;
using DynamicExpresso;
using Microsoft.Extensions.Configuration;

namespace BBT.Workflow.Scripting.Expressions;

/// <summary>Which expression family a <see cref="ExpressionScope"/> binds (see <see cref="ExpressionSignatures.Mapping"/>).</summary>
public enum ScopeKind
{
    /// <summary>A condition rule.</summary>
    Condition,

    /// <summary>A task input mapping (before the task runs).</summary>
    MappingInput,

    /// <summary>A task output mapping (after the task ran); adds <c>result</c>.</summary>
    MappingOutput,

    /// <summary>A transition's declarative output block: the condition variables, no <c>task</c> / <c>config</c> / <c>result</c>.</summary>
    TransitionOutput,

    /// <summary>A SubFlow's declarative input block (the child start request): the condition variables.</summary>
    SubFlowInput,

    /// <summary>A SubFlow's declarative output block (child completion): the condition variables plus <c>result</c>.</summary>
    SubFlowOutput
}

/// <summary>
/// The variables one declarative expression evaluation (or several entries of one mapping) sees, built
/// lazily: a variable is materialized only when an expression asks for it, at most once per scope, and
/// nothing is cloned. Lifecycle: <see cref="Create(ScriptContext, TimeProvider, ICallerAccessor, ScopeKind, JsonElement?)"/> per mapping/condition evaluation, then for each
/// compiled expression <see cref="PrepareAsync"/> (async: resolves the caller's roles when <c>user</c> is
/// used) followed by the synchronous <see cref="Bind(IReadOnlyList{Parameter}, IReadOnlySet{string})"/>.
/// Not thread-safe; never shared across evaluations.
/// </summary>
/// <remarks>
/// Variables: <c>data</c> the instance's latest data, <c>input</c> the raw request payload of the hop
/// (<see cref="ScriptContext.HopInput"/>; never the phase-dependent <see cref="ScriptContext.Body"/>), <c>instance</c> the read-only instance view,
/// <c>now</c> one clock reading per scope, <c>user</c> the caller (fail-closed roles), <c>context</c> the
/// legacy <see cref="ExpressoRuleContext"/>, <c>headers</c> the incoming request's headers (<see cref="ExpressionHeaders"/>, a
/// view over <see cref="ScriptContext.Headers"/>: the <c>Workflow:HeaderExposure:Deny</c> names are absent), and in the output phase <c>result</c> the task's standard
/// response (<c>body</c>, <c>headers</c>, <c>statusCode</c>). In both mapping phases <c>task</c> is the task's
/// <c>config</c> JSON and <c>config</c> the host configuration (<see cref="ExpressionConfig"/>), both given by
/// <see cref="WithTask"/>. <c>data</c>, <c>instance.Data</c>, a
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

    /// <summary>Name of the hop-request-payload variable (<see cref="ScriptContext.HopInput"/>).</summary>
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

    /// <summary>Name of the task-configuration variable (mapping phases only).</summary>
    public const string TaskVariable = "task";

    /// <summary>Name of the host-configuration variable (mapping phases only).</summary>
    public const string ConfigVariable = "config";

    /// <summary>
    /// Name of the incoming-request-headers variable (<see cref="ExpressionHeaders"/>, every kind): a read-only view over
    /// <see cref="ScriptContext.Headers"/>, so <c>Workflow:HeaderExposure:Deny</c> applies to it exactly as to
    /// <c>context.Headers</c>.
    /// </summary>
    public const string HeadersVariable = "headers";

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
    private ExpressionInstance? _instance;
    private DateTimeOffset? _now;
    private ExpressoRuleContext? _ruleContext;
    private bool _resultBuilt;
    private RuleJsonDynamic? _resultView;
    private ExpressionUser? _user;
    private JsonElement? _taskConfig;
    private bool _taskBuilt;
    private RuleJsonDynamic? _task;
    private IConfiguration? _configuration;
    private ExpressionConfig? _config;
    private ExpressionHeaders? _headers;
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
    /// <param name="kind">Expression family; <c>result</c> exists only for <see cref="ScopeKind.MappingOutput"/> and <see cref="ScopeKind.SubFlowOutput"/>.</param>
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
    /// Creates a scope whose <c>result</c> is the given view (output phase) instead of a view over a
    /// <see cref="JsonElement"/>; <paramref name="result"/> is borrowed the same way, so whatever backs it must
    /// outlive the scope.
    /// </summary>
    /// <param name="context">The execution context the variables are read from.</param>
    /// <param name="clock">Source of <c>now</c> (read once per scope).</param>
    /// <param name="caller">Caller whose roles back <c>user</c>; <see cref="NullCallerAccessor"/> on System hops.</param>
    /// <param name="kind">Expression family; <c>result</c> exists only for <see cref="ScopeKind.MappingOutput"/> and <see cref="ScopeKind.SubFlowOutput"/>.</param>
    /// <param name="result">The <c>result</c> variable (a borrowed view), or null.</param>
    public static ExpressionScope Create(
        ScriptContext context, TimeProvider clock, ICallerAccessor caller, ScopeKind kind, RuleJsonDynamic? result)
    {
        var scope = Create(context, clock, caller, kind);
        scope._resultBuilt = true;
        scope._resultView = result;
        return scope;
    }

    /// <summary>
    /// Gives a mapping scope its <c>task</c> variable (the task's <c>config</c> as it stands when the expression runs,
    /// borrowed) and its <c>config</c> variable (<paramref name="configuration"/>; an empty configuration when null).
    /// Returns this scope.
    /// </summary>
    public ExpressionScope WithTask(JsonElement taskConfig, IConfiguration? configuration)
    {
        _taskConfig = taskConfig;
        _taskBuilt = false;
        _task = null;
        _configuration = configuration;
        return this;
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

    private static readonly object BoxedDefaultDateTimeOffset = DateTimeOffset.MinValue;

    private static object? DefaultOf(Type type)
    {
        if (type == typeof(DateTimeOffset))
            return BoxedDefaultDateTimeOffset;
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }

    private object? Materialize(string name) => name switch
    {
        DataVariable => Data(),
        InputVariable => Input(),
        InstanceVariable => InstanceView(),
        NowVariable => _now ??= _clock.GetUtcNow(),
        UserVariable => _user ??= new ExpressionUser(this),
        ContextVariable => _ruleContext ??= ExpressoRuleContextMapper.FromScriptContext(_context),
        ResultVariable when _kind is ScopeKind.MappingOutput or ScopeKind.SubFlowOutput => ResultView(),
        TaskVariable when IsTaskKind => TaskView(),
        ConfigVariable when IsTaskKind => _config ??= new ExpressionConfig(_configuration),
        // The script view (deny list applied), never TransportHeaders; wrapped, not copied.
        HeadersVariable => _headers ??= new ExpressionHeaders((object?)_context.Headers),
        _ => throw new ArgumentException($"'{name}' is not a variable of a {_kind} expression scope.", nameof(name))
    };

    /// <summary>The two task-mapping phases: they alone have <c>task</c> / <c>config</c> and read the transition record's body as <c>input</c>.</summary>
    private bool IsTaskKind => _kind is ScopeKind.MappingInput or ScopeKind.MappingOutput;

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
        var body = ResolveInput();
        _input = body switch
        {
            null => null,
            JsonElement element => RuleJsonDynamic.Borrow(element),
            _ => RuleJsonDynamic.FromObject(body)
        };
        return _input;
    }

    /// <summary>
    /// <c>input</c> is the raw request payload that triggered the hop (<see cref="ScriptContext.HopInput"/>), the same
    /// value in every step. Task mappings keep the transition record's body (<see cref="ScriptContext.CurrentTransition"/>)
    /// whenever the task step attached one, exactly as before — it is the hop's request body except where a legacy
    /// transition <c>mapping</c> script rewrote the record or a retry reused the original record. A context built
    /// outside any hop (no hop input bound) falls back to that record body as well.
    /// </summary>
    private object? ResolveInput()
    {
        if (IsTaskKind && _context.CurrentTransition is { } currentTransition)
            return currentTransition.Data;

        return _context.HasHopInput ? _context.HopInput : _context.CurrentTransition?.Data;
    }

    private ExpressionInstance? InstanceView()
    {
        if (_instanceBuilt)
            return _instance;

        _instanceBuilt = true;
        _instance = _context.Instance is { } instance ? new ExpressionInstance(instance) : null;
        return _instance;
    }

    private RuleJsonDynamic? TaskView()
    {
        if (_taskBuilt)
            return _task;

        _taskBuilt = true;
        _task = _taskConfig is { ValueKind: JsonValueKind.Object } element ? RuleJsonDynamic.Borrow(element) : null;
        return _task;
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
    // An array on purpose: expressions use user.roles.Length and indexers, and the clone keeps the resolved set immutable.
    public string[] roles => (string[])_scope.UserRoles().Clone(); // skipcq: CS-P1026

    /// <summary>True when the caller holds <paramref name="role"/> (ordinal).</summary>
    /// <exception cref="CallerRolesUnavailableException">No caller (<see cref="available"/> is false).</exception>
    public bool hasRole(string role) => Array.IndexOf(_scope.UserRoles(), role) >= 0;
}
#pragma warning restore IDE1006
