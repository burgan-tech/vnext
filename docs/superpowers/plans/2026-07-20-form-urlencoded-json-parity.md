# Form URL-Encoded JSON Parity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Make Orchestration Start, Transition, and Function endpoints accept form-urlencoded bodies with deterministic JSON typing, safe bracket paths, unchanged JSON behavior, and accurate API metadata.

**Architecture:** Keep normalization in `FormUrlEncodedJsonElementInputFormatter`, but split key parsing, tree insertion, and scalar conversion into focused private helpers. Register the formatter through Orchestration-specific MVC options, then verify common MVC binding plus the real controller action metadata through the Orchestration assembly.

**Tech Stack:** .NET 10, ASP.NET Core MVC input formatters, System.Text.Json, xUnit, Shouldly, Microsoft.AspNetCore.TestHost.

## Global Constraints

- Existing `application/json` binding behavior must remain unchanged.
- Form support is enabled only in the Orchestration host.
- Standard envelope `key`, `stage`, and `tags` values remain strings.
- Raw payload leaves and values below `attributes` use JSON-scalar literal semantics.
- Ambiguous or malformed paths return HTTP 400 instead of silently overwriting data.
- Do not add a third-party query-string parser.

---

### Task 1: Typed and collision-safe bracket normalization

**Files:**
- Modify: `test/BBT.Workflow.Application.Tests/HttpApi/FormUrlEncodedJsonElementInputFormatterTests.cs`
- Modify: `src/BBT.Workflow.HttpApi.Shared/Formatters/FormUrlEncodedJsonElementInputFormatter.cs`

**Interfaces:**
- Consumes: ASP.NET Core `IFormCollection` and request header `x-vnext-payload-mode`.
- Produces: `JsonElement` on success or an MVC model-state formatter failure on invalid form shape.

- [x] **Step 1: Add failing scalar-contract tests**

Add focused tests asserting:

```csharp
[Theory]
[InlineData("age=30", "age", JsonValueKind.Number)]
[InlineData("active=true", "active", JsonValueKind.True)]
[InlineData("value=null", "value", JsonValueKind.Null)]
public async Task ReadRequestBodyAsync_RawJsonLiteral_UsesJsonType(
    string body, string property, JsonValueKind expectedKind)
{
    var element = await ReadElementAsync(new(), body);
    element.GetProperty(property).ValueKind.ShouldBe(expectedKind);
}

[Fact]
public async Task ReadRequestBodyAsync_QuotedScalar_PreservesString()
{
    var element = await ReadElementAsync(new(), "code=%2200123%22");
    element.GetProperty("code").GetString().ShouldBe("00123");
}
```

Extend `BuildContext`/`ReadElementAsync` with an optional payload-mode header. Add standard-mode assertions proving numeric-looking `key`, `stage`, and `tags[]` remain strings while `attributes[age]` becomes a number.

- [x] **Step 2: Run the scalar tests and verify RED**

Run:

```bash
dotnet test test/BBT.Workflow.Application.Tests/BBT.Workflow.Application.Tests.csproj \
  --filter 'FullyQualifiedName~FormUrlEncodedJsonElementInputFormatterTests' --no-restore
```

Expected: new number/boolean/null assertions fail because the formatter currently emits every leaf with `WriteStringValue`.

- [x] **Step 3: Add failing bracket-path and error tests**

Add tests for:

```text
items[0][name]=A&items[1][name]=B      -> [{"name":"A"},{"name":"B"}]
items[][name]=A                         -> formatter failure
items[1][name]=A                        -> formatter failure (sparse index)
a=x&a[b]=y                              -> formatter failure (scalar/object collision)
a[b                                     -> formatter failure (malformed bracket)
items[-1]=A                             -> formatter failure (negative index)
```

The failure helper must assert `result.HasError` and a non-empty `ModelState` error message.

- [x] **Step 4: Run the bracket tests and verify RED**

Run the same filtered command. Expected: indexed arrays are emitted as objects and invalid forms currently succeed or lose data.

- [x] **Step 5: Implement the minimal typed path tree**

Replace the `List<string>` plus global `appendToArray` parser with typed path segments:

```csharp
private abstract record PathSegment;
private sealed record PropertySegment(string Name) : PathSegment;
private sealed record IndexSegment(int Index) : PathSegment;
private sealed record AppendSegment : PathSegment;
```

Parse a property root followed by bracketed property names, non-negative numeric indices, or a trailing empty append segment. Reject trailing characters, empty roots, negative indices, and non-trailing append segments. Insert into dictionary/list nodes while requiring contiguous indices and rejecting any scalar/container type collision.

Resolve payload mode from `x-vnext-payload-mode`, falling back to presence of a top-level `attributes` property. Convert leaves with this rule:

```csharp
private static object? ParseScalar(string value, bool preserveString)
{
    if (preserveString)
        return value;

    try
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.ValueKind switch
        {
            JsonValueKind.String => document.RootElement.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null
                => document.RootElement.Clone(),
            _ => throw new FormUrlEncodedPathException("Object and array scalar values are not supported.")
        };
    }
    catch (JsonException)
    {
        return value;
    }
}
```

Preserve strings for the standard envelope paths `key`, `stage`, and `tags`; apply literal conversion below `attributes` or to every raw-payload leaf. On a path exception, add its message to `context.ModelState` and return `InputFormatterResult.Failure()`.

- [x] **Step 6: Run formatter tests and verify GREEN**

Run the filtered formatter command. Expected: all formatter tests pass.

### Task 2: Scope registration to Orchestration

**Files:**
- Modify: `src/BBT.Workflow.HttpApi.Shared/Microsoft/Extensions/DependencyInjection/WorkflowApiBaseServiceCollectionExtensions.cs`
- Modify: `orchestration/BBT.Workflow.Orchestration.HttpApi.Host/Microsoft/Extensions/DependencyInjection/OrchestrationApiServiceCollectionExtensions.cs`
- Modify: `orchestration/BBT.Workflow.Orchestration.HttpApi.Host/BBT.Workflow.Orchestration.HttpApi.Host.csproj`
- Modify: `test/BBT.Workflow.Application.Tests/BBT.Workflow.Application.Tests.csproj`
- Create: `test/BBT.Workflow.Application.Tests/HttpApi/FormUrlEncodedFormatterRegistrationTests.cs`

**Interfaces:**
- Produces: Orchestration-specific `IConfigureOptions<MvcOptions>` registration.
- Ensures: shared `AddAspNetCoreModules` does not add the formatter.

- [x] **Step 1: Add failing registration tests**

Add a project reference from Application.Tests to the Orchestration host and expose Orchestration internals to the test assembly. Test shared registration separately from a small internal Orchestration registration extension:

```csharp
sharedOptions.InputFormatters.ShouldNotContain(x =>
    x is FormUrlEncodedJsonElementInputFormatter);

orchestrationOptions.InputFormatters.ShouldContain(x =>
    x is FormUrlEncodedJsonElementInputFormatter);
```

- [x] **Step 2: Run registration tests and verify RED**

Run:

```bash
dotnet test test/BBT.Workflow.Application.Tests/BBT.Workflow.Application.Tests.csproj \
  --filter 'FullyQualifiedName~FormUrlEncodedFormatterRegistrationTests'
```

Expected: shared registration contains the formatter and no Orchestration-only registration exists.

- [x] **Step 3: Move the registration**

Restore the shared call to `services.AddControllers()` and add an internal Orchestration helper:

```csharp
internal static IServiceCollection AddFormUrlEncodedJsonElementInput(this IServiceCollection services)
{
    services.Configure<MvcOptions>(options =>
        options.InputFormatters.Insert(0, new FormUrlEncodedJsonElementInputFormatter()));
    return services;
}
```

Call it from `AddOrchestrationApiModule` immediately after `AddAspNetCoreModules(configuration)`.

- [x] **Step 4: Run registration and formatter tests and verify GREEN**

Run both filtered test classes. Expected: all pass.

### Task 3: Verify MVC binding and real endpoint API metadata

**Files:**
- Modify: `test/BBT.Workflow.Application.Tests/BBT.Workflow.Application.Tests.csproj`
- Create: `test/BBT.Workflow.Application.Tests/HttpApi/FormUrlEncodedMvcContractTests.cs`

**Interfaces:**
- Consumes: actual `InstanceController` and `FunctionController` action descriptors.
- Produces: regression proof for body binding and advertised request formats.

- [x] **Step 1: Add TestHost dependency and failing MVC contract tests**

Add `Microsoft.AspNetCore.TestHost` using `$(MicrosoftPackageVersion)`. Build a minimal `WebApplication` with a probe `[ApiController]` action binding `[FromBody] JsonElement?`, register the Orchestration formatter, and assert:

```csharp
using var form = new FormUrlEncodedContent(new Dictionary<string, string>
{
    ["attributes[age]"] = "30"
});
var formResponse = await client.PostAsync("/probe", form);
formResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

var jsonResponse = await client.PostAsJsonAsync("/probe", new { attributes = new { age = 30 } });
jsonResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
```

Add invalid-form HTTP tests expecting 400.

- [x] **Step 2: Add failing actual-action metadata tests**

Register the real Orchestration controller assembly as an MVC application part, obtain `IApiDescriptionGroupCollectionProvider`, and locate:

```text
InstanceController.StartAsync
InstanceController.TransitionAsync
FunctionController.InvokeDomainFunctionAsync
FunctionController.InvokeInstanceFunctionAsync
```

For every HTTP method/action description, assert request formats contain both `application/json` and `application/x-www-form-urlencoded`.

- [x] **Step 3: Run MVC contract tests and verify RED**

Run the filtered MVC class. Expected: form binding or action metadata assertions fail until Orchestration registration and formatter behavior are active in the test application.

- [x] **Step 4: Implement only the test-host/API metadata wiring needed for GREEN**

Reuse the production Orchestration registration helper. Do not duplicate formatter configuration in tests. If ApiExplorer requires API-version services, register the same `AddAetherApiVersioning(apiTitle: "vNext API")` call used by production.

- [x] **Step 5: Run MVC contract tests and verify GREEN**

Run the filtered MVC command. Expected: form, JSON regression, invalid-form 400, and all four action metadata assertions pass.

### Task 4: Publish the form contract and run verification

**Files:**
- Create: `docs/contracts/form-url-encoded-payloads.md`
- Modify: `src/BBT.Workflow.HttpApi.Shared/Formatters/FormUrlEncodedJsonElementInputFormatter.cs`

**Interfaces:**
- Produces: user-facing syntax, typing, payload-mode, and rejection documentation.

- [x] **Step 1: Write the contract documentation**

Document curl-style examples for standard and raw payloads, the JSON-literal/string rules, indexed arrays, header overrides, and rejected ambiguous inputs. Link the formatter XML remarks to `docs/contracts/form-url-encoded-payloads.md`.

- [x] **Step 2: Run targeted verification**

Run:

```bash
dotnet test test/BBT.Workflow.Application.Tests/BBT.Workflow.Application.Tests.csproj \
  --filter 'FullyQualifiedName~FormUrlEncoded' --no-restore
git diff --check
```

Expected: all form-urlencoded tests pass and diff check returns no output.

- [x] **Step 3: Run the relevant full project**

Run:

```bash
dotnet test test/BBT.Workflow.Application.Tests/BBT.Workflow.Application.Tests.csproj --no-restore
```

Expected: no new failures compared with the observed baseline. Report baseline failures separately if the project remains non-green.

- [x] **Step 4: Review final scope**

Run `git status --short` and `git diff --stat origin/master...HEAD` plus the working-tree diff. Confirm no Execution controller behavior changed and unrelated user changes remain intact.
