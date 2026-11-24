using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Caller;
using BBT.Workflow.Scripting.Expressions;
using BBT.Workflow.Scripting.Rules;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Scripting.Expressions;

using InputFn = Func<RuleJsonDynamic, RuleJsonDynamic, ExpressoInstanceView, DateTimeOffset, ExpressionUser, ExpressoRuleContext, object>;
using OutputFn = Func<RuleJsonDynamic, RuleJsonDynamic, ExpressoInstanceView, DateTimeOffset, ExpressionUser, ExpressoRuleContext, RuleJsonDynamic, object>;

public sealed class ExpressionScopeTests
{
    private const string InstanceJson = """{"x":42,"customer":{"name":"Ada"}}""";

    private readonly ExpressionCompiler _compiler = new(64);
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero));

    // ---- helpers ----

    private sealed class TransitionSpy
    {
        public int DataReads;
        public int HeaderReads;

        public ScriptTransitionRequest Create(object? data) => new(
            () => { DataReads++; return data; },
            () => { HeaderReads++; return new Dictionary<string, object?> { ["h"] = "1" }; });
    }

    private sealed class CallerSpy(bool available, IReadOnlyList<string>? roles, bool fail = false) : ICallerAccessor
    {
        public int Resolutions;
        public bool IsAvailable => available;
        public bool ResolutionFailed { get; private set; }
        public bool? RolesFromHeader => null;

        public Task<IReadOnlyList<string>> RolesAsync()
        {
            if (!available)
                throw new CallerRolesUnavailableException("unavailable");
            Resolutions++;
            if (fail)
            {
                ResolutionFailed = true;
                throw new CallerRolesUnavailableException("provider down");
            }

            return Task.FromResult(roles!);
        }

        public async Task<bool> HasRoleAsync(string role) => (await RolesAsync()).Contains(role);
    }

    private static Instance InstanceWithData(string json)
    {
        var instance = Instance.Create(Guid.NewGuid(), "scope-flow", "1.0.0", "scope-key");
        var row = new InstanceData(Guid.NewGuid(), instance.Id, "1.0.0", new JsonData(json), true);
        typeof(InstanceData).GetProperty(nameof(InstanceData.VersionNo))!.SetValue(row, 1L);
        instance.AcceptPersistedData(row);
        return instance;
    }

    private static ScriptContext Context(TransitionSpy? spy = null, object? input = null, object? body = null) =>
        new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetInstance(InstanceWithData(InstanceJson))
            .SetBody(body ?? new Dictionary<string, object> { ["fromBody"] = true })
            .SetCurrentTransition((spy ?? new TransitionSpy()).Create(input ?? new Dictionary<string, object> { ["amount"] = 7 }))
            .Build();

    private ExpressionScope Scope(ScriptContext ctx, ICallerAccessor? caller = null, ScopeKind kind = ScopeKind.MappingInput,
        JsonElement? result = null) =>
        ExpressionScope.Create(ctx, _clock, caller ?? NullCallerAccessor.Instance, kind, result);

    private async Task<object?> EvalAsync(ExpressionScope scope, string text)
    {
        var signature = ExpressionSignatures.Mapping(ScopeKind.MappingInput);
        var compiled = _compiler.Compile<InputFn>(text, signature);
        compiled.IsSuccess.ShouldBeTrue(compiled.IsSuccess ? null : compiled.Error.Message);
        var prepared = await scope.PrepareAsync(compiled.Value.UsedIdentifiers);
        prepared.IsSuccess.ShouldBeTrue();
        var args = scope.Bind(signature.Parameters, compiled.Value.UsedIdentifiers);
        try
        {
            return compiled.Value.Invoke.DynamicInvoke(args);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    // ---- signature ----

    [Fact]
    public void Mapping_DeclaresVariables_ResultOnlyForOutput()
    {
        ExpressionSignatures.Mapping(ScopeKind.MappingInput).Parameters.Select(p => p.Name)
            .ShouldBe(["data", "input", "instance", "now", "user", "context"]);
        ExpressionSignatures.Mapping(ScopeKind.MappingOutput).Parameters.Select(p => p.Name)
            .ShouldBe(["data", "input", "instance", "now", "user", "context", "result"]);
        ExpressionSignatures.Mapping(ScopeKind.MappingInput).ReturnType.ShouldBe(typeof(object));
        ExpressionSignatures.Mapping(ScopeKind.MappingInput).Parameters.Single(p => p.Name == "now").Type
            .ShouldBe(typeof(DateTimeOffset));
        ExpressionSignatures.Mapping(ScopeKind.MappingOutput).Name
            .ShouldNotBe(ExpressionSignatures.Mapping(ScopeKind.MappingInput).Name);
    }

    // ---- laziness ----

    [Fact]
    public void Bind_OnlyDataUsed_DoesNotBuildContextOrInput()
    {
        var spy = new TransitionSpy();
        var scope = Scope(Context(spy));
        var data = ExpressionSignatures.Mapping(ScopeKind.MappingInput).Parameters.Where(p => p.Name == "data").ToList();

        var args = scope.Bind(data);

        args.Length.ShouldBe(1);
        args[0].ShouldBeOfType<RuleJsonDynamic>().ToString().ShouldContain("Ada");
        spy.DataReads.ShouldBe(0);
        spy.HeaderReads.ShouldBe(0);
    }

    [Fact]
    public void Bind_WithUsedSet_LeavesUnusedSlotsDefault()
    {
        var spy = new TransitionSpy();
        var scope = Scope(Context(spy));
        var signature = ExpressionSignatures.Mapping(ScopeKind.MappingInput);

        var args = scope.Bind(signature.Parameters, new HashSet<string> { "data", "Int32" });

        args.Length.ShouldBe(signature.Parameters.Count);
        args[0].ShouldNotBeNull();
        args[1].ShouldBeNull();      // input
        args[2].ShouldBeNull();      // instance
        args[3].ShouldBe(default(DateTimeOffset));
        args[5].ShouldBeNull();      // context
        spy.DataReads.ShouldBe(0);
        spy.HeaderReads.ShouldBe(0);
    }

    [Fact]
    public void Bind_SameNameTwice_MaterializesOnce()
    {
        var spy = new TransitionSpy();
        var scope = Scope(Context(spy));
        var input = ExpressionSignatures.Mapping(ScopeKind.MappingInput).Parameters.Where(p => p.Name == "input").ToList();

        var first = scope.Bind(input)[0];
        var second = scope.Bind(input)[0];

        ReferenceEquals(first, second).ShouldBeTrue();
        spy.DataReads.ShouldBe(1);
    }

    [Fact]
    public void Now_IsStableWithinOneScope()
    {
        var scope = Scope(Context());
        var now = ExpressionSignatures.Mapping(ScopeKind.MappingInput).Parameters.Where(p => p.Name == "now").ToList();

        var first = scope.Bind(now)[0];
        _clock.Advance(TimeSpan.FromMinutes(5));
        var second = scope.Bind(now)[0];

        second.ShouldBe(first);
        first.ShouldBe(new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero));
    }

    // ---- variable meanings ----

    [Fact]
    public async Task Data_ReadsInstanceLatestData()
    {
        (await EvalAsync(Scope(Context()), "data.customer.name.ToString()")).ShouldBe("Ada");
    }

    [Fact]
    public async Task Input_IsTransitionRequestBody_NotBody()
    {
        var scope = Scope(Context(body: new Dictionary<string, object> { ["amount"] = 999 }));

        (await EvalAsync(scope, "input.amount.ToString()")).ShouldBe("7");
    }

    [Fact]
    public async Task Input_NoCurrentTransition_IsNull_NoBodyFallback()
    {
        var ctx = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetBody(new Dictionary<string, object> { ["amount"] = 999 })
            .Build();

        (await EvalAsync(Scope(ctx), "input == null")).ShouldBe(true);
    }

    [Fact]
    public async Task Instance_ExposesIdentityAndState()
    {
        var ctx = Context();

        (await EvalAsync(Scope(ctx), "instance.Key")).ShouldBe("scope-key");
        (await EvalAsync(Scope(ctx), "instance.Id")).ShouldBe(ctx.Instance!.Id);
    }

    [Fact]
    public async Task Result_OutputPhase_WrapsStandardResponse()
    {
        using var doc = JsonDocument.Parse("""{"body":{"ok":true},"headers":{"h":"v"},"statusCode":200}""");
        var scope = Scope(Context(), kind: ScopeKind.MappingOutput, result: doc.RootElement);
        var signature = ExpressionSignatures.Mapping(ScopeKind.MappingOutput);
        var compiled = _compiler.Compile<OutputFn>("result.statusCode.AsInt32() == 200 && result.body.ok.AsBoolean()", signature);
        compiled.IsSuccess.ShouldBeTrue(compiled.IsSuccess ? null : compiled.Error.Message);
        (await scope.PrepareAsync(compiled.Value.UsedIdentifiers)).IsSuccess.ShouldBeTrue();

        var value = compiled.Value.Invoke.DynamicInvoke(scope.Bind(signature.Parameters, compiled.Value.UsedIdentifiers));

        value.ShouldBe(true);
    }

    [Fact]
    public async Task Context_LegacyMembers_StillResolve()
    {
        var scope = Scope(Context());

        (await EvalAsync(scope, "context.Instance.Data[\"x\"].AsInt32()")).ShouldBe(42);
        (await EvalAsync(scope, "context.Body.fromBody.AsBoolean()")).ShouldBe(true);
        (await EvalAsync(scope, "context.CurrentTransition.Data.amount.AsInt32()")).ShouldBe(7);
    }

    // ---- user (D-3) ----

    [Fact]
    public async Task User_HasRole_ResolvesOnceOnlyWhenUsed()
    {
        var caller = new CallerSpy(available: true, roles: ["a", "b"]);
        var scope = Scope(Context(), caller);

        (await EvalAsync(scope, "data.x.AsInt32()")).ShouldBe(42);
        caller.Resolutions.ShouldBe(0);

        (await EvalAsync(scope, "user.hasRole(\"a\")")).ShouldBe(true);
        (await EvalAsync(scope, "user.roles.Length")).ShouldBe(2);
        caller.Resolutions.ShouldBe(1);
    }

    [Fact]
    public async Task User_Unavailable_RolesReadFails()
    {
        var scope = Scope(Context(), NullCallerAccessor.Instance);

        await Should.ThrowAsync<CallerRolesUnavailableException>(() => EvalAsync(scope, "user.hasRole(\"a\")"));
        await Should.ThrowAsync<CallerRolesUnavailableException>(() => EvalAsync(scope, "user.roles.Length"));
    }

    [Fact]
    public async Task User_Available_GuardsSystemHop()
    {
        var scope = Scope(Context(), NullCallerAccessor.Instance);

        (await EvalAsync(scope, "user.available ? user.hasRole(\"a\") : false")).ShouldBe(false);
    }

    [Fact]
    public async Task User_ProviderFailure_PrepareFailsClosed()
    {
        var caller = new CallerSpy(available: true, roles: null, fail: true);
        var scope = Scope(Context(), caller);

        var prepared = await scope.PrepareAsync(new HashSet<string> { "user" });

        prepared.IsSuccess.ShouldBeFalse();
        prepared.Error.Code.ShouldBe(WorkflowErrors.CallerRolesUnavailable().Code);
    }
}
