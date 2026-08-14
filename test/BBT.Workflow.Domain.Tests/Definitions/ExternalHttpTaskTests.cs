using System;
using System.Text.Json;
using Xunit;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Pins the contract of the external (orchestrator-executed) HTTP task type: discriminator "21"
/// materializes a <see cref="ExternalHttpTask"/> that shares HttpTask's whole configuration and
/// scripting surface, while keeping its own runtime type identity through cloning.
/// </summary>
public class ExternalHttpTaskTests
{
    [Fact]
    public void Deserialize_Type21_MaterializesExternalHttpTask_WithInheritedConfig()
    {
        var json = """
        {
            "type": "21",
            "config": {
                "url": "https://google.com",
                "method": "GET",
                "timeoutSeconds": 12,
                "validateSsl": false,
                "acceptedStatusCodes": [ "404" ]
            }
        }
        """;

        var task = JsonSerializer.Deserialize<WorkflowTask>(json, JsonSerializerConstants.JsonOptions);

        var localHttp = Assert.IsType<ExternalHttpTask>(task);
        Assert.Equal(TaskType.ExternalHttp, localHttp.GetTaskType());
        Assert.Equal("https://google.com", localHttp.Url);
        Assert.Equal("GET", localHttp.Method);
        Assert.Equal(12, localHttp.TimeoutSeconds);
        Assert.False(localHttp.ValidateSSL);
        Assert.Equal(["404"], localHttp.AcceptedStatusCodes);
    }

    [Fact]
    public void Deserialize_Type6_StillMaterializesPlainHttpTask()
    {
        var json = """{ "type": "6", "config": { "url": "https://example.com" } }""";

        var task = JsonSerializer.Deserialize<WorkflowTask>(json, JsonSerializerConstants.JsonOptions);

        Assert.IsType<HttpTask>(task);
        Assert.Equal(TaskType.Http, task!.GetTaskType());
    }

    /// <summary>
    /// Mapping scripts cast <c>task as HttpTask</c> and drive the request through its setters;
    /// the local variant must satisfy that cast so existing script idioms work unchanged.
    /// </summary>
    [Fact]
    public void ExternalHttpTask_IsUsableThroughTheHttpTaskScriptSurface()
    {
        var task = ExternalHttpTask.Create("""{ "url": "http://placeholder" }""".ToJsonElement());

        var asHttp = task as HttpTask;

        Assert.NotNull(asHttp);
        asHttp!.SetUrl("https://resolved.example.com/api");
        asHttp.AddHeader("Authorization", "key");
        Assert.Equal("https://resolved.example.com/api", task.Url);
    }

    /// <summary>
    /// The per-execution copy is made via <see cref="WorkflowTask.Clone"/>; losing the runtime
    /// type there would silently re-route the task to the remote type-6 executor.
    /// </summary>
    [Fact]
    public void Clone_PreservesRuntimeTypeAndConfiguration()
    {
        var task = ExternalHttpTask.Create("""
        {
            "url": "https://example.com/api",
            "method": "POST",
            "timeoutSeconds": 45,
            "acceptedStatusCodes": [ "4xx" ]
        }
        """.ToJsonElement());
        task.SetReference(new Reference("local-call", "test-domain", "sys-tasks", "1.0.0"));

        var clone = task.Clone();

        var typedClone = Assert.IsType<ExternalHttpTask>(clone);
        Assert.Equal(TaskType.ExternalHttp, typedClone.GetTaskType());
        Assert.Equal("https://example.com/api", typedClone.Url);
        Assert.Equal("POST", typedClone.Method);
        Assert.Equal(45, typedClone.TimeoutSeconds);
        Assert.Equal(["4xx"], typedClone.AcceptedStatusCodes);
        Assert.Equal("local-call", typedClone.Key);
    }
}
