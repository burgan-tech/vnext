using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.ExceptionHandling;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Pins <see cref="PublishCompletedAppService"/> — the post-deployment hook pipeline behind
/// <c>POST definitions/publish/completed</c>.
/// </summary>
/// <remarks>
/// The properties under test are all about NOT losing work or information: this endpoint is the last
/// step of a CD pipeline and, for the discovery cache, the only automatic invalidation there is. One
/// hook failing must not cancel the others, a throwing hook must not turn a finished deployment into
/// a 500, and a failure must be reported rather than logged away.
/// </remarks>
public sealed class PublishCompletedAppServiceTests
{
    [Fact]
    public async Task Every_hook_runs_even_after_one_fails()
    {
        var first = Hook("first", 10, Result<string>.Ok("Refreshed"));
        var failing = Hook("failing", 20, Result<string>.Fail(Error.Failure("X:1", "nope")));
        var last = Hook("last", 30, Result<string>.Ok("Done"));

        var result = await CreateSut(out _, first, failing, last)
            .ExecuteAsync(new PublishCompletedInput(), CancellationToken.None);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Success.ShouldBeFalse();
        result.Value.Hooks.Select(h => h.Name).ShouldBe(new[] { "first", "failing", "last" });
        result.Value.Hooks[1].Outcome.ShouldBe(PublishCompletedHookOutcomes.Failed);
        result.Value.Hooks[1].Message.ShouldBe("nope");

        // Stopping at the first failure would let an unrelated registration's ORDER decide whether
        // the rest of a deployment's post-work happens at all.
        await last.Received(1).ExecuteAsync(Arg.Any<PublishCompletedInput>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_hook_that_throws_is_recorded_as_a_failure_and_does_not_escape()
    {
        var throwing = Substitute.For<IPublishCompletedHook>();
        throwing.Name.Returns("throwing");
        throwing.ExecuteAsync(Arg.Any<PublishCompletedInput>(), Arg.Any<CancellationToken>())
            .Returns<Task<Result<string>>>(_ => throw new InvalidOperationException("boom"));

        var after = Hook("after", 99, Result<string>.Ok("Refreshed"));

        var result = await CreateSut(out _, throwing, after)
            .ExecuteAsync(new PublishCompletedInput(), CancellationToken.None);

        // The components are already published by the time this runs; a 500 here would be read as a
        // failed deployment.
        result.IsSuccess.ShouldBeTrue();
        result.Value!.Success.ShouldBeFalse();
        result.Value.Hooks[0].Outcome.ShouldBe(PublishCompletedHookOutcomes.Failed);
        result.Value.Hooks[0].Message.ShouldBe("boom");
        result.Value.Hooks[1].Outcome.ShouldBe("Refreshed");
    }

    [Fact]
    public async Task Hooks_run_in_order_regardless_of_registration_order()
    {
        var late = Hook("late", 100, Result<string>.Ok("ok"));
        var early = Hook("early", 1, Result<string>.Ok("ok"));

        var result = await CreateSut(out _, late, early)
            .ExecuteAsync(new PublishCompletedInput(), CancellationToken.None);

        result.Value!.Hooks.Select(h => h.Name).ShouldBe(new[] { "early", "late" });
    }

    [Fact]
    public async Task A_run_with_no_hooks_is_a_success()
    {
        var result = await CreateSut(out _).ExecuteAsync(new PublishCompletedInput(), CancellationToken.None);

        result.Value!.Success.ShouldBeTrue();
        result.Value.Hooks.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_named_success_from_the_hook_is_reported_verbatim()
    {
        // Disabled, SkippedNotOwner and Refreshed are all successes an operator has to tell apart.
        var hook = Hook("discovery-cache", 100, Result<string>.Ok(PublishCompletedHookOutcomes.Disabled));

        var result = await CreateSut(out _, hook).ExecuteAsync(new PublishCompletedInput(), CancellationToken.None);

        result.Value!.Success.ShouldBeTrue();
        result.Value.Hooks[0].Outcome.ShouldBe("Disabled");
    }

    [Fact]
    public async Task A_domain_that_is_not_this_runtime_is_rejected()
    {
        var sut = CreateSut(out var runtimeInfo);
        runtimeInfo.When(r => r.Check("someone-else")).Throw(new NotFoundDomainException("someone-else", "mine"));

        // A pipeline naming the wrong domain is pointed at the wrong runtime; refreshing a stranger's
        // caches quietly would be worse than failing.
        await Should.ThrowAsync<NotFoundDomainException>(() => sut.ExecuteAsync(
            new PublishCompletedInput { Domain = "someone-else" }, CancellationToken.None));
    }

    [Fact]
    public async Task An_absent_domain_is_not_checked()
    {
        var sut = CreateSut(out var runtimeInfo);

        await sut.ExecuteAsync(new PublishCompletedInput { Domain = null }, CancellationToken.None);
        await sut.ExecuteAsync(new PublishCompletedInput { Domain = "  " }, CancellationToken.None);

        // The field is optional: a caller that does not name a domain must not be turned away.
        runtimeInfo.DidNotReceive().Check(Arg.Any<string>());
    }

    // ────────────────────────────────────────────────────────────────────
    // Harness
    // ────────────────────────────────────────────────────────────────────

    private static IPublishCompletedHook Hook(string name, int order, Result<string> result)
    {
        var hook = Substitute.For<IPublishCompletedHook>();
        hook.Name.Returns(name);
        hook.Order.Returns(order);
        hook.ExecuteAsync(Arg.Any<PublishCompletedInput>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(result));
        return hook;
    }

    private static PublishCompletedAppService CreateSut(
        out IRuntimeInfoProvider runtimeInfoProvider,
        params IPublishCompletedHook[] hooks)
    {
        runtimeInfoProvider = Substitute.For<IRuntimeInfoProvider>();

        return new PublishCompletedAppService(
            hooks,
            runtimeInfoProvider,
            NullLogger<PublishCompletedAppService>.Instance);
    }
}
