using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Scripting.Functions;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using Xunit.Abstractions;
using IRuntimeInfoProvider = BBT.Workflow.Runtime.IRuntimeInfoProvider;
using Dapr.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace BBT.Workflow.Scripting;

/// <summary>
/// Dynamic script validation tests to help developers test their scripts quickly
/// without needing to debug constantly. These tests provide a way to validate
/// script compilation and basic execution scenarios.
/// </summary>
[Collection("ScriptingTests")]
public class ScriptValidationTests : ApplicationTestBase<ApplicationEntryPoint>
{
    private readonly IScriptEngine _scriptEngine;
    private readonly ITestOutputHelper _output;

    public ScriptValidationTests(ITestOutputHelper output)
    {
        _scriptEngine = GetRequiredService<IScriptEngine>();
        _output = output;
    }

    protected override void AddApplication(IServiceCollection services)
    {
        // Mock DaprClient for testing
        var mockDaprClient = new Mock<DaprClient>();
        mockDaprClient
            .Setup(x => x.GetSecretAsync(
                It.IsAny<string>(), 
                It.IsAny<string>(), 
                It.IsAny<IReadOnlyDictionary<string, string>>(), 
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string> { { "test_key", "mock_secret_value" } });

        services.AddSingleton(mockDaprClient.Object);
        
        // Mock Logger for IScriptServices
        var mockLogger = new Mock<ILogger<ScriptServices>>();
        services.AddSingleton(mockLogger.Object);
        
        // Mock Configuration for IScriptServices
        var mockConfiguration = new Mock<IConfiguration>();
        services.AddSingleton(mockConfiguration.Object);
        
        base.AddApplication(services);
    }

    /// <summary>
    /// Cleans script code for testing by commenting out #load directives
    /// and adding necessary using statements
    /// </summary>
    /// <param name="scriptCode">Original script code</param>
    /// <returns>Cleaned script code ready for testing</returns>
    private string CleanScriptForTesting(string scriptCode)
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

    /// <summary>
    /// Test IMapping execution with mock data
    /// </summary>
    private async Task<object?> TestMappingExecution(IMapping mapping, string scriptName)
    {
        try
        {
            var httpTask = WorkflowTaskFactory.CreateHttpTask();
            var mockHeaders = new Dictionary<string, string>
            {
                ["Accept-Language"] = "tr-TR",
                ["X-Request-Id"] = "test-request-123",
                ["user_reference"] = "test-user",
                ["ClientId"] = "test-client",
                ["business_line"] = "retail"
            };

            var mockInstanceData = new
            {
                entityData = new
                {
                    subProductCode = "VDLGLDR",
                    customerNumber = "12345",
                    accountType = "savings"
                }
            };

            var instance = InstanceFactory.CreateDefault();
            instance.AddData(Guid.NewGuid(), new JsonData(JsonSerializer.Serialize(mockInstanceData)), VersionStrategy.IncreaseMajor);

            var context = new ScriptContext.Builder(Mock.Of<ILogger<ScriptContext>>())
                .SetWorkflow(WorkflowFactory.CreateDefault())
                .SetInstance(instance)
                .SetTransition(TransitionFactory.CreateDefault())
                .SetRuntime(Mock.Of<IRuntimeInfoProvider>())
                .SetHeaders(mockHeaders)
                .SetBody(mockInstanceData)
                .SetDefinitions(new Dictionary<string, object>())
                .Build();

            // Test InputHandler
            var inputResponse = await mapping.InputHandler(httpTask, context);
            _output.WriteLine($"✅ {scriptName} InputHandler executed successfully");

            // Test OutputHandler with mock response
            context.SetBody(new { StatusCode = 200, Data = new { result = "success" } });
            var outputResponse = await mapping.OutputHandler(context);
            _output.WriteLine($"✅ {scriptName} OutputHandler executed successfully");

            return new { Input = inputResponse, Output = outputResponse };
        }
        catch (Exception ex)
        {
            _output.WriteLine($"⚠️ {scriptName} execution test failed: {ex.Message}");
            return null;
        }
    }
}

/// <summary>
/// Result of script validation operation
/// </summary>
public class ScriptValidationResult
{
    public bool IsValid { get; private set; }
    public string Message { get; private set; } = "";
    public string? ErrorMessage { get; private set; }
    public IMapping? MappingInstance { get; private set; }
    public object? ExecutionResult { get; private set; }

    private ScriptValidationResult() { }

    public static ScriptValidationResult Success(string message, IMapping? mappingInstance = null, object? executionResult = null)
    {
        return new ScriptValidationResult
        {
            IsValid = true,
            Message = message,
            MappingInstance = mappingInstance,
            ExecutionResult = executionResult
        };
    }

    public static ScriptValidationResult Failed(string errorMessage)
    {
        return new ScriptValidationResult
        {
            IsValid = false,
            ErrorMessage = errorMessage,
            Message = "Validation failed"
        };
    }
}
