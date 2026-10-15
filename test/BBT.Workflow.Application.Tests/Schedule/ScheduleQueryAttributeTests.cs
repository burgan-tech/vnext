using System.Collections.Generic;
using Microsoft.AspNetCore.Http;
using BBT.Workflow.Orchestration.Controllers.Instances;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Schedule;

/// <summary>
/// Covers the query-string projection that seeds a scheduled instance.
/// <para>
/// A cron tick carries no body and Dapr forwards none of the component's metadata, so the query
/// string is the only channel for seed data. A regression here either leaks a control parameter into
/// instance data or changes what a schedule starts with — both silently, because nothing validates
/// the component YAML.
/// </para>
/// </summary>
public sealed class ScheduleQueryAttributeTests
{
    private static IQueryCollection Query(params (string Key, string?[] Values)[] parameters)
    {
        var dictionary = new Dictionary<string, Microsoft.Extensions.Primitives.StringValues>();
        foreach (var (key, values) in parameters)
            dictionary[key] = new Microsoft.Extensions.Primitives.StringValues(values);
        return new QueryCollection(dictionary);
    }

    /// <summary>Control parameters steer the endpoint and must never become instance data.</summary>
    [Fact]
    public void Reserved_parameters_are_excluded()
    {
        var attributes = InstanceController.ReadScheduleAttributes(Query(
            ("scheduleId", ["daily"]),
            ("sync", ["true"]),
            ("branchCode", ["99999"])));

        attributes.ShouldContainKeyAndValue("branchCode", "99999");
        attributes.ShouldNotContainKey("scheduleId");
        attributes.ShouldNotContainKey("sync");
    }

    /// <summary>
    /// A YAML author writing <c>?ScheduleId=</c> must not have it silently become seed data, so the
    /// reserved match is case-insensitive.
    /// </summary>
    [Fact]
    public void Reserved_parameters_are_matched_case_insensitively()
    {
        var attributes = InstanceController.ReadScheduleAttributes(Query(
            ("ScheduleId", ["daily"]),
            ("SYNC", ["true"]),
            ("Mode", ["full"])));

        attributes.Count.ShouldBe(1);
        attributes.ShouldContainKeyAndValue("Mode", "full");
    }

    /// <summary>
    /// A repeated parameter keeps its first value. Seed data is configuration: concatenating
    /// duplicates would hide a typo in the component YAML instead of making it visible.
    /// </summary>
    [Fact]
    public void Repeated_parameter_keeps_its_first_value()
    {
        var attributes = InstanceController.ReadScheduleAttributes(Query(
            ("branchCode", ["111", "222"])));

        attributes.ShouldContainKeyAndValue("branchCode", "111");
    }

    /// <summary>A valueless parameter contributes nothing rather than a null or empty attribute.</summary>
    [Fact]
    public void Valueless_parameter_is_dropped()
    {
        var attributes = InstanceController.ReadScheduleAttributes(Query(("flag", [null])));

        attributes.ShouldNotContainKey("flag");
    }

    /// <summary>No query string at all yields no attributes, which the service turns into no body.</summary>
    [Fact]
    public void Empty_query_yields_no_attributes()
    {
        InstanceController.ReadScheduleAttributes(new QueryCollection()).ShouldBeEmpty();
    }
}
