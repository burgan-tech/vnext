using System.Security.Cryptography;
using CustomScriptHelpersDemo.Contracts;
using CustomScriptHelpersDemo.Engine;

var baseDir = AppContext.BaseDirectory;
string Path_(string rel) => System.IO.Path.Combine(baseDir, rel);

var sandbox = new SandboxOptions();
var compiler = new ScriptCompiler(sandbox);
var engine = new ScriptComponentEngine(compiler);
var store = new ComponentStore(Path_("components"));

// The HOST owns the RSA key pair and passes it to scripts as Base64 key material.
// In the real runtime this comes from the secret store, not generated here.
using var rsa = RSA.Create(2048);
var services = new DemoScriptServices(new Dictionary<string, string>
{
    ["currency"] = "EUR",
    ["rsa:publicKey"] = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo()),
    ["rsa:privateKey"] = Convert.ToBase64String(rsa.ExportPkcs8PrivateKey()),
});

Console.WriteLine("=== vNext custom script-helpers demo (component model) ===\n");

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

Console.WriteLine("\n=== done ===");
