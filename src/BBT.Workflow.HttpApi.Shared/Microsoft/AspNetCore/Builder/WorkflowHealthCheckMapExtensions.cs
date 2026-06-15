using System.Net.Mime;
using System.Text.Json;
using System.Text.Json.Serialization;
using BBT.Workflow.Runtime;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Microsoft.AspNetCore.Builder;

public static class WorkflowHealthCheckMapExtensions
{
    private static readonly JsonSerializerOptions JsonSerializerOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static WebApplication MapAppHealthChecks(this WebApplication app)
    {
        app.MapHealthChecks("/health",
            new HealthCheckOptions
            {
                Predicate = _ => true, AllowCachingResponses = false, ResponseWriter = WriteResponse
            });

        app.MapHealthChecks("/ready",
            new HealthCheckOptions
            {
                Predicate = (check) => check.Tags.Contains("ready"), ResponseWriter = WriteResponse
            });

        app.MapHealthChecks("/live",
            new HealthCheckOptions
            {
                Predicate = (check) => check.Tags.Contains("live"), ResponseWriter = WriteResponse
            });

        app.MapGet("/version", (IRuntimeInfoProvider runtimeInfoProvider) =>
                Results.Ok(new { version = runtimeInfoProvider.Version }))
            .ExcludeFromDescription();

        return app;
    }

    private static Task WriteResponse(
        HttpContext context,
        HealthReport report)
    {
        var runtimeInfoProvider = context.RequestServices.GetService<IRuntimeInfoProvider>();

        var json = JsonSerializer.Serialize(
            new
            {
                Status = report.Status.ToString(),
                Duration = report.TotalDuration,
                Domain = runtimeInfoProvider?.Domain,
                Version = runtimeInfoProvider?.Version,
                Info = report.Entries
                    .Select(e =>
                        new
                        {
                            e.Key,
                            e.Value.Description,
                            e.Value.Duration,
                            Status = Enum.GetName(e.Value.Status),
                            Error = e.Value.Exception?.Message,
                            e.Value.Tags,
                            e.Value.Data
                        })
                    .ToList()
            },
            JsonSerializerOptions);

        context.Response.ContentType = MediaTypeNames.Application.Json;
        return context.Response.WriteAsync(json);
    }
}