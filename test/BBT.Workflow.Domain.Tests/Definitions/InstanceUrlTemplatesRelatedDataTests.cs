using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Definitions;

/// <summary>
/// Locks down the two internal related-data route templates against the literal route strings on
/// <c>InstanceController</c>'s <c>[HttpGet]</c>/<c>[HttpPost]</c> attributes
/// (<c>GetRelatedDataAsync</c> / <c>GetRelatedDataBatchAsync</c>). <see cref="InstanceUrlTemplates"/> has
/// no other test coverage, so a typo here would only surface at cross-domain runtime — as a remote
/// related-instance read silently 404ing (a reported failure, per the design) rather than a build break.
/// </summary>
public class InstanceUrlTemplatesRelatedDataTests
{
    // Must match InstanceController's [HttpGet("{domain}/workflows/{workflow}/instances/{instance}/internal/related-data")].
    private const string ControllerGetRoute = "{domain}/workflows/{workflow}/instances/{instance}/internal/related-data";

    // Must match InstanceController's [HttpPost("{domain}/workflows/{workflow}/internal/related-data/batch")].
    private const string ControllerPostRoute = "{domain}/workflows/{workflow}/internal/related-data/batch";

    [Fact]
    public void RelatedData_WithoutApiVersionPrefix_MatchesControllerRoute()
    {
        var url = InstanceUrlTemplates.RelatedData("lending", "loan-application", "11111111-1111-1111-1111-111111111111");

        url.ShouldBe("/lending/workflows/loan-application/instances/11111111-1111-1111-1111-111111111111/internal/related-data");
        url.ShouldBe("/" + FillControllerRoute(ControllerGetRoute, "lending", "loan-application", "11111111-1111-1111-1111-111111111111"));
    }

    [Fact]
    public void RelatedData_WithApiVersionPrefix_PrependsPrefixToControllerRoute()
    {
        var apiVersionPrefix = InstanceUrlTemplates.GetApiVersionPrefix("1.0");

        var url = InstanceUrlTemplates.RelatedData(
            "lending", "loan-application", "11111111-1111-1111-1111-111111111111", apiVersionPrefix);

        url.ShouldBe("api/v1.0/lending/workflows/loan-application/instances/11111111-1111-1111-1111-111111111111/internal/related-data");
        url.ShouldBe(apiVersionPrefix + "/" + FillControllerRoute(
            ControllerGetRoute, "lending", "loan-application", "11111111-1111-1111-1111-111111111111"));
    }

    [Fact]
    public void RelatedDataBatch_WithoutApiVersionPrefix_MatchesControllerRoute()
    {
        var url = InstanceUrlTemplates.RelatedDataBatch("lending", "loan-application");

        url.ShouldBe("/lending/workflows/loan-application/internal/related-data/batch");
        url.ShouldBe("/" + FillControllerRoute(ControllerPostRoute, "lending", "loan-application"));
    }

    [Fact]
    public void RelatedDataBatch_WithApiVersionPrefix_PrependsPrefixToControllerRoute()
    {
        var apiVersionPrefix = InstanceUrlTemplates.GetApiVersionPrefix("1.0");

        var url = InstanceUrlTemplates.RelatedDataBatch("lending", "loan-application", apiVersionPrefix);

        url.ShouldBe("api/v1.0/lending/workflows/loan-application/internal/related-data/batch");
        url.ShouldBe(apiVersionPrefix + "/" + FillControllerRoute(ControllerPostRoute, "lending", "loan-application"));
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
