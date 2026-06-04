using System.Security.Cryptography;
using CustomScriptHelpersDemo.Contracts;
using CustomScriptHelpersDemo.Engine;
using Microsoft.Extensions.Configuration;

var baseDir = AppContext.BaseDirectory;
string Path_(string rel) => System.IO.Path.Combine(baseDir, rel);

// All configuration lives in appsettings.json (Scripting:* sections).
var config = new ConfigurationBuilder()
    .SetBasePath(baseDir)
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

var helpersEnabled = config.GetValue("Scripting:Helpers:Enabled", true);

var sandbox = new SandboxOptions();
config.GetSection("Scripting:Sandbox").Bind(sandbox);

// Plugin dir: env var (Docker volume) wins, else appsettings; resolve relative → base dir.
var pluginDir = Environment.GetEnvironmentVariable("SCRIPT_PLUGIN_DIR");
if (string.IsNullOrWhiteSpace(pluginDir))
    pluginDir = sandbox.PluginDirectory;
sandbox.PluginDirectory = Path.IsPathRooted(pluginDir) ? pluginDir : Path_(pluginDir);

var compiler = new ScriptCompiler(sandbox);
var engine = new ScriptComponentEngine(compiler, sandbox);
var store = new ComponentStore(Path_("components"));

// Script services config comes from appsettings (Scripting:ScriptServices); the HOST adds the
// RSA key pair at runtime (in the real runtime these come from the secret store, not generated here).
var scriptConfig = config.GetSection("Scripting:ScriptServices")
    .GetChildren().ToDictionary(c => c.Key, c => c.Value ?? string.Empty);
using var rsa = RSA.Create(2048);
scriptConfig["rsa:publicKey"] = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
scriptConfig["rsa:privateKey"] = Convert.ToBase64String(rsa.ExportPkcs8PrivateKey());
var services = new DemoScriptServices(scriptConfig);

Console.WriteLine("=== vNext custom script-helpers demo (component model) ===\n");
Console.WriteLine($"Config: Helpers.Enabled={helpersEnabled}, AllowUnsafe={sandbox.AllowUnsafe}, " +
                  $"AllowedAssemblies={sandbox.AllowedAssemblies.Count}, BannedNamespaces={sandbox.BannedNamespaces.Count}\n");

if (!helpersEnabled)
{
    Console.WriteLine("Scripting:Helpers:Enabled is false — helper loading disabled. Exiting.");
    return;
}

// ---------------------------------------------------------------------------
// Read the flow definition and find the mapping reference for a transition.
// ---------------------------------------------------------------------------
var flow = store.LoadFlow("flows/order-flow.json");
var transition = flow.Transitions.First(t => t.Key == "submit-order");
var mappingRef = transition.Mapping;

var allowedAssemblies = mappingRef.AllowedAssemblies;

Console.WriteLine($"Flow '{flow.Key}' v{flow.Version} — transition '{transition.Key}'");
Console.WriteLine($"  mapping           : {mappingRef.Location}");
Console.WriteLine($"  helpers           : {string.Join(", ", mappingRef.Helpers)}");
Console.WriteLine($"  allowedAssemblies : {string.Join(", ", allowedAssemblies ?? [])}\n");

// ---------------------------------------------------------------------------
// [1] Build the referenced helper classes FIRST (cached by content hash + allow-list).
//     The per-mapping allowedAssemblies grant is what lets the RSA helper compile.
// ---------------------------------------------------------------------------
var helperComponents = mappingRef.Helpers.Select(store.Helper).ToList();
var (helpers, fromCache) = engine.GetOrBuildHelpers(helperComponents, allowedAssemblies);
Console.WriteLine($"[1] Helper set built ({(fromCache ? "from cache" : "compiled")}). " +
                  $"Namespaces auto-imported: {string.Join(", ", helpers.Namespaces)}");

// ---------------------------------------------------------------------------
// [2] THEN compile the mapping against the helper set and inject services.
// ---------------------------------------------------------------------------
var mappingComponent = store.Load(mappingRef.Location);
var mapping = engine.BuildMapping(mappingComponent, helpers, services, allowedAssemblies);
Console.WriteLine("[2] Mapping compiled + services injected.");

// ---------------------------------------------------------------------------
// [3] Run the mapping.
// ---------------------------------------------------------------------------
Console.WriteLine("[3] Running mapping (netAmount = 100) ...");
var response = await mapping.InputHandler(new ScriptContext
{
    TransitionKey = transition.Key,
    Data = new Dictionary<string, object?>
    {
        ["netAmount"] = 100m,
        ["cardNumber"] = "4111-1111-1111-1111",
    },
});

var data = (Dictionary<string, object?>)response.Data!;
Console.WriteLine("    Result:");
foreach (var kv in data)
{
    var value = kv is { Key: "encryptedCard", Value: string s } && s.Length > 24
        ? s[..24] + "… (" + s.Length + " chars)"
        : kv.Value;
    Console.WriteLine($"      {kv.Key} = {value}");
}

// ---------------------------------------------------------------------------
// [4] Re-running the same transition reuses the cached helper set.
// ---------------------------------------------------------------------------
var (_, fromCache2) = engine.GetOrBuildHelpers(helperComponents, allowedAssemblies);
Console.WriteLine($"\n[4] Second run helper set: {(fromCache2 ? "served from cache ✓" : "rebuilt ✗")}");

// ---------------------------------------------------------------------------
// [4b] Without the per-mapping grant, the same RSA helper no longer compiles —
//      System.Security.Cryptography is not in the global baseline.
// ---------------------------------------------------------------------------
Console.WriteLine("\n[4b] Building the RSA helper WITHOUT the allowedAssemblies grant ...");
try
{
    engine.GetOrBuildHelpers([store.Helper("rsa-crypto")]); // no extra allow-list
    Console.WriteLine("    !! ERROR: RSA helper compiled without the grant.");
}
catch (ScriptCompilationException ex)
{
    var first = ex.Message.Split('\n').FirstOrDefault(l => l.Contains("error CS")) ?? ex.Message.Split('\n')[0];
    Console.WriteLine("    BLOCKED as expected (assembly not allow-listed):");
    Console.WriteLine($"      {first.Trim()}");
}

// ---------------------------------------------------------------------------
// [5] A malicious helper component is rejected by the sandbox at build time.
// ---------------------------------------------------------------------------
Console.WriteLine("\n[5] Attempting to build a malicious helper component (System.IO.File) ...");
try
{
    engine.GetOrBuildHelpers([store.Load("helpers-malicious/evil.csx")]);
    Console.WriteLine("    !! ERROR: malicious helper was NOT blocked.");
}
catch (ScriptCompilationException ex)
{
    Console.WriteLine("    BLOCKED as expected:");
    foreach (var line in ex.Message.Split('\n'))
        Console.WriteLine($"      {line}");
}

// ---------------------------------------------------------------------------
// [6] Third-party assembly loaded DYNAMICALLY from the plugin volume.
//     Present only if a DLL was mounted/dropped into the plugin directory.
// ---------------------------------------------------------------------------
Console.WriteLine($"\n[6] Third-party assembly from the plugin volume ({sandbox.PluginDirectory}) ...");
if (File.Exists(Path.Combine(sandbox.PluginDirectory, "Newtonsoft.Json.dll")))
{
    // Newtonsoft.Json is in the baseline AllowedAssemblies — no per-mapping grant needed.
    var (jsonSet, _) = engine.GetOrBuildHelpers([store.Helper("json-helper")]);
    var jsonType = jsonSet.Assembly.GetType("Acme.Helpers.JsonHelper")!;
    var json = (string)jsonType.GetMethod("Serialize")!
        .Invoke(null, [new Dictionary<string, object?> { ["from"] = "plugin-volume", ["ok"] = true }])!;
    Console.WriteLine("    Newtonsoft.Json loaded dynamically (baseline-allowed, not a host dependency).");
    Console.WriteLine($"    JsonHelper.Serialize -> {json}");
}
else
{
    Console.WriteLine("    No third-party DLLs mounted. To enable this step:");
    Console.WriteLine("      • local : ./setup-plugins.sh   (copies Newtonsoft.Json.dll into ./plugins)");
    Console.WriteLine("      • docker: mount a volume to /app/assemblies (see docker-compose.yml)");
}

Console.WriteLine("\n=== done ===");
