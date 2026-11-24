using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Authorization;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution;
using BBT.Workflow.Instances;
using BBT.Workflow.Logging;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting;
using BBT.Workflow.Scripting.Caller;
using BBT.Workflow.Scripting.Expressions;
using BBT.Workflow.Scripting.Rules;
using BBT.Workflow.Shared;
using BBT.Workflow.Tasks.Coordinator;
using BBT.Workflow.Tasks.Executors;
using BBT.Workflow.Tasks.Invocation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Tasks.Mapping;

/// <summary>
/// The declarative <c>input</c> / <c>output</c> blocks of a task binding (<see cref="DeclarativeTaskMappingRunner"/>):
/// select/into before set, declaration order, missing sources, encrypted tokens, input surfaces, failures, the
/// <c>user</c> variable, materialization before the result document goes away, and parallel branches sharing the
/// compiled delegates.
/// </summary>
public sealed class DeclarativeTaskMappingRunnerTests
{
    private const int TaskDeclarativeMappingFailedEventId = 10268;
    private const int TaskDeclarativeMappingEntrySkippedEventId = 10269;

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 24, 10, 0, 0, TimeSpan.Zero));

    // ---- output ----

    [Fact]
    public async Task Output_SelectInto_WritesSubtree()
    {
        var output = Block(select: "$.body.customer", into: "$.kyc.customer");
        var ctx = Context(Http(), output: output);

        var delta = await OutputAsync(ctx, ResultWith("""{"customer":{"id":7,"name":"Ada"},"other":1}"""));

        Json(delta).ShouldBe("""{"kyc":{"customer":{"id":7,"name":"Ada"}}}""");
    }

    [Fact]
    public async Task Output_SelectWithoutInto_MergesTheObjectAtTheDataRoot()
    {
        var ctx = Context(Http(), output: Block(select: "$.body.customer"));

        var delta = await OutputAsync(ctx, ResultWith("""{"customer":{"id":7},"other":1}"""));

        Json(delta).ShouldBe("""{"id":7}""");
    }

    [Fact]
    public async Task Output_IntoWithoutSelect_TakesTheWholeBody()
    {
        var ctx = Context(Http(), output: Block(into: "$.response"));

        var delta = await OutputAsync(ctx, ResultWith("""{"ok":true}"""));

        Json(delta).ShouldBe("""{"response":{"ok":true}}""");
    }

    [Fact]
    public async Task Output_SetAfterInto_Ordered()
    {
        // RF-3: into first, then every set entry in declaration order on top of it.
        var output = Block(select: "$.body", into: "$.kyc", set:
        [
            ("$.kyc.score", "result.body.score.AsInt32() + 10"),
            ("$.kyc.state", "instance.key"),
            ("$.kyc.checkedAt", "now")
        ]);
        var ctx = Context(Http(), output: output);

        var delta = await OutputAsync(ctx, ResultWith("""{"score":1,"level":"x"}"""));

        var kyc = delta!["kyc"]!.AsObject();
        kyc["score"]!.GetValue<int>().ShouldBe(11);       // set overrides the selected subtree
        kyc["level"]!.GetValue<string>().ShouldBe("x");   // the rest of the selected subtree stays
        kyc["state"]!.GetValue<string>().ShouldBe("ctx-key");
        kyc["checkedAt"]!.GetValue<string>().ShouldBe(JsonSerializer.Serialize(_clock.GetUtcNow(),
            JsonSerializerConstants.JsonOptions).Trim('"'));
        kyc.Select(p => p.Key).ToArray().ShouldBe(["score", "level", "state", "checkedAt"]);
    }

    [Fact]
    public async Task Output_LaterSetEntryWins_DeclarationOrder()
    {
        var ctx = Context(Http(), output: Block(set: [("$.k.a", "1"), ("$.k", "\"replaced\"")]));

        var delta = await OutputAsync(ctx, ResultWith("{}"));

        Json(delta).ShouldBe("""{"k":"replaced"}""");
    }

    [Fact]
    public async Task Output_MissingSource_WritesNothing()
    {
        // RF-1: a missing source path or a null value writes nothing — no exception, no empty object, no empty version.
        var output = Block(select: "$.body.missing", into: "$.kyc.x", set:
        [
            ("$.a", "data.customer"),
            ("$.b", "data.customer?.id"),
            ("$.c", "result.body.nothing")
        ]);
        var ctx = Context(Http(), output: output, instanceJson: """{"x":1}""");

        var result = await Runner().ProcessOutputAsync(ctx.Task, ResultWith("""{"present":1}"""), ctx, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeNull();
    }

    [Fact]
    public async Task Output_MissingIntermediate_WritesNothing()
    {
        // RF-1: a path through a missing intermediate (no '?.') is a missing source — the entry writes nothing.
        var ctx = Context(Http(), output: Block(set:
        [
            ("$.id", "data.customer.id"),
            ("$.deep", "data.x.y.z"),
            ("$.item", "data.list[9].name"),
            ("$.kept", "data.x")
        ]), instanceJson: """{"x":1,"list":[]}""");

        var delta = await OutputAsync(ctx, ResultWith("{}"));

        Json(delta).ShouldBe("""{"kept":1}""");
    }

    [Fact]
    public async Task Input_MissingIntermediate_WritesNothing()
    {
        var task = Http("""{ "url": "https://x.test", "method": "POST", "body": { "static": 1 } }""");
        var ctx = Context(task, input: Block(set: [("$.body.id", "data.missing.id"), ("$.body.n", "input.none.deeper")]),
            instanceJson: """{"x":1}""");

        (await Runner().PrepareInputAsync(task, ctx, CancellationToken.None)).IsSuccess.ShouldBeTrue();

        task.Body!.Value.GetRawText().ShouldBe("""{"static":1}""");
    }

    [Fact]
    public async Task Output_ExpressionThrows_FailsTheMapping_NeverSkips()
    {
        // An expression that genuinely fails (a method call with an out-of-range argument) is a failure, not a skip.
        var logger = new CapturingLogger();
        var ctx = Context(Http(), output: Block(set: [("$.id", "\"abc\".Substring(10)")]), instanceJson: """{"x":1}""");

        var result = await Runner().ProcessOutputAsync(ctx.Task, ResultWith("{}"), ctx, CancellationToken.None,
            Site(logger));

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.TaskExecution);
        result.Error.Message.ShouldStartWith("Http task declarative output mapping failed: set['$.id']: ");
        var logged = logger.Events.ShouldHaveSingleItem();
        logged.Id.ShouldBe(TaskDeclarativeMappingFailedEventId);
        logged.Level.ShouldBe(LogLevel.Error);
    }

    [Fact]
    public async Task Output_EncryptedToken_StaysToken()
    {
        // RF-2: an x-encryption field holds its stored token; reading it and writing it back keeps the token (the
        // runner never decrypts — only the write funnel's protector, which it does not call, ever sees the field).
        const string token = "enc:v1:Q2lwaGVydGV4dA==";
        var protector = Substitute.For<IInstanceDataProtector>();
        var ctx = Context(Http(), output: Block(set: [("$.copy.secret", "data.secret"), ("$.whole", "data")]),
            instanceJson: $$"""{"secret":"{{token}}","plain":1}""");

        var delta = await OutputAsync(ctx, ResultWith("{}"));

        delta!["copy"]!["secret"]!.GetValue<string>().ShouldBe(token);
        delta["whole"]!["secret"]!.GetValue<string>().ShouldBe(token);
        protector.ReceivedCalls().ShouldBeEmpty();
    }

    [Fact]
    public async Task Output_ValuesAreMaterialized_BeforeTheResultDocumentIsDisposed()
    {
        // The runner disposes the result document it borrowed `result` from before it returns; a delta still holding a
        // borrowed view would throw ObjectDisposedException here.
        var ctx = Context(Http(), output: Block(set: [("$.nested", "result.body.deep"), ("$.n", "result.statusCode")]));

        var delta = await OutputAsync(ctx, ResultWith("""{"deep":{"list":[1,2,{"x":"y"}]}}"""));

        Json(delta).ShouldBe("""{"nested":{"list":[1,2,{"x":"y"}]},"n":200}""");
    }

    [Fact]
    public void Materialize_BorrowedView_SurvivesItsDocument()
    {
        JsonNode? node;
        using (var document = JsonDocument.Parse("""{"a":{"b":[1,"two"]}}"""))
        {
            var borrowed = RuleJsonDynamic.Borrow(document.RootElement.GetProperty("a"));
            node = DeclarativeValues.ToNode(borrowed);
        }

        node!.ToJsonString().ShouldBe("""{"b":[1,"two"]}""");
    }

    [Fact]
    public async Task Output_ResultView_ReadsEachMemberAndTheWholeResult_AsTheResultObjectWould()
    {
        // `result` is read through a member view (no { body, headers, statusCode } document is built for a member
        // read); used as a value it is the whole object, with header names as sent and member names case-insensitive.
        var ctx = Context(Http(), output: Block(select: "$.headers", into: "$.hdr", set:
        [
            ("$.whole", "result"),
            ("$.cased", "result.Body.a"),
            ("$.code", "result[\"statusCode\"]"),
            ("$.missing", "result.nope")
        ]));
        var result = TaskInvocationResult.Success(
            data: JsonSerializer.Deserialize<JsonElement>("""{"a":1}"""), statusCode: 201,
            headers: new Dictionary<string, string> { ["X-Req"] = "1" });

        var delta = await OutputAsync(ctx, result);

        Json(delta).ShouldBe(
            """{"hdr":{"X-Req":"1"},"whole":{"body":{"a":1},"headers":{"X-Req":"1"},"statusCode":201},"cased":1,"code":201}""");
    }

    [Theory]
    [InlineData("$", """{"r":{"body":{"a":1},"headers":null,"statusCode":200}}""")]
    [InlineData("$.statusCode", """{"r":200}""")]
    [InlineData("$.body.a", """{"r":1}""")]
    [InlineData("$.Body.a", "null")]     // path reads are case-sensitive
    [InlineData("$.other", "null")]
    [InlineData("$[0]", "null")]
    [InlineData("$[*]", "null")]
    [InlineData("$.headers.x", "null")]  // no headers: JSON null, nothing below it
    public async Task Output_Select_ResolvesItsFirstSegmentAgainstTheResultObject(string select, string expected)
    {
        var ctx = Context(Http(), output: Block(select: select, into: "$.r"));

        var delta = await OutputAsync(ctx, ResultWith("""{"a":1}"""));

        Json(delta).ShouldBe(expected);
    }

    [Fact]
    public async Task Output_Delta_StaysReadable_AfterADisposableTaskResultDocumentIsDisposed()
    {
        // Data borrowed from a disposable document the caller owns: the selected subtree and every set value are copied
        // out before the mapping returns, so disposing that document afterwards leaves the delta readable.
        var ctx = Context(Http(), output: Block(select: "$.body.customer", into: "$.c",
            set: [("$.n", "result.body.customer.name"), ("$.all", "result.body")]));
        JsonObject? delta;
        using (var document = JsonDocument.Parse("""{"customer":{"name":"Ada","tags":["x"]}}"""))
        {
            delta = await OutputAsync(ctx, TaskInvocationResult.Success(data: document.RootElement, statusCode: 200));
        }

        Json(delta).ShouldBe(
            """{"c":{"name":"Ada","tags":["x"]},"n":"Ada","all":{"customer":{"name":"Ada","tags":["x"]}}}""");
    }

    [Fact]
    public async Task Output_Delta_DoesNotPinANonDisposableTaskResultDocument()
    {
        // The HTTP / Dapr invokers hand over a JsonElement of a NON-disposable document, for which JsonElement.Clone() is
        // a no-op. The delta must hold a subtree-sized copy, not a node over the whole response: once the task result is
        // unreachable, its document is collected although the delta is still alive.
        var ctx = Context(Http(), output: Block(select: "$.body.customer.name", into: "$.n",
            set: [("$.c", "result.body.customer")]));

        var (delta, source) = await OutputOverUnreachableResultAsync(ctx);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        source.IsAlive.ShouldBeFalse("the delta still references the task result's document");
        Json(delta).ShouldBe("""{"n":"Ada","c":{"name":"Ada"}}""");
        delta!["n"]!.AsValue().TryGetValue<JsonElement>(out var leaf).ShouldBeTrue();
        leaf.GetRawText().ShouldBe("\"Ada\"");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private async Task<(JsonObject? Delta, WeakReference Source)> OutputOverUnreachableResultAsync(TaskExecutorContext ctx)
    {
        var body = JsonSerializer.Deserialize<JsonElement>(
            """{"customer":{"name":"Ada"},"items":[""" + string.Join(",", Enumerable.Range(0, 2000)) + "]}");
        var parent = typeof(JsonElement).GetField("_parent",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(body)!;
        var source = new WeakReference(parent);
        var delta = await OutputAsync(ctx, TaskInvocationResult.Success(data: body, statusCode: 200));
        return (delta, source);
    }

    [Fact]
    public async Task Output_Delta_OutlivesTheViewsSerializedBody_WithTheRuntimesJsonOptions()
    {
        // A non-JSON Data is serialized once (runtime options: camelCase dictionary keys) into a document the view
        // disposes at scope end; the delta, read after the mapping returned, holds owned copies.
        var ctx = Context(Http(), output: Block(select: "$.body.customer", into: "$.c", set: [("$.raw", "result")]));
        var data = new Dictionary<string, object> { ["Customer"] = new Dictionary<string, object> { ["Name"] = "Ada" } };

        var delta = await OutputAsync(ctx, TaskInvocationResult.Success(data: data, statusCode: 200));

        Json(delta).ShouldBe(
            """{"c":{"name":"Ada"},"raw":{"body":{"customer":{"name":"Ada"}},"headers":null,"statusCode":200}}""");
    }

    [Fact]
    public async Task Output_RawBodyText_IsTheBodyWhenThereIsNoData()
    {
        var ctx = Context(Http(), output: Block(set: [("$.b", "result.body"), ("$.len", "result.body.ToString().Length")]));

        var delta = await OutputAsync(ctx, TaskInvocationResult.Success(body: "plain text", statusCode: 200));

        Json(delta).ShouldBe("""{"b":"plain text","len":10}""");
    }

    [Fact]
    public async Task Output_NoOutputBlock_PassesResultDataThrough()
    {
        var ctx = Context(Http(), input: Block(set: [("$.body.a", "1")]));

        var result = await Runner().ProcessOutputAsync(ctx.Task, TaskInvocationResult.Success(data: "raw"), ctx, CancellationToken.None);

        result.Value.ShouldBe("raw");
    }

    // ---- user ----

    [Fact]
    public async Task User_RolesOfTheHopCaller_AreReadOnlyWhenUsed()
    {
        var resolutions = 0;
        var caller = new CallerAccessor(() =>
        {
            resolutions++;
            return Task.FromResult(new CallerRoleSnapshot(["approver"], false, false));
        });
        var ctx = Context(Http(), output: Block(set: [("$.can", "user.hasRole(\"approver\")"), ("$.a", "user.available")]),
            caller: caller);

        var delta = await OutputAsync(ctx, ResultWith("{}"));

        Json(delta).ShouldBe("""{"can":true,"a":true}""");
        resolutions.ShouldBe(1);

        var noUser = Context(Http(), output: Block(set: [("$.x", "1")]), caller: caller);
        await OutputAsync(noUser, ResultWith("{}"));
        resolutions.ShouldBe(1);
    }

    [Fact]
    public async Task User_ProviderFailure_PropagatesAsAMappingFailure()
    {
        var caller = new CallerAccessor(() => Task.FromResult(new CallerRoleSnapshot([], true, false)));
        var logger = new CapturingLogger();
        var ctx = Context(Http(), output: Block(set: [("$.can", "user.available && user.hasRole(\"x\")")]), caller: caller);

        var result = await Runner().ProcessOutputAsync(ctx.Task, ResultWith("{}"), ctx, CancellationToken.None, Site(logger));

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrors.CallerRolesUnavailable().Code);
        logger.Events.ShouldHaveSingleItem().Id.ShouldBe(TaskDeclarativeMappingFailedEventId);
    }

    [Fact]
    public async Task User_SystemHop_RoleReadFails_AvailableGuards()
    {
        var guarded = Context(Http(), output: Block(set: [("$.can", "user.available ? user.hasRole(\"x\") : false")]));
        Json(await OutputAsync(guarded, ResultWith("{}"))).ShouldBe("""{"can":false}""");

        var unguarded = Context(Http(), output: Block(set: [("$.can", "user.hasRole(\"x\")")]));
        var result = await Runner().ProcessOutputAsync(unguarded.Task, ResultWith("{}"), unguarded, CancellationToken.None);
        result.IsSuccess.ShouldBeFalse();
        AssertNoCallerFailure(result.Error, "output");
    }

    [Fact]
    public async Task User_SystemHop_InputSetRoleRead_FailsNonTransient()
    {
        var task = Http();
        var ctx = Context(task, input: Block(set: [("$.body.roles", "user.roles")]));

        var result = await Runner().PrepareInputAsync(task, ctx, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        AssertNoCallerFailure(result.Error, "input");
    }

    [Fact]
    public async Task User_InputSelect_SystemHop_RolesFail_AvailableReads()
    {
        var task = Http();
        var roles = Context(task, input: Block(select: "$.user.roles", into: "$.body.roles"));
        var failed = await Runner().PrepareInputAsync(task, roles, CancellationToken.None);
        failed.IsSuccess.ShouldBeFalse();
        AssertNoCallerFailure(failed.Error, "input");

        var availableTask = Http();
        var available = Context(availableTask, input: Block(select: "$.user.available", into: "$.body.available"));
        (await Runner().PrepareInputAsync(availableTask, available, CancellationToken.None)).IsSuccess.ShouldBeTrue();
        availableTask.Body!.Value.GetRawText().ShouldBe("""{"available":false}""");
    }

    [Fact]
    public async Task User_InputSelect_UserHop_ReadsRoles()
    {
        var caller = new CallerAccessor(() => Task.FromResult(new CallerRoleSnapshot(["approver", "viewer"], false, false)));
        var task = Http();
        var ctx = Context(task, input: Block(select: "$.user.roles", into: "$.body.roles"), caller: caller);

        (await Runner().PrepareInputAsync(task, ctx, CancellationToken.None)).IsSuccess.ShouldBeTrue();

        task.Body!.Value.GetRawText().ShouldBe("""{"roles":["approver","viewer"]}""");
    }

    [Fact]
    public async Task User_InputSelect_ProviderFailure_StaysTransient()
    {
        var caller = new CallerAccessor(() => Task.FromResult(new CallerRoleSnapshot([], true, false)));
        var task = Http();
        var ctx = Context(task, input: Block(select: "$.user.roles", into: "$.body.roles"), caller: caller);

        var result = await Runner().PrepareInputAsync(task, ctx, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrors.CallerRolesUnavailable().Code);
    }

    private static void AssertNoCallerFailure(Error error, string phase)
    {
        // A System hop has no caller: deterministic, so not the transient 110006 ("can be retried").
        error.Code.ShouldBe(WorkflowErrorCodes.TaskExecution);
        error.Code.ShouldNotBe(WorkflowErrors.CallerRolesUnavailable().Code);
        error.Prefix.ShouldNotBe(WorkflowErrors.CallerRolesUnavailable().Prefix);
        error.Message.ShouldStartWith($"Http task declarative {phase} mapping failed: ");
        error.Message.ShouldContain("user.available");
    }

    // ---- RF-1 skip log ----

    [Fact]
    public async Task MissingSources_AreLoggedAtDebug_WithPathNeverValue()
    {
        var logger = new CapturingLogger();
        var ctx = Context(Http(), output: Block(select: "$.body.missing", into: "$.kyc.x", set:
        [
            ("$.id", "data.customer.id"),
            ("$.kept", "data.secret")
        ]), instanceJson: """{"secret":"s3cr3t-value"}""");

        var result = await Runner().ProcessOutputAsync(ctx.Task, ResultWith("""{"present":"p-value"}"""), ctx,
            CancellationToken.None, Site(logger));

        result.IsSuccess.ShouldBeTrue();
        var skipped = logger.Events.Where(e => e.Id == TaskDeclarativeMappingEntrySkippedEventId).ToList();
        skipped.Count.ShouldBe(2);
        skipped.ShouldAllBe(e => e.Level == LogLevel.Debug);
        skipped[0].Message.ShouldContain("$.body.missing");
        skipped[0].Message.ShouldContain("Phase=output");
        skipped[0].Message.ShouldContain("TaskKey=decl-task");
        skipped[1].Message.ShouldContain("set['$.id']");
        logger.Events.ShouldAllBe(e => !e.Message.Contains("s3cr3t-value") && !e.Message.Contains("p-value"));
    }

    [Fact]
    public async Task MissingInputSelect_IsLoggedAtDebug()
    {
        var logger = new CapturingLogger();
        var task = Http();
        var ctx = Context(task, input: Block(select: "$.data.nothing", into: "$.body.x"));

        (await Runner().PrepareInputAsync(task, ctx, CancellationToken.None, Site(logger))).IsSuccess.ShouldBeTrue();

        var skipped = logger.Events.ShouldHaveSingleItem();
        skipped.Id.ShouldBe(TaskDeclarativeMappingEntrySkippedEventId);
        skipped.Message.ShouldContain("$.data.nothing");
        skipped.Message.ShouldContain("Phase=input");
    }

    // ---- assignment ----

    [Fact]
    public async Task AssignmentInASetExpression_FailsAtRunTime()
    {
        var ctx = Context(Http(), output: Block(set: [("$.x", "data.customer = 1")]));

        var result = await Runner().ProcessOutputAsync(ctx.Task, ResultWith("{}"), ctx, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Message.ShouldContain("does not compile");
        result.Error.Code.ShouldBe(WorkflowErrorCodes.TaskExecution);
    }

    // ---- input ----

    [Fact]
    public async Task Input_Set_AppliesThroughSurfaceSetters()
    {
        var task = Http("""{ "url": "https://x.test", "method": "POST", "body": { "static": 1 }, "headers": { "h0": "v0" } }""");
        var input = Block(select: "$.data.customer", into: "$.body.customer", set:
        [
            ("$.body.amount", "input.amount.AsInt32() * 2"),
            ("$.headers.x-channel", "\"web\""),
            ("$.headers.x-count", "3")
        ]);
        var ctx = Context(task, input: input);

        var result = await Runner().PrepareInputAsync(task, ctx, CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldNotBeNull();
        task.Body!.Value.GetRawText().ShouldBe("""{"static":1,"customer":{"id":7,"name":"Ada"},"amount":14}""");
        var headers = JsonSerializer.Deserialize<Dictionary<string, string?>>(task.Headers!.Value)!;
        headers["h0"].ShouldBe("v0");
        headers["x-channel"].ShouldBe("web");
        headers["x-count"].ShouldBe("3");
    }

    [Fact]
    public async Task Input_Query_AppliesThroughTheQueryStringSetter()
    {
        var task = DaprServiceTask.Create(
            """{ "appId": "a", "methodName": "/m", "httpVerb": "GET", "queryString": "size=10" }""".ToJsonElement());
        task.SetReference(new Reference("svc", "test-domain", "sys-tasks", "1.0.0"));
        var ctx = Context(task, input: Block(set: [("$.query.page", "2"), ("$.query.q", "\"a b\"")]));

        (await Runner().PrepareInputAsync(task, ctx, CancellationToken.None)).IsSuccess.ShouldBeTrue();

        task.QueryString.ShouldBe("size=10&page=2&q=a%20b");
    }

    [Fact]
    public async Task Input_UnsupportedSurface_FailsAtRunTimeWithClearError()
    {
        // A binding's task type is not resolvable at publish (the binding holds only a reference), so a surface the
        // type lacks is reported when the mapping runs — as a Result failure, never silently dropped.
        var logger = new CapturingLogger();
        var task = Http();
        var ctx = Context(task, input: Block(set: [("$.query.page", "2")]));

        var result = await Runner().PrepareInputAsync(task, ctx, CancellationToken.None, Site(logger));

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.TaskExecution);
        result.Error.Message.ShouldBe(
            "Http task declarative input mapping failed: task type Http has no 'query' input surface; target only the surfaces it supports.");
        logger.Events.ShouldHaveSingleItem().Message.ShouldContain("Phase=input");
    }

    [Fact]
    public async Task Input_PathInsideANonObjectSurface_FailsInsteadOfReplacingIt()
    {
        var task = Http("""{ "url": "https://x.test", "method": "POST", "body": [1, 2] }""");
        var ctx = Context(task, input: Block(set: [("$.body.x", "1")]));

        var result = await Runner().PrepareInputAsync(task, ctx, CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Message.ShouldContain("'body' is a JSON Array, not an object");
        task.Body!.Value.ValueKind.ShouldBe(JsonValueKind.Array); // left as configured
        task.Body.Value.GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task Input_WholeSurfaceTarget_ReplacesANonObjectSurface()
    {
        var task = Http("""{ "url": "https://x.test", "method": "POST", "body": [1, 2] }""");
        var ctx = Context(task, input: Block(set: [("$.body", "\"text\"")]));

        (await Runner().PrepareInputAsync(task, ctx, CancellationToken.None)).IsSuccess.ShouldBeTrue();

        task.Body!.Value.GetString().ShouldBe("text");
    }

    // ---- failure plumbing ----

    [Fact]
    public async Task Output_ResultDocumentThatCannotBeBuilt_IsAResultFailure_WithLog()
    {
        var logger = new CapturingLogger();
        var ctx = Context(Http(), output: Block(set: [("$.v", "result.body")]));
        var unserializable = TaskInvocationResult.Success(data: new ThrowingData(), statusCode: 200);

        var result = await Runner().ProcessOutputAsync(ctx.Task, unserializable, ctx, CancellationToken.None, Site(logger));

        result.IsSuccess.ShouldBeFalse();
        result.Error.Message.ShouldStartWith("Http task declarative output mapping failed: ");
        logger.Events.ShouldHaveSingleItem().Id.ShouldBe(TaskDeclarativeMappingFailedEventId);
    }

    [Fact]
    public async Task Cancellation_OfTheMappingsToken_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var ctx = Context(Http(), output: Block(set: [("$.a", "1")]));

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Runner().ProcessOutputAsync(ctx.Task, ResultWith("{}"), ctx, cts.Token));
    }

    [Fact]
    public async Task Memo_IsKeyedByBlockAndPhase()
    {
        var task = Http("""{ "url": "https://x.test", "method": "POST" }""");
        var shared = Block(set: [("$.body.a", "1")]);
        var ctx = Context(task, binding: OnExecuteTask.Create(1, task, null, input: shared, output: shared));

        (await Runner().PrepareInputAsync(task, ctx, CancellationToken.None)).IsSuccess.ShouldBeTrue();
        var output = await Runner().ProcessOutputAsync(task, ResultWith("{}"), ctx, CancellationToken.None);

        output.IsSuccess.ShouldBeTrue(output.IsSuccess ? null : output.Error.Message);
        Json((JsonObject?)output.Value).ShouldBe("""{"body":{"a":1}}""");
        ctx.CompiledDeclarativeMappings!.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Executor_WithoutDeclarativeSupport_FailsABindingWithBlocks(bool inputBlock)
    {
        var logger = new CapturingLogger();
        var executor = new HumanTaskExecutor(logger.For<HumanTaskExecutor>());
        var task = HumanTask.Create("""{ "title": "t" }""".ToJsonElement());
        task.SetReference(new Reference("approve", "test-domain", "sys-tasks", "1.0.0"));
        var block = Block(set: [("$.a", "1")]);
        var binding = inputBlock
            ? OnExecuteTask.Create(1, task, null, input: Block(set: [("$.body.a", "1")]))
            : OnExecuteTask.Create(1, task, null, output: block);

        var result = await executor.ExecuteAsync(Context(task, binding: binding), CancellationToken.None);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.ShouldBe(WorkflowErrorCodes.TaskExecution);
        result.Error.Message.ShouldBe("task type Human does not support declarative input/output blocks");
        var logged = logger.Events.Single(e => e.Id == TaskDeclarativeMappingFailedEventId);
        logged.Message.ShouldContain(inputBlock ? "Phase=input" : "Phase=output");
    }

    private sealed class ThrowingData
    {
        public string Value => throw new InvalidOperationException("cannot read");
    }

    // ---- parallel branches (RF-4) ----

    [Fact]
    public async Task ParallelBranches_ShareCompiledDelegateSafely()
    {
        var compiler = new RecordingCompiler(new ExpressionCompiler(64));
        var runner = new DeclarativeTaskMappingRunner(compiler, _clock);
        var task = Http();
        // One binding (same order, the same MappingBlock instance) run by many branches at once, each over its own data.
        var binding = OnExecuteTask.Create(1, task, null, variableKey: "a",
            output: Block(set: [("$.echo", "data.n.AsInt32() * 10"), ("$.body", "result.body.v")]));

        var results = await Task.WhenAll(Enumerable.Range(0, 64).Select(i => Task.Run(async () =>
        {
            var ctx = Context(task, binding: binding, instanceJson: $$"""{"n":{{i}}}""");
            var result = await runner.ProcessOutputAsync(task, ResultWith($$"""{"v":{{i}}}"""), ctx, CancellationToken.None);
            return (i, result);
        })));

        foreach (var (i, result) in results)
            Json((JsonObject?)result.Value).ShouldBe($$"""{"echo":{{i * 10}},"body":{{i}}}""");
        compiler.DistinctDelegates.ShouldBe(2); // one shared delegate per expression, whatever the branch count
    }

    // ---- routing and the executor template ----

    [Fact]
    public void Router_PicksPerPhase_DeclarativeOverScript()
    {
        var task = Http();
        var script = ScriptCode.FromNative("return null;");

        TaskMappingRouter.InputKind(OnExecuteTask.Create(1, task, script)).ShouldBe(TaskMappingKind.Script);
        TaskMappingRouter.InputKind(OnExecuteTask.Create(1, task, null)).ShouldBe(TaskMappingKind.PassThrough);
        TaskMappingRouter.InputKind(OnExecuteTask.Create(1, task, ScriptCode.FromNative(string.Empty))).ShouldBe(TaskMappingKind.PassThrough);
        var outputOnly = OnExecuteTask.Create(1, task, null, output: Block(set: [("$.a", "1")]));
        TaskMappingRouter.InputKind(outputOnly).ShouldBe(TaskMappingKind.PassThrough);
        TaskMappingRouter.OutputKind(outputOnly).ShouldBe(TaskMappingKind.Declarative);
    }

    [Fact]
    public void Router_ScriptAndBlocksBothStored_ScriptWins_LegacyMeaning()
    {
        // Publish rejects script + blocks (D-4); a binding stored before that keeps its legacy meaning: the script runs.
        var task = Http();
        var both = OnExecuteTask.Create(1, task, ScriptCode.FromNative("return null;"),
            input: Block(set: [("$.body.a", "1")]), output: Block(set: [("$.a", "1")]));

        TaskMappingRouter.InputKind(both).ShouldBe(TaskMappingKind.Script);
        TaskMappingRouter.OutputKind(both).ShouldBe(TaskMappingKind.Script);
        both.RunsDeclarativeMapping.ShouldBeFalse();

        var emptyScript = OnExecuteTask.Create(1, task, ScriptCode.FromNative(string.Empty), output: Block(set: [("$.a", "1")]));
        TaskMappingRouter.OutputKind(emptyScript).ShouldBe(TaskMappingKind.Declarative);
        emptyScript.RunsDeclarativeMapping.ShouldBeTrue();
    }

    [Fact]
    public async Task RoutingRunner_ScriptAndBlocks_RunsTheScriptNotTheBlocks()
    {
        var scriptRunner = new ScriptTaskMappingRunner(Substitute.For<IScriptEngine>());
        var compiler = new RecordingCompiler(new ExpressionCompiler(64));
        var routing = new RoutingTaskMappingRunner(scriptRunner, new DeclarativeTaskMappingRunner(compiler, _clock));
        var task = Http();
        var binding = OnExecuteTask.Create(1, task, ScriptCode.FromNative("public class M {}"),
            output: Block(set: [("$.declarative", "1")]));
        var ctx = Context(task, binding: binding);

        await routing.ProcessOutputAsync(task, ResultWith("""{"a":1}"""), ctx, CancellationToken.None);

        compiler.DistinctDelegates.ShouldBe(0); // the declarative block was never compiled or run
    }

    [Fact]
    public async Task Executor_DeclarativeOutput_IsTheResponseData()
    {
        var dispatcher = Substitute.For<ITaskInvocationDispatcher>();
        dispatcher.DispatchAsync(Arg.Any<WorkflowTask>(), Arg.Any<string>(), Arg.Any<BBT.Workflow.Execution.Bindings.HttpTaskBinding>(),
                Arg.Any<TaskTraceContext>(), Arg.Any<CancellationToken>())
            .Returns(Result<TaskInvocationResult>.Ok(ResultWith("""{"score":5}""")));
        var executor = new HttpTaskExecutor(Substitute.For<IRemoteInvokerService>(), Substitute.For<IScriptEngine>(),
            dispatcher, NullLogger<HttpTaskExecutor>.Instance,
            new RoutingTaskMappingRunner(new ScriptTaskMappingRunner(Substitute.For<IScriptEngine>()), Runner()));
        var task = Http();
        var ctx = Context(task, output: Block(select: "$.body.score", into: "$.kyc.score"));

        var response = await executor.ExecuteAsync(ctx, CancellationToken.None);

        response.IsSuccess.ShouldBeTrue();
        Json((JsonObject?)response.Value!.Data).ShouldBe("""{"kyc":{"score":5}}""");
        ctx.ScriptContext.TaskResponse.ShouldContainKey(task.Key.ToVariableName());
    }

    [Fact]
    public void CallerRefiner_CarriesTheHopCallerOnlyForDeclarativeBindingsOnUserHops()
    {
        var accessor = new CallerAccessor(() => Task.FromResult(new CallerRoleSnapshot([], false, false)));
        var factory = Substitute.For<ICallerAccessorFactory>();
        factory.ForHop(ExecutionActor.User, Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(accessor);
        factory.ForHop(ExecutionActor.System, Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(NullCallerAccessor.Instance);
        var task = Http();
        var declarative = OnExecuteTask.Create(1, task, null, output: Block(set: [("$.a", "1")]));
        var script = OnExecuteTask.Create(1, task, ScriptCode.FromNative("return null;"));
        var hop = new TransitionExecutionContext { Actor = ExecutionActor.User };

        var refiner = TaskMappingCaller.Refiner(factory, hop, [declarative, script], CancellationToken.None);

        refiner.ShouldNotBeNull();
        refiner(declarative, TaskEngineExecutionOptions.Default).Caller.ShouldBeSameAs(accessor);
        refiner(script, TaskEngineExecutionOptions.Default).Caller.ShouldBeNull();
        TaskMappingCaller.Refiner(factory, hop, [script], CancellationToken.None).ShouldBeNull();
        TaskMappingCaller.Refiner(factory, new TransitionExecutionContext { Actor = ExecutionActor.System },
            [declarative], CancellationToken.None).ShouldBeNull();
        TaskMappingCaller.Refiner(null, hop, [declarative], CancellationToken.None).ShouldBeNull();
    }

    // ---- helpers ----

    private DeclarativeTaskMappingRunner Runner() => new(new ExpressionCompiler(64), _clock);

    private static TaskMappingSite Site(CapturingLogger logger) =>
        TaskMappingSite.For(TaskType.Http) with { Logger = logger.For<HttpTaskExecutor>() };

    private async Task<JsonObject?> OutputAsync(TaskExecutorContext ctx, TaskInvocationResult result)
    {
        var output = await Runner().ProcessOutputAsync(ctx.Task, result, ctx, CancellationToken.None);
        output.IsSuccess.ShouldBeTrue(output.IsSuccess ? null : output.Error.Message);
        return (JsonObject?)output.Value;
    }

    private static string Json(JsonObject? node) => node?.ToJsonString() ?? "null";

    private static TaskInvocationResult ResultWith(string bodyJson) =>
        TaskInvocationResult.Success(data: JsonSerializer.Deserialize<JsonElement>(bodyJson), statusCode: 200, taskType: "Http");

    private static MappingBlock Block(string? select = null, string? into = null, (string Path, string? Expr)[]? set = null) =>
        new(select, into, set is null
            ? null
            : new OrderedStringMap(set.Where(e => e.Expr is not null)
                .Select(e => new KeyValuePair<string, string>(e.Path, e.Expr!))));

    private static HttpTask Http(string? config = null)
    {
        var task = HttpTask.Create((config ?? """{ "url": "https://x.test", "method": "POST" }""").ToJsonElement());
        task.SetReference(new Reference("decl-task", "test-domain", "sys-tasks", "1.0.0"));
        return task;
    }

    private static Instance InstanceWithData(string json)
    {
        var instance = Instance.Create(Guid.NewGuid(), "test-flow", "1.0.0", "ctx-key");
        var row = new InstanceData(Guid.NewGuid(), instance.Id, "1.0.0", new JsonData(json), true);
        typeof(InstanceData).GetProperty(nameof(InstanceData.VersionNo))!.SetValue(row, 1L);
        instance.AcceptPersistedData(row);
        return instance;
    }

    private static TaskExecutorContext Context(
        WorkflowTask task,
        MappingBlock? input = null,
        MappingBlock? output = null,
        string instanceJson = """{"customer":{"id":7,"name":"Ada"}}""",
        ICallerAccessor? caller = null,
        OnExecuteTask? binding = null)
    {
        binding ??= OnExecuteTask.Create(1, task, null, input: input, output: output);
        var scriptContext = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance)
            .SetRuntime(Substitute.For<IRuntimeInfoProvider>())
            .SetInstance(InstanceWithData(instanceJson))
            .SetCurrentTransition(new ScriptTransitionRequest(
                () => new Dictionary<string, object> { ["amount"] = 7 },
                () => new Dictionary<string, object?>()))
            .Build();
        return new TaskExecutorContext(task, binding, scriptContext, null, TaskTrigger.OnExecute, TaskExecutionOrigin.Flow)
        {
            Caller = caller ?? NullCallerAccessor.Instance
        };
    }

    /// <summary>Delegates to a real compiler and records every distinct delegate instance it hands out.</summary>
    private sealed class RecordingCompiler(IExpressionCompiler inner) : IExpressionCompiler
    {
        private readonly HashSet<Delegate> _delegates = new(ReferenceEqualityComparer.Instance);

        public int DistinctDelegates
        {
            get
            {
                lock (_delegates)
                    return _delegates.Count;
            }
        }

        public Result<CompiledExpression<TDelegate>> Compile<TDelegate>(string text, ExpressionSignature signature)
            where TDelegate : Delegate
        {
            var compiled = inner.Compile<TDelegate>(text, signature);
            if (compiled.IsSuccess)
            {
                lock (_delegates)
                    _delegates.Add(compiled.Value!.Invoke);
            }

            return compiled;
        }
    }
}
