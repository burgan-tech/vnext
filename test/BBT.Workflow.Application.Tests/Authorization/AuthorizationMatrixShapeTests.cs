using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Caching;
using BBT.Workflow.Definitions;
using BBT.Workflow.Execution.LongPoll;
using BBT.Workflow.Gateway;
using BBT.Workflow.Instances;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;
using WorkflowDefinition = BBT.Workflow.Definitions.Workflow;

namespace BBT.Workflow.Authorization;

/// <summary>
/// The authorization-matrix wire shape: a combinator grant carries <c>allOf</c>/<c>anyOf</c> and no
/// <c>role</c> key; a plain grant stays <c>{ "role", "grant" }</c> exactly as before.
/// </summary>
public sealed class AuthorizationMatrixShapeTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    private static WorkflowDefinition Workflow() =>
        JsonSerializer.Deserialize<WorkflowDefinition>("""
            {
              "key": "wf", "type": "F", "timeout": null, "labels": [], "functions": [], "features": [],
              "states": [ { "key": "s", "stateType": "intermediate", "labels": [], "transitions": [] } ],
              "sharedTransitions": [], "extensions": [],
              "startTransition": {"key": "start", "from": null, "target": "s", "triggerType": "Manual", "versionStrategy": "Patch", "labels": [], "onExecutionTasks": [], "view": null},
              "queryRoles": [
                { "role": "maker", "grant": "allow" },
                { "grant": "deny", "allOf": [ { "role": "maker" }, { "role": "$PreviousUser" } ] },
                { "grant": "allow", "anyOf": [ { "role": "a" }, { "role": "b" } ] }
              ]
            }
            """, JsonOptions)!;

    private static async Task<JsonElement> QueryRolesAsync()
    {
        var componentCache = Substitute.For<IComponentCacheStore>();
        componentCache.GetFlowAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Result<WorkflowDefinition>.Ok(Workflow()));
        var instances = Substitute.For<IInstanceRepository>();
        instances.FindByIdentifierAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Instance?)null);

        var sut = new AuthorizeAppService(
            Substitute.For<IServiceProvider>(),
            Substitute.For<IRuntimeInfoProvider>(),
            componentCache,
            instances,
            Substitute.For<ITransitionAuthorizationManager>(),
            Substitute.For<IAuthorizeGateway>(),
            Substitute.For<ICallerRoleResolver>(),
            Substitute.For<ILongPollInteractionGate>(),
            Substitute.For<ILogger<AuthorizeAppService>>());

        var result = await sut.GetAuthorizationMatrixForInstanceAsync("core", "wf", Guid.NewGuid().ToString());

        result.IsSuccess.ShouldBeTrue();
        return JsonDocument.Parse(JsonSerializer.Serialize(result.Value)).RootElement.GetProperty("queryRoles");
    }

    [Fact]
    public async Task Plain_grant_keeps_role_and_grant()
    {
        var plain = (await QueryRolesAsync())[0];

        plain.GetProperty("role").GetString().ShouldBe("maker");
        plain.GetProperty("grant").GetString().ShouldBe("allow");
        plain.TryGetProperty("allOf", out _).ShouldBeFalse();
        plain.TryGetProperty("anyOf", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task AllOf_grant_exposes_children_and_omits_role()
    {
        var combinator = (await QueryRolesAsync())[1];

        combinator.TryGetProperty("role", out _).ShouldBeFalse();
        combinator.GetProperty("grant").GetString().ShouldBe("deny");
        var children = combinator.GetProperty("allOf");
        children.GetArrayLength().ShouldBe(2);
        children[0].GetProperty("role").GetString().ShouldBe("maker");
        children[1].GetProperty("role").GetString().ShouldBe("$PreviousUser");
        combinator.TryGetProperty("anyOf", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task AnyOf_grant_exposes_children_and_omits_role()
    {
        var combinator = (await QueryRolesAsync())[2];

        combinator.TryGetProperty("role", out _).ShouldBeFalse();
        combinator.GetProperty("anyOf").GetArrayLength().ShouldBe(2);
        combinator.TryGetProperty("allOf", out _).ShouldBeFalse();
    }
}
