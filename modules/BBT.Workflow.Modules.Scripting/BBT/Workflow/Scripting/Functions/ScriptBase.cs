using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Scripting.Functions;

/// <summary>
/// Base class for dynamically compiled scripts that provides access to runtime services.
/// Scripts inherit from this class and receive dependencies through property injection
/// after instantiation, enabling DI-compatible scripting.
/// </summary>
public abstract class ScriptBase
{
    /// <summary>
    /// Runtime services injected after script instantiation.
    /// Provides access to Dapr, logging, and configuration.
    /// </summary>
    protected IScriptServices? Services { get; private set; }
    
    /// <summary>
    /// Indicates whether services have been injected into this script instance.
    /// </summary>
    public bool HasServices => Services != null;
    
    /// <summary>
    /// Injects runtime services into the script instance.
    /// Called by the script engine after instantiation.
    /// </summary>
    /// <param name="services">The runtime services to inject</param>
    /// <exception cref="ArgumentNullException">Thrown when services is null</exception>
    public void SetServices(IScriptServices services)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
    }

    #region Secret Functions

    /// <summary>
    /// Gets a secret from Dapr secret store
    /// </summary>
    /// <param name="storeName">The name of the dapr secret store</param>
    /// <param name="secretStore">The name of the secret store</param>
    /// <param name="secretKey">The key of the secret</param>
    /// <returns>The secret value</returns>
    protected string GetSecret(string storeName, string secretStore, string secretKey)
    {
        return GetSecretAsync(storeName, secretStore, secretKey)
            .ConfigureAwait(false).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Gets a secret from Dapr secret store asynchronously
    /// </summary>
    /// <param name="storeName">The name of the dapr secret store</param>
    /// <param name="secretStore">The name of the secret store</param>
    /// <param name="secretKey">The key of the secret</param>
    /// <returns>The secret value</returns>
    protected async Task<string> GetSecretAsync(string storeName, string secretStore, string secretKey)
    {
        if (Services?.DaprClient == null)
            throw new InvalidOperationException("Dapr client is not available. Ensure services are injected.");

        if (string.IsNullOrWhiteSpace(storeName))
            throw new ArgumentException("Dapr Store name cannot be null or empty", nameof(storeName));

        if (string.IsNullOrWhiteSpace(secretStore))
            throw new ArgumentException("Store name cannot be null or empty", nameof(secretStore));

        if (string.IsNullOrWhiteSpace(secretKey))
            throw new ArgumentException("Secret key cannot be null or empty", nameof(secretKey));

        try
        {
            var secretsResponse = await Services.DaprClient.GetSecretAsync(storeName, secretStore);

            if (secretsResponse == null)
                return string.Empty;

            return secretsResponse.TryGetValue(secretKey, out var value) ? value : string.Empty;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to retrieve secret '{secretKey}' from store '{storeName}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Gets multiple secrets from Dapr secret store
    /// </summary>
    /// <param name="storeName">The name of the dapr secret store</param>
    /// <param name="secretStore">The name of the secret store</param>
    /// <returns>Dictionary of secret keys and values</returns>
    protected Dictionary<string, string> GetSecrets(string storeName, string secretStore)
    {
        return GetSecretsAsync(storeName, secretStore)
            .ConfigureAwait(false).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Gets multiple secrets from Dapr secret store asynchronously
    /// </summary>
    /// <param name="storeName">The name of the dapr secret store</param>
    /// <param name="secretStore">The name of the secret store</param>
    /// <returns>Dictionary of secret keys and values</returns>
    protected async Task<Dictionary<string, string>> GetSecretsAsync(string storeName, string secretStore)
    {
        if (Services?.DaprClient == null)
            throw new InvalidOperationException("Dapr client is not available. Ensure services are injected.");

        if (string.IsNullOrWhiteSpace(storeName))
            throw new ArgumentException("Dapr Store name cannot be null or empty", nameof(storeName));

        if (string.IsNullOrWhiteSpace(secretStore))
            throw new ArgumentException("Store name cannot be null or empty", nameof(secretStore));

        var secretsResponse = await Services.DaprClient.GetSecretAsync(storeName, secretStore);
        return secretsResponse ?? new Dictionary<string, string>();
    }

    #endregion

    #region Property Helper Functions

    /// <summary>
    /// Checks if a dynamic object has a specific property
    /// </summary>
    /// <param name="obj">The dynamic object to check</param>
    /// <param name="propertyName">The property name to check for</param>
    /// <returns>True if the property exists, false otherwise</returns>
    protected bool HasProperty(object obj, string propertyName)
    {
        if (obj == null || string.IsNullOrWhiteSpace(propertyName)) return false;

        // Check if it's an ExpandoObject or IDictionary<string, object>
        if (obj is IDictionary<string, object> dict)
        {
            return dict.ContainsKey(propertyName);
        }

        // For other dynamic objects, try to access the property using reflection
        var objType = obj.GetType();
        return objType.GetProperty(propertyName,
                   System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance |
                   System.Reflection.BindingFlags.IgnoreCase) != null ||
               objType.GetField(propertyName,
                   System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance |
                   System.Reflection.BindingFlags.IgnoreCase) != null;
    }

    /// <summary>
    /// Gets a property value from a dynamic object safely
    /// </summary>
    /// <param name="obj">The dynamic object</param>
    /// <param name="propertyName">The property name</param>
    /// <returns>The property value or null if not found</returns>
    protected static object? GetPropertyValue(object obj, string propertyName)
    {
        if (obj == null || string.IsNullOrWhiteSpace(propertyName)) return null;

        if (obj is IDictionary<string, object> dict)
        {
            return dict.TryGetValue(propertyName, out var value) ? value : null;
        }

        var objType = obj.GetType();
        var property = objType.GetProperty(propertyName,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.IgnoreCase);
        if (property != null)
        {
            return property.GetValue(obj);
        }
        
        var field = objType.GetField(propertyName,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.IgnoreCase);
        if (field != null)
        {
            return field.GetValue(obj);
        }

        return null;
    }

    /// <summary>
    /// Gets a property value from a dynamic object safely and converts it to a specific type
    /// </summary>
    /// <typeparam name="T">The type to convert the value to</typeparam>
    /// <param name="obj">The dynamic object</param>
    /// <param name="propertyName">The property name</param>
    /// <returns>The property value converted to type T or default(T) if not found or null</returns>
    protected static T? GetPropertyValue<T>(object obj, string propertyName)
    {
        var value = GetPropertyValue(obj, propertyName);

        if (value == null)
            return default;

        if (value is T tValue)
            return tValue;

        try
        {
            return (T)Convert.ChangeType(value, typeof(T));
        }
        catch
        {
            return default;
        }
    }

    /// <summary>
    /// Gets a property value from a dynamic object safely and converts it to a specific type with a default value
    /// </summary>
    /// <typeparam name="T">The type to convert the value to</typeparam>
    /// <param name="obj">The dynamic object</param>
    /// <param name="propertyName">The property name</param>
    /// <param name="defaultValue">The default value to return if property is not found, null, or conversion fails</param>
    /// <returns>The property value converted to type T or defaultValue if not found, null, or conversion fails</returns>
    /// <example>
    /// <code>
    /// // Get string property with default
    /// var name = GetPropertyValue(context.Instance.Data, "name", "Unknown");
    /// 
    /// // Get int property with default
    /// var count = GetPropertyValue(context.Instance.Data, "count", 0);
    /// 
    /// // Get bool property with default
    /// var isActive = GetPropertyValue(context.Instance.Data, "isActive", false);
    /// </code>
    /// </example>
    protected static T GetPropertyValue<T>(object obj, string propertyName, T defaultValue)
    {
        if (obj == null || string.IsNullOrWhiteSpace(propertyName))
            return defaultValue;

        var value = GetPropertyValue(obj, propertyName);

        if (value == null)
            return defaultValue;

        if (value is T tValue)
            return tValue;

        try
        {
            return (T)Convert.ChangeType(value, typeof(T));
        }
        catch
        {
            return defaultValue;
        }
    }

    #endregion

    #region Logging Functions

    /// <summary>
    /// Logs a trace message with caller information
    /// </summary>
    /// <param name="message">The message to log (supports structured logging placeholders like {propertyName})</param>
    /// <param name="file">Source file path (automatically captured)</param>
    /// <param name="method">Method name (automatically captured)</param>
    /// <param name="line">Line number (automatically captured)</param>
    /// <param name="args">Optional arguments for structured logging. IMPORTANT: Use named argument syntax: args: new object[] { value1, value2 }</param>
    /// <example>
    /// <code>
    /// // No arguments
    /// LogTrace("Processing started");
    /// 
    /// // With arguments - use named parameter!
    /// LogTrace("Status: {status}", args: new object[] { context.Instance?.Data?.status });
    /// 
    /// // Multiple arguments
    /// LogTrace("User {userId} at {time}", args: new object[] { userId, DateTime.Now });
    /// </code>
    /// </example>
    protected void LogTrace(
        string message,
        [CallerFilePath] string? file = null,
        [CallerMemberName] string? method = null,
        [CallerLineNumber] int line = 0,
        params object[] args)
    {
        LogWithScope(Microsoft.Extensions.Logging.LogLevel.Trace, message, file, method, line, args);
    }

    /// <summary>
    /// Logs a debug message with caller information
    /// </summary>
    /// <param name="message">The message to log (supports structured logging placeholders like {propertyName})</param>
    /// <param name="file">Source file path (automatically captured)</param>
    /// <param name="method">Method name (automatically captured)</param>
    /// <param name="line">Line number (automatically captured)</param>
    /// <param name="args">Optional arguments for structured logging. IMPORTANT: Use named argument syntax: args: new object[] { value1, value2 }</param>
    protected void LogDebug(
        string message,
        [CallerFilePath] string? file = null,
        [CallerMemberName] string? method = null,
        [CallerLineNumber] int line = 0,
        params object[] args)
    {
        LogWithScope(Microsoft.Extensions.Logging.LogLevel.Debug, message, file, method, line, args);
    }

    /// <summary>
    /// Logs an informational message with caller information
    /// </summary>
    /// <param name="message">The message to log (supports structured logging placeholders like {propertyName})</param>
    /// <param name="file">Source file path (automatically captured)</param>
    /// <param name="method">Method name (automatically captured)</param>
    /// <param name="line">Line number (automatically captured)</param>
    /// <param name="args">Optional arguments for structured logging. IMPORTANT: Use named argument syntax: args: new object[] { value1, value2 }</param>
    protected void LogInformation(
        string message,
        [CallerFilePath] string? file = null,
        [CallerMemberName] string? method = null,
        [CallerLineNumber] int line = 0,
        params object[] args)
    {
        LogWithScope(Microsoft.Extensions.Logging.LogLevel.Information, message, file, method, line, args);
    }

    /// <summary>
    /// Logs a warning message with caller information
    /// </summary>
    /// <param name="message">The message to log (supports structured logging placeholders like {propertyName})</param>
    /// <param name="file">Source file path (automatically captured)</param>
    /// <param name="method">Method name (automatically captured)</param>
    /// <param name="line">Line number (automatically captured)</param>
    /// <param name="args">Optional arguments for structured logging. IMPORTANT: Use named argument syntax: args: new object[] { value1, value2 }</param>
    protected void LogWarning(
        string message,
        [CallerFilePath] string? file = null,
        [CallerMemberName] string? method = null,
        [CallerLineNumber] int line = 0,
        params object[] args)
    {
        LogWithScope(Microsoft.Extensions.Logging.LogLevel.Warning, message, file, method, line, args);
    }

    /// <summary>
    /// Logs an error message with caller information
    /// </summary>
    /// <param name="message">The message to log (supports structured logging placeholders like {propertyName})</param>
    /// <param name="file">Source file path (automatically captured)</param>
    /// <param name="method">Method name (automatically captured)</param>
    /// <param name="line">Line number (automatically captured)</param>
    /// <param name="args">Optional arguments for structured logging. IMPORTANT: Use named argument syntax: args: new object[] { value1, value2 }</param>
    protected void LogError(
        string message,
        [CallerFilePath] string? file = null,
        [CallerMemberName] string? method = null,
        [CallerLineNumber] int line = 0,
        params object[] args)
    {
        LogWithScope(Microsoft.Extensions.Logging.LogLevel.Error, message, file, method, line, args);
    }

    /// <summary>
    /// Logs a critical message with caller information
    /// </summary>
    /// <param name="message">The message to log (supports structured logging placeholders like {propertyName})</param>
    /// <param name="file">Source file path (automatically captured)</param>
    /// <param name="method">Method name (automatically captured)</param>
    /// <param name="line">Line number (automatically captured)</param>
    /// <param name="args">Optional arguments for structured logging. IMPORTANT: Use named argument syntax: args: new object[] { value1, value2 }</param>
    protected void LogCritical(
        string message,
        [CallerFilePath] string? file = null,
        [CallerMemberName] string? method = null,
        [CallerLineNumber] int line = 0,
        params object[] args)
    {
        LogWithScope(Microsoft.Extensions.Logging.LogLevel.Critical, message, file, method, line, args);
    }

    /// <summary>
    /// Internal method that handles logging with scope information.
    /// Gracefully degrades if services are not available.
    /// </summary>
    private void LogWithScope(
        Microsoft.Extensions.Logging.LogLevel level,
        string message,
        string? file,
        string? method,
        int line,
        object[] args)
    {
        if (Services?.Logger == null)
            return; // Graceful degradation - no logging if services unavailable
            
        var scriptFile = string.IsNullOrEmpty(file) ? GetType().Name : file;
        
        using (Services.Logger.BeginScope(new Dictionary<string, object?>
        {
            ["ScriptFile"] = scriptFile,
            ["ScriptMethod"] = method,
            ["ScriptLine"] = line
        }))
        {
            // Use LoggerExtensions to properly handle message template with args
#pragma warning disable CA2254
            Services.Logger.Log(level, message, args);
#pragma warning restore CA2254
        }
    }

    #endregion

    #region Configuration Functions

    /// <summary>
    /// Gets a configuration value by key
    /// </summary>
    /// <param name="key">The configuration key (supports nested keys with ':' separator)</param>
    /// <returns>The configuration value or null if not found</returns>
    protected string? GetConfigValue(string key)
    {
        if (Services?.Configuration == null)
            throw new InvalidOperationException("Configuration is not available. Ensure services are injected.");

        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Configuration key cannot be null or empty", nameof(key));

        return Services.Configuration[key];
    }

    /// <summary>
    /// Gets a configuration value by key with a default value
    /// </summary>
    /// <param name="key">The configuration key (supports nested keys with ':' separator)</param>
    /// <param name="defaultValue">The default value to return if key is not found</param>
    /// <returns>The configuration value or default value if not found</returns>
    protected string GetConfigValue(string key, string defaultValue)
    {
        if (Services?.Configuration == null)
            throw new InvalidOperationException("Configuration is not available. Ensure services are injected.");

        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Configuration key cannot be null or empty", nameof(key));

        return Services.Configuration[key] ?? defaultValue;
    }

    /// <summary>
    /// Gets a configuration value as a specific type
    /// </summary>
    /// <typeparam name="T">The type to convert the value to</typeparam>
    /// <param name="key">The configuration key (supports nested keys with ':' separator)</param>
    /// <returns>The configuration value converted to type T</returns>
    protected T? GetConfigValue<T>(string key)
    {
        if (Services?.Configuration == null)
            throw new InvalidOperationException("Configuration is not available. Ensure services are injected.");

        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Configuration key cannot be null or empty", nameof(key));

        var value = Services.Configuration[key];
        if (value == null)
            return default;

        try
        {
            return (T)Convert.ChangeType(value, typeof(T));
        }
        catch (Exception ex)
        {
            Services.Logger?.LogWarning(
                ex,
                "Failed to convert configuration value for key '{ConfigKey}' to type {TargetType}. Raw value: '{RawValue}'. Returning default.",
                key,
                typeof(T).FullName,
                value
            );

            return default;
        }
    }

    /// <summary>
    /// Gets a configuration value as a specific type with a default value
    /// </summary>
    /// <typeparam name="T">The type to convert the value to</typeparam>
    /// <param name="key">The configuration key (supports nested keys with ':' separator)</param>
    /// <param name="defaultValue">The default value to return if key is not found</param>
    /// <returns>The configuration value converted to type T or default value if not found</returns>
    protected T GetConfigValue<T>(string key, T defaultValue)
    {
        if (Services?.Configuration == null)
            throw new InvalidOperationException("Configuration is not available. Ensure services are injected.");

        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Configuration key cannot be null or empty", nameof(key));

        var value = Services.Configuration[key];
        if (value == null)
            return defaultValue;

        try
        {
            return (T)Convert.ChangeType(value, typeof(T));
        }
        catch (Exception ex)
        {
            Services.Logger?.LogWarning(
                ex,
                "Failed to convert configuration value for key '{ConfigKey}' to type {TargetType}. Raw value: '{RawValue}'. Returning provided default.",
                key,
                typeof(T).FullName,
                value
            );

            return defaultValue;
        }
    }

    /// <summary>
    /// Gets a connection string by name
    /// </summary>
    /// <param name="name">The connection string name</param>
    /// <returns>The connection string or null if not found</returns>
    protected string? GetConnectionString(string name)
    {
        if (Services?.Configuration == null)
            throw new InvalidOperationException("Configuration is not available. Ensure services are injected.");

        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Connection string name cannot be null or empty", nameof(name));

        return Services.Configuration.GetConnectionString(name);
    }

    /// <summary>
    /// Checks if a configuration key exists
    /// </summary>
    /// <param name="key">The configuration key to check</param>
    /// <returns>True if the key exists, false otherwise</returns>
    protected bool ConfigExists(string key)
    {
        if (Services?.Configuration == null)
            throw new InvalidOperationException("Configuration is not available. Ensure services are injected.");

        if (string.IsNullOrWhiteSpace(key))
            return false;

        return Services.Configuration[key] != null;
    }

    #endregion
}
