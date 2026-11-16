using System.Net;
using BBT.Aether.AspNetCore.ExceptionHandling;
using BBT.Aether.Results;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace BBT.Workflow.HttpApi.Results;

/// <summary>
/// Pins the HTTP status of a list filter that passed the boundary validator but could not be
/// compiled into SQL (vnext#1080).
/// </summary>
/// <remarks>
/// <c>InstanceQueryAppService</c> raises that failure as a <c>UserFriendlyException</c> carrying
/// <see cref="WorkflowErrorCodes.InstanceFilterInvalid"/>. Aether's <c>TryAsync</c> turns any
/// user-friendly exception into a <c>Failure</c>-prefixed error, and the <c>Failure</c> prefix maps
/// to 500, so the "Validation:" text inside the code never decided the status. Only the explicit
/// mapping row in <c>AddExceptionHandling</c> makes it 400.
/// </remarks>
public sealed class InstanceFilterInvalidStatusMappingTests
{
    [Fact]
    public void InstanceFilterInvalid_raised_as_user_friendly_failure_maps_to_400()
    {
        var finder = CreateFinder();

        // The exact shape TryAsync produces from the app service's UserFriendlyException.
        var error = Error.Failure(WorkflowErrorCodes.InstanceFilterInvalid, "Filter could not be applied: Invalid Guid value: not-an-id");

        finder.GetStatusCodeFromError(error).ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public void An_unmapped_user_friendly_failure_still_maps_to_500()
    {
        // Guards the premise above: without a mapping row a Failure-prefixed error is a 500.
        var finder = CreateFinder();

        finder.GetStatusCodeFromError(Error.Failure("Validation:999999", "unmapped"))
            .ShouldBe(HttpStatusCode.InternalServerError);
    }

    private static DefaultHttpExceptionStatusCodeFinder CreateFinder()
    {
        var services = new ServiceCollection();
        services.AddExceptionHandling();
        using var provider = services.BuildServiceProvider();

        return new DefaultHttpExceptionStatusCodeFinder(
            provider.GetRequiredService<IOptions<AetherExceptionHttpStatusCodeOptions>>());
    }
}
