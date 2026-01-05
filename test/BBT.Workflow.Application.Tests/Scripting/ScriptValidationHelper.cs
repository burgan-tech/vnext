using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using BBT.Workflow.Scripting.Functions;
using Moq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Scripting;

/// <summary>
/// Utility class for validating workflow scripts quickly and easily.
/// This helper can be used in console applications, interactive environments,
/// or anywhere you need to validate scripts outside of formal testing.
/// </summary>
public static class ScriptValidationHelper
{
    private static IScriptEngine? _scriptEngine;
    private static readonly object _lockObject = new();

    /// <summary>
    /// Initialize the script validation helper with necessary dependencies
    /// </summary>
    public static void Initialize(IServiceProvider serviceProvider)
    {
        lock (_lockObject)
        {
            _scriptEngine = serviceProvider.GetRequiredService<IScriptEngine>();
        }
    }

    private static void EnsureInitialized()
    {
        if (_scriptEngine == null)
        {
            throw new InvalidOperationException(
                "ScriptValidationHelper is not initialized. " +
                "Call ScriptValidationHelper.Initialize(serviceProvider) first.");
        }
    }
    
    private static async Task<object?> TestMappingExecutionInternal(
        IMapping mapping, 
        string scriptName, 
        bool verbose)
    {
        try
        {
            // Create mock HTTP task
            var httpTask = HttpTask.CreateEmpty();
            httpTask.Url = "https://api.example.com/test/{contract_code}";
            httpTask.Method = "POST";

            // Create mock headers
            var mockHeaders = new Dictionary<string, string>
            {
                ["Accept-Language"] = "tr-TR",
                ["X-Request-Id"] = "test-request-123",
                ["user_reference"] = "test-user",
                ["ClientId"] = "test-client",
                ["business_line"] = "retail"
            };

            // Create mock instance data
            var mockInstanceData = new
            {
                entityData = new
                {
                    subProductCode = "VDLGLDR",
                    customerNumber = "12345",
                    accountType = "savings"
                }
            };

            // Build script context
            var context = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>())
                .SetWorkflow(CreateMockWorkflow())
                .SetInstance(CreateMockInstance(mockInstanceData))
                .SetTransition(CreateMockTransition())
                .SetRuntime(Mock.Of<Runtime.IRuntimeInfoProvider>())
                .SetHeaders(mockHeaders)
                .SetBody(mockInstanceData)
                .SetDefinitions(new Dictionary<string, object>())
                .Build();

            // Test InputHandler
            if (verbose) Console.WriteLine("🧪 Testing InputHandler...");
            var inputResponse = await mapping.InputHandler(httpTask, context);
            if (verbose) Console.WriteLine("✅ InputHandler executed successfully");

            // Test OutputHandler with mock response
            if (verbose) Console.WriteLine("🧪 Testing OutputHandler...");
            context.SetBody(new { StatusCode = 200, Data = new { result = "success" } });
            var outputResponse = await mapping.OutputHandler(context);
            if (verbose) Console.WriteLine("✅ OutputHandler executed successfully");

            return new { Input = inputResponse, Output = outputResponse };
        }
        catch (Exception ex)
        {
            if (verbose)
            {
                Console.WriteLine($"⚠️ {scriptName} execution test failed: {ex.Message}");
            }
            return null;
        }
    }

    /// <summary>
    /// Cleans script code for testing by commenting out #load directives
    /// and adding necessary using statements
    /// </summary>
    /// <param name="scriptCode">Original script code</param>
    /// <returns>Cleaned script code ready for testing</returns>
    private static string CleanScriptForTesting(string scriptCode)
    {
        if (string.IsNullOrWhiteSpace(scriptCode))
            return scriptCode;

        var lines = scriptCode.Split(new[] { '\r', '\n' }, StringSplitOptions.None);
        var cleanedLines = new List<string>();
        
        // Add necessary using statements at the top
        var hasSystemUsing = false;
        var hasJsonUsing = false;
        
        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            
            // Comment out #load directives
            if (trimmedLine.StartsWith("#load"))
            {
                cleanedLines.Add("//" + line);
                continue;
            }
            
            // Check for existing using statements
            if (trimmedLine.StartsWith("using System;"))
                hasSystemUsing = true;
            if (trimmedLine.StartsWith("using System.Text.Json;"))
                hasJsonUsing = true;
                
            cleanedLines.Add(line);
        }
        
        // Insert missing using statements after the first commented #load line
        var insertIndex = 0;
        for (int i = 0; i < cleanedLines.Count; i++)
        {
            if (cleanedLines[i].TrimStart().StartsWith("//#load"))
            {
                insertIndex = i + 1;
                break;
            }
        }
        
        var usingsToAdd = new List<string>();
        if (!hasSystemUsing)
            usingsToAdd.Add("using System;");
        if (!hasJsonUsing)
            usingsToAdd.Add("using System.Text.Json;");
            
        if (usingsToAdd.Any())
        {
            // Add empty line after #load comments
            if (insertIndex > 0 && !string.IsNullOrWhiteSpace(cleanedLines[insertIndex]))
                usingsToAdd.Insert(0, "");
                
            cleanedLines.InsertRange(insertIndex, usingsToAdd);
        }
        
        return string.Join(Environment.NewLine, cleanedLines);
    }

    // Helper methods to create mock objects
    private static Definitions.Workflow CreateMockWorkflow()
    {
        // This would need to be implemented based on your Workflow factory
        // For now, return a basic mock
        return Mock.Of<Definitions.Workflow>();
    }

    private static Instance CreateMockInstance(object data)
    {
        // This would need to be implemented based on your Instance factory
        // For now, return a basic mock with the data
        var instance = Mock.Of<Instance>();
        // Set up the mock to return the provided data
        return instance;
    }

    private static Transition CreateMockTransition()
    {
        var transition = Mock.Of<Transition>();
        Mock.Get(transition).Setup(t => t.Key).Returns("test-transition");
        return transition;
    }
}

/// <summary>
/// Internal result for validation operations
/// </summary>
internal class ValidationResult
{
    public bool IsValid { get; set; }
    public string Message { get; set; } = "";
    public string? ErrorMessage { get; set; }
    public IMapping? MappingInstance { get; set; }
    public object? ExecutionResult { get; set; }

    public static ValidationResult Success(string message, IMapping? mappingInstance = null, object? executionResult = null)
        => new() { IsValid = true, Message = message, MappingInstance = mappingInstance, ExecutionResult = executionResult };

    public static ValidationResult Failed(string errorMessage)
        => new() { IsValid = false, ErrorMessage = errorMessage, Message = "Validation failed" };
}
