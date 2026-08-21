using System.Linq;
using System.Text.Json;
using BBT.Workflow.Definitions;

namespace BBT.Workflow;

public static class WorkflowTaskFactory
{
    public static HttpTask CreateHttpTask(string name = "mock-api")
    {
        var config = """
                      {
                        "url": "https://httpbin.org/get",
                        "method": "GET",
                        "headers": {
                            "User-Agent": "vNext/1.0"
                        },
                        "timeoutSeconds": 25
                      }
                     """;

        var task = HttpTask.Create(config.ToJsonElement());
        task.SetReference(new Reference(name, "test", "sys-tasks", "1.0.0"));
        return task;
    }

    public static SoapTask CreateSoapTask(string name = "mock-soap")
    {
        var config = """
                      {
                        "url": "https://example.com/soap",
                        "soapAction": "urn:Test",
                        "soapVersion": "1.1",
                        "body": "<envelope/>",
                        "timeoutSeconds": 25
                      }
                     """;

        var task = SoapTask.Create(config.ToJsonElement());
        task.SetReference(new Reference(name, "test", "sys-tasks", "1.0.0"));
        return task;
    }

    public static GetInstancesTask CreateGetInstancesTask(
        string name = "get-instances",
        string domain = "test-domain",
        string flow = "test-flow",
        int page = 1,
        int pageSize = 10,
        string? sort = null,
        string? filter = null,
        bool useDapr = false)
    {
        var filterJson = !string.IsNullOrWhiteSpace(filter)
            ? $@"""filter"": ""{filter.Replace("\"", "\\\"")}"","
            : "";
        // Escaped like filter above: sort is itself JSON, so its quotes must not close the string.
        var sortJson = sort != null ? $@"""sort"": ""{sort.Replace("\"", "\\\"")}""," : "";

        var config = $@"{{
            ""key"": ""{name}"",
            ""type"": ""15"",
            ""domain"": ""{domain}"",
            ""flow"": ""{flow}"",
            ""page"": {page},
            ""pageSize"": {pageSize},
            {sortJson}
            {filterJson}
            ""useDapr"": {useDapr.ToString().ToLowerInvariant()}
        }}";

        var task = GetInstancesTask.Create(config.ToJsonElement());
        task.SetReference(new Reference(name, domain, "sys-tasks", "1.0.0"));
        return task;
    }

    public static GetInstanceTask CreateGetInstanceTask(
        string name = "get-instance",
        string domain = "test-domain",
        string flow = "test-flow",
        string? key = "inst-key",
        string? instanceId = null,
        bool useDapr = false)
    {
        var keyJson = key != null ? $@"""key"": ""{key}""," : "";
        var instanceIdJson = instanceId != null ? $@"""instanceId"": ""{instanceId}""," : "";

        var config = $@"{{
            ""key"": ""{name}"",
            ""type"": ""18"",
            ""domain"": ""{domain}"",
            ""flow"": ""{flow}"",
            {keyJson}
            {instanceIdJson}
            ""useDapr"": {useDapr.ToString().ToLowerInvariant()}
        }}";

        var task = GetInstanceTask.Create(config.ToJsonElement());
        task.SetReference(new Reference(name, domain, "sys-tasks", "1.0.0"));
        return task;
    }
}