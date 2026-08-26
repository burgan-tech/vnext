using BBT.Aether.AspNetCore.Dapr;
using BBT.Aether.AspNetCore.Threads;
using BBT.Workflow.Logging;
using Dapr.Client;
using Dapr.Extensions.Configuration;
using Microsoft.AspNetCore.Server.Kestrel.Core;

ThreadPoolHelper.ConfigureThreadPool();

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

    // Two cleartext endpoints, deliberately NOT one Http1AndHttp2 endpoint.
    //
    // Without TLS there is no ALPN to negotiate HTTP/1.1 vs HTTP/2, and Kestrel does not
    // byte-sniff the client preface to multiplex both on a single cleartext port -- it just
    // downgrades the whole endpoint to HTTP/1.1 (observable at startup as the
    // "Http2DisabledWithHttp1AndNoTls" warning). So the MVC controller / health probes / swagger
    // keep an HTTP/1.1-only port, and Dapr's gRPC proxy-mode invocation (ExecutionApi:Transport
    // = grpc on the Orchestration side, --app-protocol grpc on this app's sidecar) gets its own
    // Http2-only h2c port. Do NOT collapse these back into one port -- it will silently break
    // gRPC invocation the same way it did before this comment existed.
    var httpPort = builder.Configuration.GetValue<int>("Kestrel:HttpPort", 4202);
    options.ListenAnyIP(httpPort, listenOptions => listenOptions.Protocols = HttpProtocols.Http1);

    var grpcPort = builder.Configuration.GetValue<int>("Kestrel:GrpcPort", 4212);
    options.ListenAnyIP(grpcPort, listenOptions => listenOptions.Protocols = HttpProtocols.Http2);
});

builder.Services.AddExecutionApiModule();

var app = builder.Build();
app.UseExecutionApiModule();

app.Logger.KestrelLimitsConfigured(
    app.Configuration.GetValue<int>("Kestrel:Limits:MaxRequestHeadersTotalSize", 65_536),
    app.Configuration.GetValue<int>("Kestrel:Limits:MaxRequestHeaderCount", 200));

await app.RunAsync();
