using System.Linq;
using BBT.Workflow.Definitions;
using BBT.Workflow.Orchestration.Controllers.Instances;
using Microsoft.AspNetCore.Mvc.Routing;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Definitions;

/// <summary>
/// Locks down the two internal related-data route templates against the literal route strings on
/// <see cref="InstanceController"/>'s <c>[HttpGet]</c>/<c>[HttpPost]</c> attributes
/// (<c>GetRelatedDataAsync</c> / <c>GetRelatedDataBatchAsync</c>), read via reflection rather than
/// duplicated as local constants — a controller-side route change must fail this test, not silently
/// drift past it. <see cref="InstanceUrlTemplates"/> has no other test coverage, so a typo here would
/// only surface at cross-domain runtime.
/// </summary>
public class InstanceUrlTemplatesRelatedDataTests
{
    private static string ControllerRoute(string actionName) =>
        typeof(InstanceController)
            .GetMethod(actionName)!
            .GetCustomAttributes(inherit: false)
            .OfType<HttpMethodAttribute>()
            .Single()
            .Template!;

    [Fact]
    public void RelatedData_WithoutApiVersionPrefix_MatchesControllerRoute()
    {
        var url = InstanceUrlTemplates.RelatedData(
            "lending", "loan-application", "11111111-1111-1111-1111-111111111111");

        url.ShouldBe("/" + FillControllerRoute(
            ControllerRoute(nameof(InstanceController.GetRelatedDataAsync)),
            "lending", "loan-application", "11111111-1111-1111-1111-111111111111"));
    }

    [Fact]
    public void RelatedData_WithApiVersionPrefix_PrependsPrefixToControllerRoute()
    {
        var apiVersionPrefix = InstanceUrlTemplates.GetApiVersionPrefix("1.0");

        var url = InstanceUrlTemplates.RelatedData(
            "lending", "loan-application", "11111111-1111-1111-1111-111111111111", apiVersionPrefix);

        url.ShouldBe(apiVersionPrefix + "/" + FillControllerRoute(
            ControllerRoute(nameof(InstanceController.GetRelatedDataAsync)),
            "lending", "loan-application", "11111111-1111-1111-1111-111111111111"));
    }

    [Fact]
    public void RelatedDataBatch_WithoutApiVersionPrefix_MatchesControllerRoute()
    {
        var url = InstanceUrlTemplates.RelatedDataBatch("lending", "loan-application");

        url.ShouldBe("/" + FillControllerRoute(
            ControllerRoute(nameof(InstanceController.GetRelatedDataBatchAsync)), "lending", "loan-application"));
    }

    [Fact]
    public void RelatedDataBatch_WithApiVersionPrefix_PrependsPrefixToControllerRoute()
    {
        var apiVersionPrefix = InstanceUrlTemplates.GetApiVersionPrefix("1.0");

        var url = InstanceUrlTemplates.RelatedDataBatch("lending", "loan-application", apiVersionPrefix);

        url.ShouldBe(apiVersionPrefix + "/" + FillControllerRoute(
            ControllerRoute(nameof(InstanceController.GetRelatedDataBatchAsync)), "lending", "loan-application"));
    }

    /// <summary>
    /// Fills a controller-style <c>{name}</c> route template positionally, in the same order the
    /// controller action declares its <c>[FromRoute]</c> parameters (domain, workflow, [instance]).
    /// </summary>
    private static string FillControllerRoute(string controllerRoute, params string[] values)
    {
        var result = controllerRoute
            .Replace("{domain}", values[0])
            .Replace("{workflow}", values[1]);

        return values.Length > 2 ? result.Replace("{instance}", values[2]) : result;
    }
}
