using System.Diagnostics;
using System.Linq;
using BBT.Aether.AspNetCore.Dapr;
using BBT.Aether.AspNetCore.Threads;
using BBT.Workflow.HttpApi.Shared.Telemetry;
using BBT.Workflow.Logging;
using Dapr.Client;
using Dapr.Extensions.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;

ThreadPoolHelper.ConfigureThreadPool();

// PROCESS-GLOBAL MUTATION, deliberately here in Program.cs rather than hidden inside
// AddExecutionApiModule(): replacing DistributedContextPropagator.Current changes how EVERY
// inbound request in this process is parented, so the next reader should meet it at the top of
// the entry point, not discover it three DI extensions deep.
//
// Works around a Dapr defect. On the gRPC proxy-mode hop (ExecutionApi:Transport = grpc on the
// orchestration side + --app-protocol grpc on this app's sidecar), the callee sidecar's app-bound
// AppCallback hop re-issues its own gRPC call to the app and APPENDS its own span to the incoming
// traceparent instead of replacing it, so the app receives two comma-joined values sharing one
// trace id. That is invalid per W3C, ASP.NET Core correctly discards it, and every task
// invocation ends up split across two disconnected traces. The propagator runs before hosting
// starts the request Activity -- and Activity.ParentId is immutable once started -- so this is
// the only seam from which the trace can still be made whole.
//
// UNCONDITIONAL, not gated on Kestrel:GrpcPort / the transport flag, for two reasons: the
// transport is chosen by the ORCHESTRATION side's config, which this process cannot see (so a
// local gate would be guessing), and the decorator is a strict no-op for well-formed input -- a
// single valid traceparent is delegated to the inner propagator untouched. Gating it would add a
// config coupling that buys nothing.
//
// MUST stay ABOVE WebApplication.CreateBuilder: ASP.NET Core's web-host bootstrap captures
// DistributedContextPropagator.Current into DI as a singleton INSTANCE (TryAddSingleton) while the
// builder is being constructed, and hosting resolves the propagator from there. Assigning after
// CreateBuilder leaves the captured instance -- and therefore request parenting -- unchanged.
//
// See docs/runtime/dapr-invocation-transport.md for the captured header, the investigation, and
// the live before/after trace evidence.
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
    var httpBindings = ResolveHttpBindings(builder.Configuration).ToList();
    var grpcPort = builder.Configuration.GetValue<int>("Kestrel:GrpcPort", 4212);

    // Fail fast on a same-port collision between the HTTP/1.1 and gRPC (HTTP/2-only h2c)
    // endpoints, rather than letting Kestrel throw an opaque address-in-use IOException
    // (or, worse, silently binding one protocol and dropping the other) at StartAsync.
    // This is a real, not hypothetical, misconfiguration: Kestrel:GrpcPort and the HTTP
    // port both ultimately come from operator-controlled config (Helm values / env vars /
    // launchSettings) with no shared source of truth enforcing they differ -- e.g. Helm's
    // execution.dapr.appPort (which does NOT feed Kestrel:GrpcPort -- see
    // charts/vnext/values.yaml's execution.grpcPort for why they're separate keys) has
    // already collided with the HTTP port here once. A clear exception naming both ports
    // turns a crash-loop with a confusing low-level error into an immediate, actionable
    // one.
    if (httpBindings.Any(b => b.Port == grpcPort))
    {
        throw new InvalidOperationException(
            $"Execution's Kestrel HTTP/1.1 port ({grpcPort}, from {WebHostDefaults.ServerUrlsKey}) " +
            $"and its gRPC (HTTP/2-only h2c) port (Kestrel:GrpcPort, also {grpcPort}) resolve to the " +
            $"SAME port. Kestrel cannot bind both Http1 and Http2 protocols on one cleartext port -- " +
            $"give Kestrel:GrpcPort a value different from the app's HTTP hosting URL port.");
    }

    foreach (var (httpPort, loopbackOnly) in httpBindings)
    {
        if (loopbackOnly)
        {
            // "localhost" / "127.0.0.1" in the hosting URL means "bind loopback only" --
            // matches what ASP.NET Core's own URL-based binding (the behavior this loop
            // replaces) has always done for those hosts. Widening this to all interfaces
            // silently would change a dev/local-only binding's exposure.
            options.ListenLocalhost(httpPort, listenOptions => listenOptions.Protocols = HttpProtocols.Http1);
        }
        else
        {
            options.ListenAnyIP(httpPort, listenOptions => listenOptions.Protocols = HttpProtocols.Http1);
        }
    }

    options.ListenAnyIP(grpcPort, listenOptions => listenOptions.Protocols = HttpProtocols.Http2);
});

builder.Services.AddExecutionApiModule();

var app = builder.Build();
app.UseExecutionApiModule();

app.Logger.KestrelLimitsConfigured(
    app.Configuration.GetValue<int>("Kestrel:Limits:MaxRequestHeadersTotalSize", 65_536),
    app.Configuration.GetValue<int>("Kestrel:Limits:MaxRequestHeaderCount", 200));

await app.RunAsync();

// Parses the Kestrel HTTP/1.1 endpoint(s) out of the hosting-URL configuration (ASPNETCORE_URLS /
// --urls / launchSettings' applicationUrl -- all of which land in configuration under
// WebHostDefaults.ServerUrlsKey). Handles a single URL or a semicolon-separated list, "+" / "*" /
// an explicit host, and falls back to the pre-existing default (4202, all interfaces) when
// nothing is configured, so a deployment that sets no hosting URL at all keeps working exactly
// as before.
//
// Each result is (Port, LoopbackOnly): LoopbackOnly is true only for "localhost" / "127.0.0.1"
// hosts, false for "+" / "*" / "0.0.0.0", any other host, and the no-URL-configured default --
// the caller binds loopback-only ones with ListenLocalhost and the rest with ListenAnyIP,
// matching what ASP.NET Core's own URL-based binding has always done for those hosts. Before
// this function existed, ListenAnyIP was used unconditionally, which silently widened e.g.
// "http://localhost:4202" from loopback-only to all interfaces.
//
// Scheme is NOT silently discarded. "https://" is rejected with a clear, fail-fast exception
// naming the offending URL rather than being bound as cleartext HTTP/1.1 -- the repo's own
// `https` launch profile (Properties/launchSettings.json, https://localhost:7389) would
// otherwise have a TLS-advertised port silently downgraded to plaintext. This app does not
// currently provision a server certificate for Kestrel, so there's no correct way to honor
// "https://" here yet; failing fast surfaces that gap immediately at startup instead of serving
// TLS-looking traffic in the clear.
static IEnumerable<(int Port, bool LoopbackOnly)> ResolveHttpBindings(IConfiguration configuration)
{
    const int defaultPort = 4202;

    var urls = configuration[WebHostDefaults.ServerUrlsKey];
    if (string.IsNullOrWhiteSpace(urls))
    {
        return [(defaultPort, LoopbackOnly: false)];
    }

    var bindings = new List<(int Port, bool LoopbackOnly)>();
    foreach (var rawUrl in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        if (rawUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Execution's Kestrel HTTP/1.1 endpoint resolver does not support 'https://' URLs " +
                $"(offending URL: '{rawUrl}', from {WebHostDefaults.ServerUrlsKey}). Binding a TLS-scheme " +
                $"URL as cleartext HTTP/1.1 would silently downgrade it. Use an 'http://' URL, or add " +
                $"proper TLS/certificate configuration to Program.cs before using 'https://' here.");
        }

        // Uri doesn't accept "+" or "*" as a host (ASP.NET Core's own wildcard bindings), so
        // normalize them to a parseable placeholder for Uri.TryCreate. This only affects "+" /
        // "*" themselves -- both map to "0.0.0.0" below, which is correctly LoopbackOnly=false
        // either way -- "localhost" and "127.0.0.1" pass through unchanged, so the
        // loopback-vs-any decision after parsing still reflects the URL's real host.
        var parseable = rawUrl
            .Replace("://+", "://0.0.0.0", StringComparison.Ordinal)
            .Replace("://*", "://0.0.0.0", StringComparison.Ordinal);

        if (!Uri.TryCreate(parseable, UriKind.Absolute, out var parsedUrl))
        {
            continue;
        }

        var loopbackOnly = parsedUrl.Host is "localhost" or "127.0.0.1";
        var binding = (Port: parsedUrl.Port, LoopbackOnly: loopbackOnly);
        if (!bindings.Contains(binding))
        {
            bindings.Add(binding);
        }
    }

    return bindings.Count > 0 ? bindings : [(defaultPort, LoopbackOnly: false)];
}
