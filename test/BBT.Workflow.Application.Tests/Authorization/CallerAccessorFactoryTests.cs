using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Workflow.Scripting.Caller;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Authorization;

/// <summary>
/// <see cref="CallerAccessorFactory"/> maps every <see cref="CallerRoleOutcome"/> of the configured provider
/// onto the accessor rule scripts read, resolving through <c>ResolveRoleSetAsync</c> (the only member that
/// reports a failure) and only on the first read.
/// </summary>
public sealed class CallerAccessorFactoryTests
{
    private readonly ICallerRoleResolver _resolver = Substitute.For<ICallerRoleResolver>();
    private readonly CallerAccessorFactory _factory;
    private readonly Dictionary<string, string?> _headers = new() { ["sub"] = "u1" };

    public CallerAccessorFactoryTests() =>
        _factory = new CallerAccessorFactory(_resolver, NullLogger<CallerAccessorFactory>.Instance);

    [Fact]
    public async Task Resolved_ExposesTheRoles()
    {
        Answer(new CallerRoleResolution(["morph.approver"], CallerRoleOutcome.Resolved));

        var accessor = _factory.ForRequest(_headers);

        (await accessor.HasRoleAsync("morph.approver")).ShouldBeTrue();
        accessor.RolesFromHeader.ShouldBe(false);
    }

    [Fact]
    public async Task FromHeader_IsReported()
    {
        Answer(new CallerRoleResolution(["r"], CallerRoleOutcome.FromHeader));

        var accessor = _factory.ForRequest(_headers);

        await accessor.RolesAsync();
        accessor.RolesFromHeader.ShouldBe(true);
    }

    [Theory]
    [InlineData(CallerRoleOutcome.Empty)]
    [InlineData(CallerRoleOutcome.Skipped)]
    public async Task EmptyOrSkipped_IsACallerWithNoRoles(CallerRoleOutcome outcome)
    {
        Answer(new CallerRoleResolution([], outcome));

        var accessor = _factory.ForRequest(_headers);

        (await accessor.RolesAsync()).ShouldBeEmpty();
        accessor.ResolutionFailed.ShouldBeFalse();
    }

    [Fact]
    public async Task Failed_ThrowsAndMarksTheAccessor()
    {
        Answer(new CallerRoleResolution([], CallerRoleOutcome.Failed, "timeout"));

        var accessor = _factory.ForRequest(_headers);

        await Should.ThrowAsync<CallerRolesUnavailableException>(accessor.HasRoleAsync("x"));
        accessor.ResolutionFailed.ShouldBeTrue();
    }

    [Fact]
    public async Task ResolvesOnFirstRead_WithTheRequestHeaders_Once()
    {
        Answer(new CallerRoleResolution(["r"], CallerRoleOutcome.Resolved));

        var accessor = _factory.ForRequest(_headers);
        await _resolver.DidNotReceiveWithAnyArgs().ResolveRoleSetAsync(default);

        await accessor.HasRoleAsync("r");
        await accessor.HasRoleAsync("s");

        await _resolver.Received(1).ResolveRoleSetAsync(
            Arg.Is<IReadOnlyDictionary<string, string?>?>(h => h != null && h["sub"] == "u1"),
            Arg.Any<CancellationToken>());
        await _resolver.DidNotReceiveWithAnyArgs().ResolveRolesAsync(default);
    }

    [Fact]
    public void ForHop_SystemActor_IsUnavailable_UserActor_IsTheRequestCaller()
    {
        _factory.ForHop(BBT.Workflow.Shared.ExecutionActor.System, _headers).ShouldBeSameAs(NullCallerAccessor.Instance);
        _factory.ForHop(BBT.Workflow.Shared.ExecutionActor.User, _headers).ShouldBeOfType<CallerAccessor>();
    }

    private void Answer(CallerRoleResolution resolution) =>
        _resolver.ResolveRoleSetAsync(Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(resolution));
}
