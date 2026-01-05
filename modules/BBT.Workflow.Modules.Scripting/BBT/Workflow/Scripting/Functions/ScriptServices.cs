using System;
using Dapr.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Scripting.Functions;

/// <summary>
/// Default implementation of script services that provides runtime dependencies
/// to dynamically compiled scripts. Registered as scoped to ensure each request
/// gets its own instance with proper scope isolation.
/// </summary>
/// <param name="daprClient">The Dapr client for distributed application runtime operations</param>
/// <param name="logger">The logger for script execution logging</param>
/// <param name="configuration">The configuration for accessing application settings</param>
public sealed class ScriptServices(
    DaprClient daprClient,
    ILogger<ScriptServices> logger,
    IConfiguration configuration) : IScriptServices
{
    /// <inheritdoc />
    public DaprClient DaprClient { get; } = daprClient ?? throw new ArgumentNullException(nameof(daprClient));
    
    /// <inheritdoc />
    public ILogger Logger { get; } = logger ?? throw new ArgumentNullException(nameof(logger));
    
    /// <inheritdoc />
    public IConfiguration Configuration { get; } = configuration ?? throw new ArgumentNullException(nameof(configuration));
}

