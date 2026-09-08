using System;
using System.Linq;
using System.Reflection;
using BBT.Workflow.Instances;
using BBT.Workflow.Shared;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Instances;

/// <summary>
/// Pins the contract that the state function's <c>incident</c> block and <c>metadata.incident</c> are
/// the SAME shape.
/// </summary>
/// <remarks>
/// They are two independently hand-written DTOs — <see cref="IncidentHref"/> for the state body and
/// <see cref="IncidentInfoDto"/> for instance metadata — and "a client learns one shape" is a promise
/// made to clients in the API contract doc and in <c>vnext-meta</c>. Nothing but this test stops the
/// two drifting: adding a field to one and forgetting the other compiles, ships, and quietly turns one
/// shape back into two. The vnext-example lab compares the two payloads at runtime, but that needs the
/// whole stack up; this catches the divergence at build time.
/// </remarks>
public class IncidentBlockShapeTests
{
    [Fact]
    public void TheStateBlockAndMetadataBlock_ExposeTheSamePropertyNames()
    {
        Names<IncidentHref>().ShouldBe(Names<IncidentInfoDto>());
    }

    [Fact]
    public void BothBlocks_CarryTheFlagAndTwoLinks_AndNoIncidentContent()
    {
        foreach (var block in new[] { typeof(IncidentHref), typeof(IncidentInfoDto) })
        {
            var properties = Public(block).ToDictionary(p => p.Name, p => p.PropertyType);

            properties.Count.ShouldBe(3, $"{block.Name} should be exactly the flag plus two links");
            properties["HasActiveIncident"].ShouldBe(typeof(bool));

            // Nullable: absent from the JSON while nothing is open, which is how a client knows not to
            // follow it.
            properties["Active"].ShouldBe(typeof(ActiveIncidentHref));
            properties["History"].ShouldBe(typeof(IncidentHistoryHref));

            // The fields that used to be embedded. Reintroducing any of them here would put a read
            // back on the state function's hottest path and revive the stale-`active` ETag hole.
            foreach (var content in new[]
                     {
                         "Id", "ErrorCode", "Message", "State", "Transition", "Task", "ErrorLayer",
                         "StatusCode", "BoundaryAction", "BoundaryLevel", "TraceId", "CreatedAtUtc",
                         "StackTrace", "TotalCount", "Href", "HistoryHref", "RetryCount"
                     })
            {
                properties.ContainsKey(content).ShouldBeFalse(
                    $"{block.Name}.{content} embeds incident content — fetch it through Active.Href instead");
            }
        }
    }

    [Fact]
    public void NeitherLinkType_CarriesAnythingButAnHref()
    {
        foreach (var link in new[] { typeof(ActiveIncidentHref), typeof(IncidentHistoryHref) })
            Names(link).ShouldBe(["Href"], $"{link.Name} is a link, not a payload");
    }

    private static string[] Names<T>() => Names(typeof(T));

    private static string[] Names(Type type) =>
        Public(type).Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

    private static PropertyInfo[] Public(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
}
