using Dapr.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Scripting.Functions;

/// <summary>
/// Provides runtime services to dynamically compiled scripts.
/// Injected after script instantiation to enable DI-compatible scripting.
/// This interface replaces the static ScriptHelper approach with proper dependency injection.
/// </summary>
public interface IScriptServices
{
    /// <summary>
    /// Dapr client for distributed application runtime operations.
    /// Used for secret management, state storage, pub/sub, and service invocation.
    /// </summary>
    DaprClient DaprClient { get; }
    
    /// <summary>
    /// Logger for script execution logging.
    /// Provides structured logging with script context information.
    /// </summary>
    ILogger Logger { get; }
    
    /// <summary>
    /// Configuration for accessing application settings.
    /// Provides access to configuration values and connection strings.
    /// </summary>
    IConfiguration Configuration { get; }
}

