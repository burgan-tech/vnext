using System.Text.Json;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Monitoring;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Tasks.Factory;

/// <summary>
/// Pins the pooled-copy path for the external HTTP task: <c>PoolableTaskRegistry</c> uses
/// exact-type lookup, so without its own registration a pooled <see cref="ExternalHttpTask"/>
/// would silently fall back to <c>CopyBaseToInternal</c> and lose every HTTP property
/// (URL, method, headers, body).
/// </summary>
public sealed class PooledTaskFactoryTests
{
    [Fact]
    public void CreateFromCached_PooledExternalHttpTask_RetainsHttpProperties()
    {
        var factory = new PooledTaskFactory(
            Substitute.For<IComponentCacheStore>(),
            NullLogger<PooledTaskFactory>.Instance,
            Options.Create(new TaskFactoryOptions
            {
                UseObjectPooling = true,
                PooledTaskTypes = ["ExternalHttpTask"]
            }),
            Substitute.For<IWorkflowMetrics>());

        var cached = ExternalHttpTask.Create(JsonDocument.Parse("""
        {
            "url": "https://api.example.com/orders",
            "method": "POST",
            "timeoutSeconds": 45,
            "validateSsl": false,
            "acceptedStatusCodes": [ "404" ]
        }
        """).RootElement);
        cached.SetReference(new Reference("external-call", "test-domain", "sys-tasks", "1.0.0"));

        var result = factory.CreateFromCached(cached);

        result.IsSuccess.ShouldBeTrue();
        var copy = result.Value.ShouldBeOfType<ExternalHttpTask>();
        copy.ShouldNotBeSameAs(cached);
        copy.GetTaskType().ShouldBe(TaskType.ExternalHttp);
        copy.Url.ShouldBe("https://api.example.com/orders");
        copy.Method.ShouldBe("POST");
        copy.TimeoutSeconds.ShouldBe(45);
        copy.ValidateSSL.ShouldBeFalse();
        copy.AcceptedStatusCodes.ShouldBe(["404"]);
        copy.Key.ShouldBe("external-call");
    }
}
