# Implementation Guide — Custom Script Helpers (sandboxed, component-referenced)

> Issue-ready spec. Lets a consuming developer ship their own C# helper classes as **components**
> (uploaded as `.csx`, just like flows/tasks/views), **reference them from a flow definition's
> mapping**, and have the runtime **build the helper classes first, then compile + run the mapping**
> against them — sandboxed, cached by content hash.
>
> A working, runnable proof-of-concept lives in [`samples/CustomScriptHelpersDemo`](./README.md).
> This document explains how to land the same design inside the real runtime.

## 1. Goal & decisions

| Decision | Choice | Reason |
|----------|--------|--------|
| Delivery | **Helper = component (`.csx`), source-compiled at runtime** (not precompiled DLL) | Stored/versioned like every other component; the only delivery we can actually sandbox since we own the Roslyn compile. |
| Wiring | **Referenced from the transition mapping** (`mapping.helpers[]`) | A mapping declares exactly which helpers it needs; the engine builds that set, then the mapping. Explicit, per-flow, no global state. |
| Scope | **Referenced helpers only** | Custom code becomes referenceable types + auto-`using`. `ScriptBase` stays runtime-owned. |
| Trust | **Restricted / sandboxed (best-effort)** | Two-layer compile-time gate: reference allow-list + banned-API analyzer. Not a hard boundary (see §9). |

**Consumer experience:** upload helper components and reference them from the flow mapping:

```jsonc
// flow definition — transition mapping
"mapping": {
  "helpers":           [ "tax-calculator", "rsa-crypto" ], // helper component keys (+ version)
  "allowedAssemblies": [ "System.Security.Cryptography" ], // per-mapping sandbox grant (dynamic)
  "location":          "mappings/order-mapping.csx"
}
```

`allowedAssemblies` makes the reference allow-list **dynamic per-mapping** — merged on top of the
global baseline (`Scripting:Sandbox:AllowedAssemblies`). A flow grants only what its helpers need
(e.g. crypto) without widening the baseline for everyone.
At transition time the runtime builds the referenced helper set (cached), then compiles + runs the
mapping with those helpers referenced and their namespaces auto-imported. No base-image rebuild,
no startup folder — helpers travel with the domain like any other component.

## 2. Affected components

| Layer | File / location | Change |
|-------|-----------------|--------|
| Scripting module | `modules/BBT.Workflow.Modules.Scripting/BBT/Workflow/Scripting/` | New `Sandbox/` + `Helpers/` folders (options, reference set, analyzer, registry). Extend `IEvaluator`. |
| Domain / definitions | transition mapping definition + schema | New helper component type + `helpers: string[]` on the mapping. |
| Application | [`src/BBT.Workflow.Application/Scripting/ScriptEngine.cs`](../../src/BBT.Workflow.Application/Scripting/ScriptEngine.cs) + mapping compile callers | Resolve referenced helpers, build the set, merge reference + namespaces into the compile. |
| DI | [`TaskServiceCollectionExtensions.AddScriptingServices`](../../src/BBT.Workflow.Application/Microsoft/Extensions/DependencyInjection/TaskServiceCollectionExtensions.cs#L218) | Register options + singleton `IScriptHelperRegistry`. |
| Logging | [`BBT.Workflow.Domain/Logging/WorkflowLogs.cs`](../../src/BBT.Workflow.Domain/Logging/WorkflowLogs.cs) | New `LoggerMessage` entries (60xxx range, scripting). |
| Config | `appsettings.json` of both hosts | New `Scripting:Helpers` + `Scripting:Sandbox` sections. |
| Meta / CLI | `vnext-meta/features.json`, `vnext-template` | Advertise the capability + flag; validate the new component type. |
| Docs | `/docs` + `/ai-docs` | Consumer-facing how-to. |

## 3. Step-by-step

### 3.1 Sandbox options (bind from config)
`modules/.../Scripting/Sandbox/ScriptSandboxOptions.cs`
```csharp
public sealed class ScriptSandboxOptions
{
    public bool Enabled { get; set; } = true;
    public HashSet<string> AllowedAssemblies { get; set; } = new(StringComparer.OrdinalIgnoreCase) { /* curated */ };
    public HashSet<string> BannedNamespaces { get; set; } = new(StringComparer.Ordinal)
        { "System.IO", "System.Net", "System.Diagnostics", "System.Runtime.InteropServices", "System.Reflection", "Microsoft.Win32" };
    public bool AllowUnsafe { get; set; }
}

public sealed class ScriptHelpersOptions
{
    public bool Enabled { get; set; } // master switch for the helper-reference feature
}
```
Port `SandboxOptions`, `SandboxedReferenceSet`, `BannedApiAnalyzer` from the sample verbatim
(see [`Engine/`](./Engine/)). `SandboxedReferenceSet.Build(options, extraAllowed)` filters the TPA list
down to `AllowedAssemblies` **∪ the per-mapping grant**; `BannedApiAnalyzer` resolves symbols on the
semantic model and rejects banned namespaces / `DllImport` / `unsafe`.

> **Governance:** the per-mapping `allowedAssemblies` grant should be bounded by a global
> **`GrantableAssemblies` ceiling** (config), so a flow author cannot grant arbitrary assemblies
> (e.g. `System.Net.Http`). Effective set = `baseline ∪ (mappingGrant ∩ grantable)`. The
> `BannedNamespaces` analyzer is **not** bypassable by a grant — it always runs.

> **Why two layers:** reference omission blocks whole assemblies (e.g. `System.Net.Http`), but dangerous
> types like `System.IO.File` live in the *mandatory* `System.Private.CoreLib` — only the semantic
> analyzer can block those.

### 3.2 Extend `IEvaluator` for sandbox + multi-source
[`IEvaluator`](../../modules/BBT.Workflow.Modules.Scripting/BBT/Workflow/Scripting/Evaluators/IEvaluator.cs)
today compiles a single string and references the **entire AppDomain** via
[`CSharpEvaluator.CreateDefaultReferences`](../../modules/BBT.Workflow.Modules.Scripting/BBT/Workflow/Scripting/Evaluators/CSharpEvaluator.cs#L212) —
the opposite of sandboxed. Add a sandboxed path:

```csharp
// IEvaluator additions
CompiledHelpers CompileHelpers(IReadOnlyList<(string Path, string Code)> sources, CancellationToken ct = default);

public sealed record CompiledHelpers(MetadataReference Reference, string[] Namespaces, AssemblyLoadContext Context);
```
In `CSharpEvaluator`:
- When `ScriptSandboxOptions.Enabled`, build references from `SandboxedReferenceSet` (not the AppDomain
  scan) and run `BannedApiAnalyzer.Analyze(compilation)` **before** `compilation.Emit`. On violations,
  throw a `ScriptCompilationException` listing them.
- `CompileHelpers` compiles a *set* of helper component sources into one assembly (so they can reference
  each other), loads it into the shared collectible `AssemblyLoadContext`, and returns the image as a
  `MetadataReference` + discovered public namespaces.
- **Mapping scripts must be loaded into the same `AssemblyLoadContext` as the helpers** so their calls
  resolve at runtime (the sample does this in `ScriptComponentEngine`). Thread that ALC through
  `CompileToInstanceAsync`.

### 3.3 Helper component type + resolver
- Introduce a **helper/script-library component type** stored & versioned like flows/tasks/views in the
  component cache store (`IComponentCacheStore`). A helper component is just a `.csx` class body.
- Add `helpers: string[]` (component keys, with version) to the **transition mapping** definition.
- Build a `ScriptHelperRegistry` (port [`Engine/ScriptComponentEngine.cs`](./Engine/ScriptComponentEngine.cs)):
  `GetOrBuildHelpers(IReadOnlyList<HelperComponent>)` compiles the referenced set into one assembly,
  **cached by content hash** (key = hash of ordered key+code), and returns
  `MetadataReference` + `Namespaces`. Recommend a singleton registry with a shared collectible ALC.
- Cache invalidation: a helper component's content hash changes on edit ⇒ new cache entry; reuse the
  existing collectible-ALC discipline to unload stale sets.

### 3.4 Wire into the mapping compile path
Where a transition's mapping is compiled (the task executors / `FunctionAppService` that call
[`ScriptEngine.CompileToInstanceAsync`](../../src/BBT.Workflow.Application/Scripting/ScriptEngine.cs#L100)):
1. Resolve the mapping's `helpers[]` keys from the component store → `HelperComponent` list.
2. Compute the effective grant: `mapping.allowedAssemblies ∩ Sandbox.GrantableAssemblies`.
3. `var set = registry.GetOrBuildHelpers(components, grant);`  ← **build helper classes first**.
   The grant is part of the compilation identity → fold it into the helper-set cache key.
4. Pass `set.Reference` + `set.Namespaces` + the same `grant` into the mapping compile (merge as below)
   **and** compile the mapping into `set` 's ALC so helper calls resolve.
```csharp
var mergedReferences = (extraReferences ?? [])
    .Concat(DefaultReferences.Value)
    .Concat(set is null ? [] : [set.Reference])
    .Distinct();

var mergedUsings = (usingDirectives ?? [])
    .Concat(DefaultUsings)
    .Concat(set?.Namespaces ?? [])         // auto-import helper namespaces
    .Distinct();

// references built from baseline ∪ grant (SandboxedReferenceSet.Build(options, grant))
```

### 3.5 DI
In [`AddScriptingServices`](../../src/BBT.Workflow.Application/Microsoft/Extensions/DependencyInjection/TaskServiceCollectionExtensions.cs#L218):
```csharp
services.AddOptions<ScriptHelpersOptions>().BindConfiguration("Scripting:Helpers");
services.AddOptions<ScriptSandboxOptions>().BindConfiguration("Scripting:Sandbox");
services.TryAddSingleton<IScriptHelperRegistry, ScriptHelperRegistry>(); // shared cache + ALC
```
Keep `IScriptServices` **scoped** (per-request Dapr/Logger/Config). The `ScriptHelperRegistry` is a
**singleton** — helper sets are process-wide artifacts compiled once and reused across requests.

## 4. Configuration

```json
"Scripting": {
  "Helpers": { "Enabled": true },
  "Sandbox": {
    "Enabled": true,
    "AllowedAssemblies":   [ "System.Private.CoreLib", "System.Runtime", "System.Collections", "System.Linq", "System.Text.RegularExpressions", "netstandard" ],
    "GrantableAssemblies": [ "System.Security.Cryptography", "System.Text.Json" ],
    "BannedNamespaces":    [ "System.IO", "System.Net", "System.Diagnostics", "System.Runtime.InteropServices", "System.Reflection", "Microsoft.Win32" ],
    "AllowUnsafe": false
  }
}
```
- `AllowedAssemblies` — global baseline every mapping gets.
- `GrantableAssemblies` — the **ceiling** a flow's `mapping.allowedAssemblies` may draw from; a grant
  outside this set is ignored (and should be logged/validated). Put crypto here so flows opt in per-mapping.
- Default `Helpers.Enabled = false` so existing deployments are unaffected until opted in.

## 5. Delivery (no Docker change)

Helpers travel with the **domain as components**, not in the image. Consumers author `*.csx` helper
components and reference them from the transition mapping (`mapping.helpers[]`). No base-image rebuild
and no startup folder/volume — they deploy through the same component pipeline as flows/tasks/views.
Validate the new component type in the `vnext-template` CLI (`npm run validate`).

## 6. Caching & memory

- A helper **set** is compiled **once per content hash** and cached in `ScriptHelperRegistry`; first
  transition that needs it builds it, the rest reuse it (the sample's step 4 shows the cache hit).
- Editing a helper component changes its hash ⇒ a new set is built; unload stale sets via the
  collectible-ALC discipline ([`ScriptAssemblyLoadContext`](../../modules/BBT.Workflow.Modules.Scripting/BBT/Workflow/Scripting/Evaluators/ScriptAssemblyLoadContext.cs)).
- The existing per-script cache key in `CSharpEvaluator.GenerateCacheKey` already incorporates
  references; confirm the helper `MetadataReference.Display` is stable so mapping cache keys stay
  deterministic, **or** fold the helper-set hash into the mapping cache key.

## 7. Testing (`test/BBT.Workflow.Application.Tests/Scripting/`)

- `HelperRegistry_Compiles_Set_And_Exposes_Namespaces`
- `HelperRegistry_Caches_By_Content_Hash` (second build is served from cache; edit → rebuild)
- `Mapping_Can_Call_Referenced_Helper` (end-to-end, mirrors the sample's step 3)
- `Sandbox_Blocks_Banned_Namespace` — helper using `System.IO.File` → `ScriptCompilationException`
- `Sandbox_Blocks_DllImport` and `Sandbox_Blocks_Unsafe`
- `Crypto_Helper_Compiles_When_Cryptography_Allowed` (RSA round-trip)
- `Helpers_Disabled_NoBehaviorChange` — `Enabled=false` leaves existing compilation untouched
- Reuse patterns from [`ScriptEngineTests`](../../test/BBT.Workflow.Application.Tests/Scripting/ScriptEngineTests.cs).

## 8. vnext-meta

- `features.json`: add a `custom-script-helpers` feature entry with status + the `Scripting:Helpers` flag.
- Run the **vnext-meta-validator** skill after editing.

## 9. Security note (must be in the issue)

This is a **strong best-effort compile-time gate for same-org domain teams**, **not** a hard security
boundary. .NET has no in-process sandbox; a determined insider can still reach loaded types via
reflection-style tricks even with the analyzer. If a true boundary is required, isolate at the
process/container level. State this explicitly so the trust model is not over-sold.

## 10. Acceptance criteria

- [ ] A mapping referencing `helpers: ["..."]` builds the helper set first, then compiles + runs the
      mapping; the mapping calls helper types with no extra `using`.
- [ ] Helper set is cached by content hash — a second transition reuses it; editing a helper rebuilds it.
- [ ] Reference allow-list blocks an assembly-level API (e.g. `HttpClient`) at compile time.
- [ ] Banned-namespace analyzer blocks `System.IO.File` (CoreLib-resident) at compile time.
- [ ] `DllImport` and `unsafe` are rejected.
- [ ] A mapping's `allowedAssemblies` grant broadens the reference set per-mapping; removing it blocks helpers that need it (CS1069), and a grant outside `GrantableAssemblies` is ignored + logged.
- [ ] RSA helper compiles only when crypto is granted (per-mapping); keys are host/secret-store supplied, never embedded.
- [ ] `Helpers.Enabled=false` ⇒ byte-for-byte unchanged behaviour.
- [ ] A helper that fails the sandbox surfaces a clear, logged error (`WorkflowLogs`) and fails the transition compile — never silently skipped.
- [ ] Helper sets are collectible (ALC) and unloaded when superseded.
- [ ] Tests in §7 pass; `vnext-meta` validates; docs added to `/docs` + `/ai-docs`.

## 11. Task checklist

- [ ] `ScriptSandboxOptions`, `ScriptHelpersOptions`
- [ ] `SandboxedReferenceSet`, `BannedApiAnalyzer` (port from sample)
- [ ] Extend `IEvaluator` / `CSharpEvaluator` (sandboxed multi-source compile + shared ALC)
- [ ] New **helper component type** + `helpers[]` field on the transition mapping + schema/validation
- [ ] `IScriptHelperRegistry` + `ScriptHelperRegistry` (content-hash cache, shared collectible ALC)
- [ ] Resolve referenced helpers and merge into the mapping compile path (§3.4)
- [ ] DI registration (singleton registry)
- [ ] `WorkflowLogs` entries
- [ ] appsettings sections (both hosts)
- [ ] Tests
- [ ] `vnext-meta/features.json` + validate; `vnext-template` CLI validation for the new component type
- [ ] `/docs` + `/ai-docs`
```
