# Custom Script Helpers Demo (component model)

A standalone, runnable proof-of-concept for letting **consuming developers ship their own
C# helper classes as components** — uploaded as `.csx` files exactly like flows / tasks / views —
**referenced from a flow definition's mapping**, compiled at runtime, and **sandboxed**.

Execution contract: **build the referenced helper classes first → then compile and run the
mapping against them.**

It is intentionally decoupled from the main runtime: its own `.csproj`, not in any `.sln`,
imports nothing from `common.props`. It mirrors the real scripting pipeline in miniature.

## Run it

```bash
cd samples/CustomScriptHelpersDemo
dotnet run -c Release
```

Output:

```
Flow 'order-flow' v1.0.0 — transition 'submit-order'
  mapping  : mappings/order-mapping.csx
  helpers  : tax-calculator, rsa-crypto

[1] Helper set built (compiled). Namespaces auto-imported: Acme.Helpers
[2] Mapping compiled + services injected.
[3] Running mapping (netAmount = 100) ...
      [INFO] OrderMapping: Pricing order on transition 'submit-order' (currency EUR)
    Result:
      net = 100
      tax = 18,00
      gross = 118,00
      currency = EUR
      encryptedCard = jE7JobX8kTA08TGaWta6ANER… (344 chars)
      cardRoundTripOk = True

[4] Second run helper set: served from cache ✓
[5] Attempting to build a malicious helper component (System.IO.File) ...
    BLOCKED as expected:
      - evil.csx(14): banned namespace 'System.IO' via 'System.IO.File.ReadAllText(string)'
```

## The component model

```
components/
  flows/order-flow.json          flow definition; its transition mapping REFERENCES helper keys
  helpers/tax-calculator.csx      helper component  (key: "tax-calculator")
  helpers/rsa-crypto.csx          helper component  (key: "rsa-crypto", host supplies the RSA key)
  mappings/order-mapping.csx      mapping script; inherits ScriptBase, calls the helpers
  helpers-malicious/evil.csx      blocked by the sandbox
```

The flow declares which helpers a mapping needs:

```jsonc
// components/flows/order-flow.json
"mapping": {
  "helpers":           [ "tax-calculator", "rsa-crypto", "order-summary" ], // helper component keys
  "allowedAssemblies": [ "System.Security.Cryptography" ],                  // per-mapping sandbox grant
  "location":          "mappings/order-mapping.csx"
}
```

`allowedAssemblies` is **dynamic, per-mapping**: it is merged on top of the global baseline
([`SandboxOptions.AllowedAssemblies`](Engine/SandboxOptions.cs)). In this demo crypto is *not* in the
baseline, so the RSA helper compiles **only** because the flow grants it — step [4b] shows the same
helper being blocked (CS1069) when the grant is removed.

The engine ([`ScriptComponentEngine`](Engine/ScriptComponentEngine.cs)) then:

1. **Builds the helper set first** — `GetOrBuildHelpers(...)` compiles the referenced helper
   components into one assembly, **cached by content hash** (step 4 shows the cache hit).
2. **Compiles the mapping** — `BuildMapping(...)` references that helper assembly and auto-imports
   its namespaces, into the **same** collectible `AssemblyLoadContext` so calls resolve at runtime.
3. **Runs** the mapping with services injected (config + logging), exactly like the runtime.

## The two-layer sandbox

`.NET has no in-process security boundary`, so the gate is applied **at compile time**:

1. **Reference allow-list** — [`SandboxedReferenceSet`](Engine/SandboxedReferenceSet.cs) filters the
   runtime's Trusted Platform Assemblies down to the **global baseline**
   ([`SandboxOptions.AllowedAssemblies`](Engine/SandboxOptions.cs)) **plus the per-mapping
   `allowedAssemblies` grant** from the flow. Whole assemblies (e.g. `System.Net.Http`) are not
   referenced, so `HttpClient` won't compile. Crypto is enabled per-mapping via the flow's grant.
2. **Banned-API analyzer** — [`BannedApiAnalyzer`](Engine/BannedApiAnalyzer.cs) resolves every symbol
   against the semantic model and rejects banned namespaces (`System.IO`, `System.Diagnostics`, …),
   `DllImport`, and `unsafe`. Required because dangerous types like `System.IO.File` live in the
   *mandatory* `System.Private.CoreLib` and can't be blocked by reference omission alone.

> ⚠️ Strong **best-effort gate for same-org domain teams**, not a hard security boundary.
> A real boundary requires process/container isolation.

## Keys are host-owned

`rsa-crypto.csx` takes the key as a parameter; it never generates or stores keys. `Program.cs`
creates the RSA pair and passes the Base64 public/private key to scripts via config
(`GetConfig("rsa:publicKey")`). In the runtime these come from the secret store via
`ScriptBase.GetSecret(...)`.

## How this maps to the real runtime

| Demo piece | Real integration point |
|------------|------------------------|
| `helpers/*.csx` components | A new helper/script-library **component type**, stored & versioned like flows/tasks/views in the component cache store. |
| `mapping.helpers[]` in the flow JSON | New field on the transition mapping referencing helper component keys (+ version). |
| `ScriptComponentEngine.GetOrBuildHelpers` | Helper-set compile, cached by content hash, in a sandboxed path alongside [`CSharpEvaluator`](../../modules/BBT.Workflow.Modules.Scripting/BBT/Workflow/Scripting/Evaluators/CSharpEvaluator.cs). |
| `BuildMapping` (refs + auto-using, shared ALC) | Merged in [`ScriptEngine.CompileToInstanceAsync`](../../src/BBT.Workflow.Application/Scripting/ScriptEngine.cs). |
| sandbox (`SandboxOptions` + analyzer) | New sandbox policy; also tightens `CSharpEvaluator.CreateDefaultReferences`, which today references the *entire* AppDomain. |

See [IMPLEMENTATION.md](./IMPLEMENTATION.md) for the full issue-ready spec.
