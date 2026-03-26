using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
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

    #region Dynamic Collection Functions

    /// <summary>
    /// Safely casts a dynamic value to <see cref="List{T}"/> of <see cref="object"/>.
    /// Arrays in <c>Instance.Data</c> are represented as <c>List&lt;object?&gt;</c> at runtime.
    /// Returns an empty list if the value is null or not a list.
    /// </summary>
    /// <param name="list">The dynamic value to cast</param>
    /// <returns>A <see cref="List{T}"/> of nullable objects, or an empty list</returns>
    protected static List<object?> AsList(object? list)
    {
        return list as List<object?> ?? [];
    }

    /// <summary>
    /// Gets a list property from a dynamic object by property name.
    /// Equivalent to <c>GetPropertyValue</c> followed by <c>AsList</c>.
    /// </summary>
    /// <param name="obj">The dynamic object containing the list property</param>
    /// <param name="propertyName">The property name of the list</param>
    /// <returns>The list, or an empty list if the property does not exist or is not a list</returns>
    /// <example>
    /// <code>
    /// var items = GetList(context.Instance.Data, "items");
    /// </code>
    /// </example>
    protected static List<object?> GetList(object? obj, string propertyName)
    {
        if (obj is null) return [];
        return AsList(GetPropertyValue(obj!, propertyName));
    }

    /// <summary>
    /// Filters a dynamic list using a predicate. Equivalent to LINQ <c>.Where()</c>.
    /// </summary>
    /// <param name="list">The dynamic list to filter</param>
    /// <param name="predicate">The condition each element must satisfy</param>
    /// <returns>A new list containing only matching elements</returns>
    /// <remarks>
    /// When the list comes from a <c>dynamic</c> source, convert it first to avoid CS1977:
    /// <code>
    /// var items = AsList(context.Instance.Data.items);
    /// var active = ListFilter(items, x => x.status == "active");
    /// </code>
    /// </remarks>
    protected static List<object?> ListFilter(object? list, Func<dynamic, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return AsList(list).Where(item => predicate(item!)).ToList();
    }

    /// <summary>
    /// Returns the first element in a dynamic list that satisfies the predicate, or null if none found.
    /// Equivalent to LINQ <c>.FirstOrDefault()</c>.
    /// </summary>
    /// <param name="list">The dynamic list to search</param>
    /// <param name="predicate">Optional filter condition; if null, returns the first element</param>
    /// <returns>The first matching element, or null</returns>
    /// <remarks>
    /// When the list comes from a <c>dynamic</c> source, convert it first to avoid CS1977:
    /// <code>
    /// var items = AsList(context.Instance.Data.items);
    /// var item = ListFirst(items, x => x.id == targetId);
    /// </code>
    /// </remarks>
    protected static dynamic? ListFirst(object? list, Func<dynamic, bool>? predicate = null)
    {
        var items = AsList(list);
        return predicate == null
            ? items.FirstOrDefault()
            : items.FirstOrDefault(item => predicate(item!));
    }

    /// <summary>
    /// Returns the last element in a dynamic list that satisfies the predicate, or null if none found.
    /// Equivalent to LINQ <c>.LastOrDefault()</c>.
    /// </summary>
    /// <param name="list">The dynamic list to search</param>
    /// <param name="predicate">Optional filter condition; if null, returns the last element</param>
    /// <returns>The last matching element, or null</returns>
    protected static dynamic? ListLast(object? list, Func<dynamic, bool>? predicate = null)
    {
        var items = AsList(list);
        return predicate == null
            ? items.LastOrDefault()
            : items.LastOrDefault(item => predicate(item!));
    }

    /// <summary>
    /// Determines whether any element in a dynamic list satisfies the predicate.
    /// Equivalent to LINQ <c>.Any()</c>.
    /// </summary>
    /// <param name="list">The dynamic list to check</param>
    /// <param name="predicate">Optional filter condition; if null, checks whether the list has any element</param>
    /// <returns><c>true</c> if a matching element exists; otherwise <c>false</c></returns>
    /// <remarks>
    /// <b>Important:</b> When the list comes from a dynamic source (e.g. <c>context.Instance.Data.items</c>),
    /// C# cannot combine a <c>dynamic</c> argument with a lambda in the same call (CS1977).
    /// Always convert to a typed list first using <see cref="AsList"/> or <see cref="GetList"/>:
    /// <code>
    /// // ✗ Fails with CS1977 — dynamic argument + lambda in same call
    /// // ListAny(context.Instance.Data.items, x => x.status == "pending");
    ///
    /// // ✓ Correct — convert to List&lt;object?&gt; first
    /// var items = AsList(context.Instance.Data.items);
    /// var hasPending = ListAny(items, x => x.status == "pending");
    ///
    /// // ✓ Also correct — using GetList when accessing by property name
    /// var hasPending = ListAny(GetList(context.Instance.Data, "items"), x => x.status == "pending");
    ///
    /// // ✓ No-predicate form works directly (no lambda → no CS1977)
    /// var hasErrors = ListAny(context.Instance.Data.errors);
    /// </code>
    /// </remarks>
    protected static bool ListAny(object? list, Func<dynamic, bool>? predicate = null)
    {
        var items = AsList(list);
        return predicate == null
            ? items.Count > 0
            : items.Any(item => predicate(item!));
    }

    /// <summary>
    /// Returns the number of elements in a dynamic list, optionally filtered by a predicate.
    /// Equivalent to LINQ <c>.Count()</c>.
    /// </summary>
    /// <param name="list">The dynamic list to count</param>
    /// <param name="predicate">Optional filter condition</param>
    /// <returns>The count of matching elements</returns>
    /// <example>
    /// <code>
    /// var total = ListCount(context.Instance.Data.items);
    /// var activeCount = ListCount(context.Instance.Data.items, x => x.active == true);
    /// </code>
    /// </example>
    protected static int ListCount(object? list, Func<dynamic, bool>? predicate = null)
    {
        var items = AsList(list);
        return predicate == null
            ? items.Count
            : items.Count(item => predicate(item!));
    }

    /// <summary>
    /// Projects each element of a dynamic list into a new form.
    /// Equivalent to LINQ <c>.Select()</c>.
    /// </summary>
    /// <typeparam name="TResult">The type of projected elements</typeparam>
    /// <param name="list">The dynamic list to project</param>
    /// <param name="selector">A transform function applied to each element</param>
    /// <returns>A list of projected values</returns>
    /// <example>
    /// <code>
    /// var ids = ListSelect&lt;string&gt;(context.Instance.Data.items, x => (string)x.id);
    /// </code>
    /// </example>
    protected static List<TResult> ListSelect<TResult>(object? list, Func<dynamic, TResult> selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        return AsList(list).Select(item => selector(item!)).ToList();
    }

    /// <summary>
    /// Adds an item to a dynamic list. The list must be backed by a <c>List&lt;object?&gt;</c>.
    /// </summary>
    /// <param name="list">The dynamic list to modify</param>
    /// <param name="item">The item to add</param>
    /// <example>
    /// <code>
    /// var items = GetList(context.Instance.Data, "items");
    /// ListAdd(items, newItem);
    /// </code>
    /// </example>
    protected static void ListAdd(object? list, object? item)
    {
        AsList(list).Add(item);
    }

    /// <summary>
    /// Removes all elements from a dynamic list that satisfy the predicate.
    /// Equivalent to <c>List&lt;T&gt;.RemoveAll()</c>.
    /// </summary>
    /// <param name="list">The dynamic list to modify</param>
    /// <param name="predicate">The condition to match elements for removal</param>
    /// <returns>The number of elements removed</returns>
    /// <example>
    /// <code>
    /// var removed = ListRemove(context.Instance.Data.items, x => x.status == "deleted");
    /// </code>
    /// </example>
    protected static int ListRemove(object? list, Func<dynamic, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return AsList(list).RemoveAll(item => predicate(item!));
    }

    #endregion

    #region Dynamic Object Functions

    /// <summary>
    /// Creates a new empty dynamic object (<see cref="ExpandoObject"/>).
    /// Use <see cref="SetProperty"/> to assign properties to it.
    /// </summary>
    /// <returns>A new <c>dynamic</c> ExpandoObject instance</returns>
    /// <example>
    /// <code>
    /// dynamic item = CreateObject();
    /// SetProperty(item, "id", Guid.NewGuid().ToString());
    /// SetProperty(item, "status", "pending");
    /// </code>
    /// </example>
    protected static dynamic CreateObject()
    {
        return new ExpandoObject();
    }

    /// <summary>
    /// Creates a new empty dynamic list compatible with <c>Instance.Data</c> array fields.
    /// </summary>
    /// <returns>A new empty <c>List&lt;object?&gt;</c></returns>
    /// <example>
    /// <code>
    /// var newList = CreateList();
    /// ListAdd(newList, CreateObject());
    /// SetProperty(context.Instance.Data, "items", newList);
    /// </code>
    /// </example>
    protected static List<object?> CreateList()
    {
        return [];
    }

    /// <summary>
    /// Sets a property value on a dynamic object. Supports <see cref="ExpandoObject"/> and
    /// regular CLR objects. If the object is an <c>ExpandoObject</c>, the property is created
    /// if it does not already exist.
    /// </summary>
    /// <param name="obj">The dynamic object to modify</param>
    /// <param name="propertyName">The property name to set</param>
    /// <param name="value">The value to assign</param>
    /// <example>
    /// <code>
    /// SetProperty(context.Instance.Data, "processedAt", DateTime.UtcNow);
    /// </code>
    /// </example>
    protected static void SetProperty(object obj, string propertyName, object? value)
    {
        ArgumentNullException.ThrowIfNull(obj);
        if (string.IsNullOrWhiteSpace(propertyName)) return;

        if (obj is IDictionary<string, object?> dict)
        {
            dict[propertyName] = value;
            return;
        }

        obj.GetType()
           .GetProperty(propertyName,
               System.Reflection.BindingFlags.Public |
               System.Reflection.BindingFlags.Instance |
               System.Reflection.BindingFlags.IgnoreCase)
           ?.SetValue(obj, value);
    }

    /// <summary>
    /// Removes a property from a dynamic object. Only works with <see cref="ExpandoObject"/>;
    /// returns <c>false</c> for other types.
    /// </summary>
    /// <param name="obj">The dynamic object to modify</param>
    /// <param name="propertyName">The property name to remove</param>
    /// <returns><c>true</c> if the property was found and removed; otherwise <c>false</c></returns>
    /// <example>
    /// <code>
    /// RemoveProperty(context.Instance.Data, "tempField");
    /// </code>
    /// </example>
    protected static bool RemoveProperty(object obj, string propertyName)
    {
        if (obj is IDictionary<string, object?> dict)
            return dict.Remove(propertyName);

        return false;
    }

    /// <summary>
    /// Converts a dynamic object to a <c>Dictionary&lt;string, object?&gt;</c>.
    /// Useful when you need to enumerate properties or pass dynamic data to typed APIs.
    /// Returns an empty dictionary for null input.
    /// </summary>
    /// <param name="obj">The dynamic object to convert</param>
    /// <returns>A dictionary of property names and values</returns>
    /// <example>
    /// <code>
    /// var dict = ToDictionary(context.Instance.Data);
    /// foreach (var kv in dict) { ... }
    /// </code>
    /// </example>
    protected static Dictionary<string, object?> ToDictionary(object? obj)
    {
        if (obj is IDictionary<string, object?> dict)
            return new Dictionary<string, object?>(dict);

        if (obj is IDictionary<string, object> dict2)
            return dict2.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);

        return [];
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
            ["ScriptLine"] = line,
            ["HasScript"] = true
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
