namespace BBT.Workflow.Definitions;

/// <summary>
/// The application's own client-facing route shape, as built-in defaults for
/// <see cref="UrlTemplateOptions"/>.
/// <para>
/// Each template here is the path <b>below</b> the base prefix — it mirrors the controller routes
/// exactly (<c>[Route("api/v{version:apiVersion}")]</c> plus <c>{domain}/workflows/…</c>), minus the
/// prefix. The effective template is <see cref="BasePath"/> (or the operator's override of it) plus
/// one of these. They are consts rather than property initializers so that
/// <see cref="UrlTemplateOptions"/>' own properties can stay null-by-default and thereby carry the
/// "did the operator override this?" answer.
/// </para>
/// <para>
/// Placeholders are positional <see cref="string.Format(string, object?[])"/> arguments; the meaning
/// of each index is documented on the matching <see cref="UrlTemplateOptions"/> property.
/// </para>
/// </summary>
public static class UrlTemplateDefaults
{
    /// <summary>
    /// The prefix the application serves its own endpoints under. Every template below hangs off it
    /// unless the operator declares a base path of their own (typically an API gateway route).
    /// </summary>
    public const string BasePath = "/api/v1";

    /// <summary>Instance start endpoint (POST).</summary>
    public const string Start = "/{0}/workflows/{1}/instances/start";

    /// <summary>Instance transition endpoint (PATCH).</summary>
    public const string Transition = "/{0}/workflows/{1}/instances/{2}/transitions/{3}";

    /// <summary>Workflow-scoped function list endpoint (GET).</summary>
    public const string FunctionList = "/{0}/workflows/{1}/functions/{2}";

    /// <summary>Instance list endpoint (GET).</summary>
    public const string InstanceList = "/{0}/workflows/{1}/instances";

    /// <summary>Single instance endpoint (GET).</summary>
    public const string Instance = "/{0}/workflows/{1}/instances/{2}";

    /// <summary>Instance history/transitions endpoint (GET).</summary>
    public const string InstanceHistory = "/{0}/workflows/{1}/instances/{2}/transitions";

    /// <summary>Instance data endpoint (GET).</summary>
    public const string Data = "/{0}/workflows/{1}/instances/{2}/functions/data";

    /// <summary>Instance view endpoint (GET).</summary>
    public const string View = "/{0}/workflows/{1}/instances/{2}/functions/view";

    /// <summary>Instance transition schema endpoint (GET).</summary>
    public const string Schema = "/{0}/workflows/{1}/instances/{2}/functions/schema?transitionKey={3}";

    /// <summary>Instance master schema endpoint (GET).</summary>
    public const string Master = "/{0}/workflows/{1}/instances/{2}/functions/master";

    /// <summary>Domain-scoped function execution endpoint.</summary>
    public const string DomainFunction = "/{0}/functions/{1}";

    /// <summary>Domain-scoped function info endpoint (GET).</summary>
    public const string DomainFunctionInfo = "/{0}/functions/{1}/info";

    /// <summary>Domain-scoped function view endpoint (GET).</summary>
    public const string DomainFunctionView = "/{0}/functions/{1}/view?target={2}";

    /// <summary>Domain-scoped function schema endpoint (GET).</summary>
    public const string DomainFunctionSchema = "/{0}/functions/{1}/schema?target={2}";

    /// <summary>Instance-scoped function execution endpoint.</summary>
    public const string InstanceFunction = "/{0}/workflows/{1}/instances/{2}/functions/{3}";

    /// <summary>Instance-scoped function info endpoint (GET).</summary>
    public const string InstanceFunctionInfo = "/{0}/workflows/{1}/instances/{2}/functions/{3}/info";

    /// <summary>Instance function catalog endpoint (GET).</summary>
    public const string FunctionCatalog = "/{0}/workflows/{1}/instances/{2}/functions/catalog";

    /// <summary>Instance-scoped function view endpoint (GET).</summary>
    public const string InstanceFunctionView = "/{0}/workflows/{1}/instances/{2}/functions/{3}/view?target={4}";

    /// <summary>Instance-scoped function schema endpoint (GET).</summary>
    public const string InstanceFunctionSchema = "/{0}/workflows/{1}/instances/{2}/functions/{3}/schema?target={4}";
}
