using BBT.Aether.AspNetCore.Dapr;
using BBT.Aether.AspNetCore.Threads;
using BBT.Workflow.Logging;
using Dapr.Client;
using Dapr.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
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
    //
    // The HTTP port is deliberately NOT read from a bespoke "Kestrel:HttpPort" key. Registering
    // ANY code-based Listen*/UseKestrel endpoint makes Kestrel discard the hosting URLs wholesale
    // (logged at startup as "Overriding address(es) '...'. Binding to endpoints defined via
    // IConfiguration and/or UseKestrel() instead"), so a hardcoded second port key would silently
    // stop honoring ASPNETCORE_URLS/--urls -- the variable this platform has always used to
    // declare the app's HTTP port (Helm's vnext.commonEnvVars sets ASPNETCORE_URLS, launchSettings
    // sets applicationUrl) -- the moment an operator's value there ever differed from the key's
    // default. Instead we parse the port(s) back out of the hosting-URL configuration itself
    // (WebHostDefaults.ServerUrlsKey, i.e. the same "urls" value ASPNETCORE_URLS/--urls populate)
    // and bind those explicitly as Http1, so ASPNETCORE_URLS stays the single source of truth for
    // where the MVC/health/swagger surface listens. The gRPC port has no such pre-existing
    // variable -- nothing has ever declared "the app's own h2c listen port" before this second
    // endpoint existed (DAPR_GRPC_PORT is the sidecar's own API port the app calls OUT to, not a
    // port the app listens on; reusing it would collide with the sidecar) -- so Kestrel:GrpcPort
    // is genuinely new information and stays a real, standalone config key. Do not reintroduce
    // Kestrel:HttpPort as a mirror of the hosting URL.
    foreach (var httpPort in ResolveHttpPorts(builder.Configuration))
    {
        options.ListenAnyIP(httpPort, listenOptions => listenOptions.Protocols = HttpProtocols.Http1);
    }

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

// Parses the Kestrel HTTP/1.1 port(s) out of the hosting-URL configuration (ASPNETCORE_URLS /
// --urls / launchSettings' applicationUrl -- all of which land in configuration under
// WebHostDefaults.ServerUrlsKey). Handles a single URL or a semicolon-separated list, "+" / "*" /
// an explicit host, and falls back to the pre-existing default (4202) when nothing is configured,
// so a deployment that sets no hosting URL at all keeps working exactly as before.
static IEnumerable<int> ResolveHttpPorts(IConfiguration configuration)
{
    const int defaultPort = 4202;

    var urls = configuration[WebHostDefaults.ServerUrlsKey];
    if (string.IsNullOrWhiteSpace(urls))
    {
        return [defaultPort];
    }

    var ports = new List<int>();
    foreach (var rawUrl in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        // Uri doesn't accept "+" or "*" as a host (ASP.NET Core's own wildcard bindings), so
        // normalize them to a parseable placeholder -- only the port is used from the result.
        var parseable = rawUrl
            .Replace("://+", "://0.0.0.0", StringComparison.Ordinal)
            .Replace("://*", "://0.0.0.0", StringComparison.Ordinal);

        if (Uri.TryCreate(parseable, UriKind.Absolute, out var parsedUrl) && !ports.Contains(parsedUrl.Port))
        {
            ports.Add(parsedUrl.Port);
        }
    }

    return ports.Count > 0 ? ports : [defaultPort];
}
