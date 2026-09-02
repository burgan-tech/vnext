using System.Diagnostics;
using BBT.Aether.AspNetCore.Dapr;
using BBT.Aether.AspNetCore.Threads;
using BBT.Workflow.HttpApi.Shared.Telemetry;
using BBT.Workflow.Logging;
using Dapr.Client;
using Dapr.Extensions.Configuration;

ThreadPoolHelper.ConfigureThreadPool();

// PROCESS-GLOBAL MUTATION: repairs Dapr's duplicated (comma-joined) traceparent on sidecar hops so the
// inbound request is parented to the caller's trace instead of being rooted as a new one. No-op for
// well-formed input. MUST stay above WebApplication.CreateBuilder — hosting captures the propagator
// instance into DI during builder construction. Full rationale and live trace evidence: the Execution
// host's Program.cs and docs/runtime/dapr-invocation-transport.md.
DistributedContextPropagator.Current =
    new DuplicateTolerantTraceContextPropagator(DistributedContextPropagator.Current);

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.SetBasePath(Directory.GetCurrentDirectory());
// Dapr Optional
if(builder.Configuration.GetValue<bool>("Vault:Enabled", false)){
    var daprClient = new DaprClientBuilder()
        .Build();

    await DaprCheckForSidecarHelper.CheckAsync(daprClient);
    builder.Configuration.AddDaprSecretStore(builder.Configuration["DAPR_SECRET_STORE_NAME"] ?? "vnext-secret", daprClient);
}


builder.WebHost.ConfigureKestrel(options =>
{
    options.AddServerHeader = false;

    var limitsSection = builder.Configuration.GetSection("Kestrel:Limits");
    options.Limits.MaxRequestHeadersTotalSize =
        limitsSection.GetValue<int>(nameof(options.Limits.MaxRequestHeadersTotalSize), 65_536);
    options.Limits.MaxRequestHeaderCount =
        limitsSection.GetValue<int>(nameof(options.Limits.MaxRequestHeaderCount), 200);
});

builder.Services.AddOrchestrationApiModule();

var app = builder.Build();
app.UseOrchestrationApiModule();

app.Logger.KestrelLimitsConfigured(
    app.Configuration.GetValue<int>("Kestrel:Limits:MaxRequestHeadersTotalSize", 65_536),
    app.Configuration.GetValue<int>("Kestrel:Limits:MaxRequestHeaderCount", 200));

await app.RunAsync();