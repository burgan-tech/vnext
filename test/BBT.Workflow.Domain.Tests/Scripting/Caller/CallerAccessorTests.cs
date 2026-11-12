using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Scripting.Caller;

/// <summary>
/// <see cref="CallerAccessor"/>, <see cref="NullCallerAccessor"/> and their place on
/// <see cref="ScriptContext"/>: lazy single resolution, sticky failure, closing, and never-null defaults.
/// </summary>
public sealed class CallerAccessorTests
{
    [Fact]
    public async Task NothingIsResolved_UntilARead_ThenOnlyOnce()
    {
        var calls = 0;
        var accessor = new CallerAccessor(() =>
        {
            calls++;
            return Task.FromResult(new CallerRoleSnapshot(["a", "b"], false, false));
        });

        accessor.ResolutionStarted.ShouldBeFalse();
        (await accessor.HasRoleAsync("a")).ShouldBeTrue();
        (await accessor.HasRoleAsync("c")).ShouldBeFalse();
        (await accessor.RolesAsync()).ShouldBe(["a", "b"]);

        calls.ShouldBe(1);
        accessor.ResolutionFailed.ShouldBeFalse();
        accessor.RolesFromHeader.ShouldBe(false);
    }

    [Fact]
    public async Task HasRole_IsOrdinal()
    {
        var accessor = new CallerAccessor(() => Task.FromResult(new CallerRoleSnapshot(["Morph.Approver"], false, false)));

        (await accessor.HasRoleAsync("morph.approver")).ShouldBeFalse();
    }

    [Fact]
    public async Task FailedAnswer_ThrowsOnEveryRead_AndStaysFailed()
    {
        var accessor = new CallerAccessor(() => Task.FromResult(new CallerRoleSnapshot([], true, false)));

        await Should.ThrowAsync<CallerRolesUnavailableException>(accessor.HasRoleAsync("x"));
        await Should.ThrowAsync<CallerRolesUnavailableException>(accessor.RolesAsync());
        accessor.ResolutionFailed.ShouldBeTrue();
        accessor.RolesFromHeader.ShouldBeNull();
    }

    [Fact]
    public async Task ResolverException_IsAFailedAnswer()
    {
        var accessor = new CallerAccessor(() => throw new InvalidOperationException("boom"));

        var ex = await Should.ThrowAsync<CallerRolesUnavailableException>(accessor.RolesAsync());
        ex.InnerException.ShouldBeOfType<InvalidOperationException>();
        accessor.ResolutionFailed.ShouldBeTrue();
    }

    [Fact]
    public async Task HeaderSourcedSet_IsReported()
    {
        var accessor = new CallerAccessor(() => Task.FromResult(new CallerRoleSnapshot(["r"], false, true)));

        await accessor.RolesAsync();
        accessor.RolesFromHeader.ShouldBe(true);
    }

    [Fact]
    public async Task Closed_IsUnavailable_AndReadsThrow()
    {
        var accessor = new CallerAccessor(() => Task.FromResult(new CallerRoleSnapshot(["r"], false, false)));

        accessor.Close();

        accessor.IsAvailable.ShouldBeFalse();
        await Should.ThrowAsync<CallerRolesUnavailableException>(accessor.HasRoleAsync("r"));
        accessor.ResolutionStarted.ShouldBeFalse();
        accessor.ResolutionFailed.ShouldBeFalse();
    }

    [Fact]
    public async Task NullAccessor_IsUnavailable_NotEmpty()
    {
        var accessor = NullCallerAccessor.Instance;

        accessor.IsAvailable.ShouldBeFalse();
        accessor.ResolutionFailed.ShouldBeFalse();
        await Should.ThrowAsync<CallerRolesUnavailableException>(() => accessor.RolesAsync());
        await Should.ThrowAsync<CallerRolesUnavailableException>(() => accessor.HasRoleAsync("x"));
    }

    [Fact]
    public void ScriptContext_DefaultsToTheNullAccessor()
    {
        var context = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance).Build();

        context.Caller.ShouldBeSameAs(NullCallerAccessor.Instance);
    }

    [Fact]
    public void ScriptContext_CarriesTheAccessor_AndBranchesShareIt()
    {
        var accessor = new CallerAccessor(() => Task.FromResult(new CallerRoleSnapshot([], false, false)));
        var context = new ScriptContext.Builder(NullLogger<ScriptContext>.Instance).SetCaller(accessor).Build();

        context.Caller.ShouldBeSameAs(accessor);
        context.CreateParallelBranch().Caller.ShouldBeSameAs(accessor);
    }
}
