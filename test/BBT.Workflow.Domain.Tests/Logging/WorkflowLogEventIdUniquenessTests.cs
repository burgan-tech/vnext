using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BBT.Workflow.Logging;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Logging;

/// <summary>
/// Every log message in <c>WorkflowLogs</c> must own its EventId.
/// </summary>
/// <remarks>
/// <para>
/// An EventId is what an operator filters on, so two unrelated messages sharing one is not a
/// cosmetic clash: a dashboard or an alert keyed to that number silently matches both, and the
/// person reading it has no way to tell which event fired. Eighteen such collisions had accumulated
/// before this test existed.
/// </para>
/// <para>
/// Nothing else catches them. The source generator is happy, both Debug and Release build clean,
/// and the collision only shows up in a log pipeline in production. This test is the guard.
/// </para>
/// </remarks>
public class WorkflowLogEventIdUniquenessTests
{
    /// <summary>
    /// Every <c>[LoggerMessage]</c> on the class, as (EventId, method name).
    /// </summary>
    /// <remarks>
    /// Read from metadata rather than by parsing the source file: a test that greps its own repo
    /// breaks the moment the file moves, and it would not see a message declared anywhere else.
    /// </remarks>
    private static List<(int EventId, string Method)> Declarations() =>
    [
        .. typeof(WorkflowLogs)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Select(m => (Method: m, Attribute: m.GetCustomAttribute<LoggerMessageAttribute>()))
            .Where(x => x.Attribute is not null)
            .Select(x => (x.Attribute!.EventId, x.Method.Name))
    ];

    /// <summary>
    /// Guards the guard: if the attribute ever stops surviving into metadata this test would pass
    /// vacuously, which is worse than not having it.
    /// </summary>
    [Fact]
    public void TheLoggerMessageAttributesAreReadable()
    {
        Declarations().Count.ShouldBeGreaterThan(200);
    }

    /// <summary>
    /// The one legitimate exception: <c>JobFailed</c> is two overloads of the SAME event — one
    /// carrying an exception, one carrying a Result-pattern error string. They describe one thing
    /// with two payload shapes, so filtering on their shared id correctly returns both.
    /// </summary>
    private static readonly HashSet<string> SharedByOverload = ["JobFailed"];

    [Fact]
    public void NoTwoUnrelatedMessagesShareAnEventId()
    {
        var collisions = Declarations()
            .GroupBy(d => d.EventId)
            .Where(g => g.Select(d => d.Method).Distinct().Count() > 1)
            .Select(g => $"EventId {g.Key}: {string.Join(", ", g.Select(d => d.Method).Distinct().Order())}")
            .Order()
            .ToList();

        collisions.ShouldBeEmpty(
            "Two unrelated log messages share an EventId, so anything filtering on that number " +
            "matches both:" + Environment.NewLine + string.Join(Environment.NewLine, collisions));
    }

    /// <summary>
    /// Overloads that deliberately share an id must stay deliberate: same method name, same level,
    /// same id. A second method name sneaking onto a shared id is caught by the test above.
    /// </summary>
    [Fact]
    public void OverloadsThatShareAnEventId_AreOnlyTheOnesAllowedTo()
    {
        var shared = Declarations()
            .GroupBy(d => d.EventId)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g.Select(d => d.Method))
            .Distinct()
            .ToList();

        shared.ShouldBe(SharedByOverload.Order().ToList(), ignoreOrder: true);
    }

    /// <summary>
    /// EventIds carry meaning by range — 10xxx transitions, 20xxx instances, 40xxx events,
    /// 50xxx discovery — so an id outside every known band is almost certainly a typo.
    /// </summary>
    [Fact]
    public void EveryEventIdFallsInAKnownBand()
    {
        (int Lo, int Hi)[] bands = [(10_000, 19_999), (20_000, 29_999), (40_000, 49_999), (50_000, 59_999),
                                    (60_000, 69_999), (70_000, 79_999), (80_000, 89_999)];

        var strays = Declarations()
            .Where(d => !bands.Any(b => d.EventId >= b.Lo && d.EventId <= b.Hi))
            .Select(d => $"{d.Method} = {d.EventId}")
            .Order()
            .ToList();

        strays.ShouldBeEmpty();
    }
}
