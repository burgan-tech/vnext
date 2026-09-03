using System.Diagnostics;
using BBT.Aether.AspNetCore.Dapr;
using BBT.Aether.AspNetCore.Threads;
using BBT.Workflow.HttpApi.Shared.Telemetry;
using Dapr.Client;
using Dapr.Extensions.Configuration;
using Microsoft.AspNetCore.Builder;

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

builder.Services.AddWorkerInboxModule();

var host = builder.Build();
host.UseWorkerInbox();
await host.RunAsync();