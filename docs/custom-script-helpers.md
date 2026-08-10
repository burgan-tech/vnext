# Custom Script Helpers (sandboxed, component-referenced)

> Status: **experimental**, available `>= 0.0.58`. Disabled by default.
> This is a strong best-effort **compile-time** gate for same-org domain teams — **not** a hard
> security boundary. .NET has no in-process sandbox; for a true boundary isolate at the
> process/container level.

Domain teams can ship reusable C# helper classes as **components** and reference them from a
transition mapping. At transition time the runtime builds the referenced helper set first (sandboxed,
cached by content hash), then compiles and runs the mapping against it with the helper namespaces
auto-imported.

## 1. Author a helper component (`sys-mappings`)

A helper is a `Mapping` component published to the new system flow `sys-mappings`, just like
flows/tasks/views. Its body is a `.csx` class library snippet.

```jsonc
// component: sys-mappings / core / tax-calculator @ 1.0.0
{
  "key": "tax-calculator",
  "flow": "sys-mappings",
  "domain": "core",
  "version": "1.0.0",
  "name": "Tax calculator helpers",
  "code": "namespace Helpers { public static class TaxCalc { public static decimal Tax(decimal x) => x * 0.18m; } }",
  "encoding": "NAT"   // NAT (native/plain) or B64 (base64)
}
```

Publish it through the normal component pipeline (`PublishAsync` / the Publish flow). The
`MappingComponentValidator` checks that `name`/`code` are present and the code decodes; with the
sandbox enabled, banned APIs are rejected at compile time.

## 2. Reference helpers from a transition mapping

Helpers and the per-compile sandbox grant live under a `scripts` object on the mapping. `helpers[]`
uses the **same reference shape** as every other component reference (`key`/`version`/`domain`/`flow`).
The whole `scripts` object is **optional** — omit it and behaviour is identical to before.

```jsonc
"mapping": {
  "location": "mappings/order-mapping.csx",
  "code": "...",                 // the mapping body (base64 or native per "encoding")
  "encoding": "B64",
  "scripts": {
    "helpers": [
      { "key": "tax-calculator", "version": "1.0.0", "domain": "core", "flow": "sys-mappings" }
    ],
    "allowedAssemblies": [ "System.Security.Cryptography" ]   // per-mapping sandbox grant (optional)
  }
}
```

Inside the mapping you call helper types directly — their namespaces are auto-imported:

```csharp
public class Mapping : ScriptBase, ITransitionMapping
{
    public Task<dynamic> Handler(ScriptContext ctx)
        => Task.FromResult<dynamic>(new { tax = TaxCalc.Tax(100m) });
}
```

### Flow-level `scripts` (global to the workflow)

A workflow definition may declare a top-level `scripts` object. It is **global to the flow** and is
**unioned** with every mapping's `scripts` at compile time (helper references concatenated + deduped by
`domain/flow/key/version`; allowed assemblies distinct-merged). Use it to grant a helper/assembly once
for the whole flow instead of repeating it on each mapping.

```jsonc
// workflow definition (top level)
"scripts": {
  "helpers": [ { "key": "json-helper", "version": "1.0.0", "domain": "core", "flow": "sys-mappings" } ],
  "allowedAssemblies": [ "System.Security.Cryptography" ]
}
```

### `encoding: "REF"` — run a sys-mappings component directly

When `encoding` is `REF`, the mapping `code` is **not a string** but a `Reference` to a `sys-mappings`
component. The runtime resolves it from the component store and runs that component's body (which is
plain `NAT`/`B64` — no REF chaining). This lets tasks reuse published mapping definitions.

```jsonc
"mapping": {
  "code": { "key": "json-helper", "version": "1.0.0", "domain": "core", "flow": "sys-mappings" },
  "encoding": "REF"
}
```

### Every script slot must resolve to a body

`location` is authoring metadata only — the runtime never reads the `.csx` file it names. The body is
inlined into `code` by the domain build step, so a slot published with **only** a `location` has no body
at all, and nothing downstream is able to say so: the encoding defaults to `B64`, empty Base64 decodes
without error, and `HasMappingCode` turns false. A bodyless *mapping* is then silently skipped (the HTTP
task fires with no body mapping, a script task returns null), while a bodyless *rule* blows up
mid-transition when Roslyn compiles an empty script.

`ScriptCodeValidator` closes that gap at publish time for every slot a definition can carry — transition
`timer`/`rule`/`mapping`, `onExecutionTasks[].mapping`, state `onEntries`/`onExits`/`notifications`,
`subFlow.mapping`, view- and schema-selection `rule`s, workflow and function `output`, function
`cache.keyExpression`/`generationKeyExpression`, and `CacheAsideTask`'s `sourceMapping`/`keyExpression`.
Matching the `vnext-schema` guard:

| Slot | Verdict |
|------|---------|
| `{ "location": "./src/X.csx" }` | ✗ no `code` — never executed |
| `{ "type": "L", "location": "./x.csx" }` | ✗ same, `L` is the default type |
| `{ "type": "G", "location": "./x.csx" }` | ✓ `G` declares the body lives elsewhere |
| `{ "location": "./x.csx", "code": "<base64>" }` | ✓ |
| `code` present but not decodable under `encoding` | ✗ |
| `code` decoding to whitespace | ✗ |
| `encoding: "REF"` without a `sys-mappings` reference (key/domain/version) | ✗ unresolvable at compile time |

When a definition object gains a new `ScriptCode` property, add its path to the traversal in
`WorkflowValidator.ValidateWorkflowScriptCodes` / `ValidateTransitionScriptCodes` (or the matching
component validator) — an unlisted slot is an unguarded slot.

One deliberate exception: a transition `rule` whose `location` is `dynamicExpresso` is an inline boolean
expression, not a `.csx` body, so `ValidateDynamicExpressoRule` owns its emptiness check and reports it
in Dynamic Expresso terms instead.

## 3. Configuration

```json
"Scripting": {
  "Helpers": { "Enabled": false },
  "Sandbox": {
    "Enabled": false,
    "AllowUnsafe": false,
    "PluginDirectory": "/app/assemblies",
    "AllowedAssemblies": [ "System.Private.CoreLib", "System.Runtime", "System.Collections", "System.Linq", "System.Linq.Expressions", "System.Text.RegularExpressions", "Microsoft.CSharp", "netstandard" ],
    "BannedNamespaces": []
  }
}
```

- **`Scripting:Helpers:Enabled`** — master switch for referencing helpers. A mapping that declares
  `helpers[]` while this is `false` fails to compile.
- **`Scripting:Sandbox:Enabled`** — when `true`, **all** mapping compiles use the restricted reference
  set + banned-API analyzer. Default `false` keeps existing behaviour byte-for-byte.
- **`AllowedAssemblies`** — global baseline of referenceable assemblies. A mapping's
  `allowedAssemblies` is merged on top of this for that compile only.
- **`BannedNamespaces`** — *adds* to the mandatory platform baseline; it cannot remove an entry.

### Mandatory banned namespaces (non-overridable)

`System.IO`, `System.Net`, `System.Net.Http`, `System.Diagnostics`, `System.Reflection`,
`System.Runtime.InteropServices`, `Microsoft.Win32`.

A mapping/helper compile may never do file IO, network/HTTP, process/diagnostics, reflection, native
interop, or registry access. `DllImport` and `unsafe` are rejected. `System.Threading` (and
`System.Threading.Tasks`) are **allowed** — threading/synchronization primitives and `Task`-based
async are available to mappings.

## 4. Third-party / NuGet assemblies (operator-curated only)

There is **no** per-flow DLL upload or nupkg download. An operator mounts approved DLLs into
`Scripting:Sandbox:PluginDirectory` (a Docker volume), allow-lists them in `AllowedAssemblies` (or a
flow grants them per-mapping). The runtime loads them dynamically into the helper-set load context.
The banned-namespace analyzer still runs on the script source.

## 5. Caching & memory

A helper set is compiled once per content hash (ordered key+version+code plus the per-mapping grant)
and reused across requests. Editing a helper changes its hash → a new set is built. Each set lives in
its own collectible `AssemblyLoadContext` and can be unloaded when superseded.
