using System;
using System.Diagnostics;
using System.Threading.Tasks;
using BBT.Workflow.Logging;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Application.Tests.Telemetry;

/// <summary>
/// Pins the activation-episode half of <see cref="WorkflowTraceLane"/>: seeding from the ambient
/// span, the preserve-vs-clear split between <c>Use</c> and <c>Reset</c> (the same split the anchor
/// has), inherit-by-default at a subflow handoff, the classify-once rule of <c>UseEpisode</c>, and
/// async flow — the property the whole design leans on.
/// </summary>
public sealed class WorkflowTraceLaneEpisodeTests : IDisposable
{
    private const string AnchorA = "00-11111111111111111111111111111111-1111111111111111-01";

    private static readonly ActivationEpisode Carried =
        new(new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero), TelemetryConstants.ActivationTriggers.Manual, "go", Partial: false);

    public void Dispose() => Activity.Current = null;

    private static Activity StartAmbient(string name = "server")
    {
        var activity = new Activity(name);
        activity.SetIdFormat(ActivityIdFormat.W3C);
        activity.Start();
        return activity;
    }

    [Fact]
    public void UseCurrentActivity_seeds_the_episode_from_the_ambient_span_start()
    {
        using var ambient = StartAmbient();

        using (WorkflowTraceLane.UseCurrentActivity())
        {
            var episode = WorkflowTraceLane.Episode.ShouldNotBeNull();
            episode.StartedAt.UtcDateTime.ShouldBe(ambient.StartTimeUtc);
            episode.Trigger.ShouldBe(TelemetryConstants.ActivationTriggers.Http);
            episode.TransitionKey.ShouldBeNull();
            episode.Partial.ShouldBeFalse();
        }

        WorkflowTraceLane.Episode.ShouldBeNull();
    }

    [Fact]
    public void Use_with_a_null_episode_preserves_the_enclosing_one()
    {
        using (WorkflowTraceLane.Use(AnchorA, episode: Carried))
        using (WorkflowTraceLane.Use(null))
        {
            WorkflowTraceLane.Episode.ShouldBe(Carried);
        }
    }

    [Fact]
    public void Reset_with_a_null_episode_clears_it()
    {
        // The job-handler entry policy: a payload from a build that predates episodes must not
        // inherit the Dapr callback request's episode.
        using (WorkflowTraceLane.Use(AnchorA, episode: Carried))
        using (WorkflowTraceLane.Reset(AnchorA))
        {
            WorkflowTraceLane.Episode.ShouldBeNull();
        }
    }

    [Fact]
    public void Reset_with_an_episode_installs_exactly_that_episode()
    {
        using (WorkflowTraceLane.Reset(AnchorA, episode: Carried))
        {
            WorkflowTraceLane.Episode.ShouldBe(Carried);
        }
    }

    [Fact]
    public void EnterChildLane_inherits_the_episode_by_default()
    {
        // A subflow handoff: the client polling the parent observes the leaf, so the child's
        // time-to-Active is measured from the parent's request.
        using var handoff = StartAmbient("PostCommit.StartSubflowJob");

        using (WorkflowTraceLane.Use(AnchorA, episode: Carried))
        using (WorkflowTraceLane.EnterChildLane())
        {
            WorkflowTraceLane.Current.ShouldBe(handoff.Id);
            WorkflowTraceLane.Episode.ShouldBe(Carried);
        }
    }

    [Fact]
    public void EnterChildLane_with_a_trigger_restarts_the_episode_at_the_handing_off_span()
    {
        // A trigger-family task: the caller's client never observes the target instance.
        using var handoff = StartAmbient("Trigger.Local");

        using (WorkflowTraceLane.Use(AnchorA, episode: Carried))
        using (WorkflowTraceLane.EnterChildLane(TelemetryConstants.ActivationTriggers.Trigger))
        {
            var episode = WorkflowTraceLane.Episode.ShouldNotBeNull();
            episode.StartedAt.UtcDateTime.ShouldBe(handoff.StartTimeUtc);
            episode.Trigger.ShouldBe(TelemetryConstants.ActivationTriggers.Trigger);
            episode.TransitionKey.ShouldBeNull();
        }
    }

    [Fact]
    public void UseEpisode_classifies_an_http_seeded_episode_without_moving_its_start()
    {
        using var ambient = StartAmbient();

        using (WorkflowTraceLane.UseCurrentActivity())
        using (WorkflowTraceLane.UseEpisode(TelemetryConstants.ActivationTriggers.Manual, "go"))
        {
            var episode = WorkflowTraceLane.Episode.ShouldNotBeNull();
            episode.StartedAt.UtcDateTime.ShouldBe(ambient.StartTimeUtc);
            episode.Trigger.ShouldBe(TelemetryConstants.ActivationTriggers.Manual);
            episode.TransitionKey.ShouldBe("go");
        }
    }

    [Fact]
    public void UseEpisode_keeps_an_already_classified_trigger_and_refreshes_only_the_key()
    {
        // An event delivery classifies itself before re-entering the generic transition entry
        // point; the generic entry must not relabel it `manual`.
        var eventEpisode = Carried with { Trigger = TelemetryConstants.ActivationTriggers.Event, TransitionKey = null };

        using (WorkflowTraceLane.Use(AnchorA, episode: eventEpisode))
        using (WorkflowTraceLane.UseEpisode(TelemetryConstants.ActivationTriggers.Manual, "go"))
        {
            var episode = WorkflowTraceLane.Episode.ShouldNotBeNull();
            episode.Trigger.ShouldBe(TelemetryConstants.ActivationTriggers.Event);
            episode.TransitionKey.ShouldBe("go");
            episode.StartedAt.ShouldBe(Carried.StartedAt);
        }
    }

    [Fact]
    public void UseEpisode_with_a_null_key_keeps_the_existing_key()
    {
        using (WorkflowTraceLane.Use(AnchorA, episode: Carried))
        using (WorkflowTraceLane.UseEpisode(TelemetryConstants.ActivationTriggers.Ack, transitionKey: null))
        {
            WorkflowTraceLane.Episode.ShouldNotBeNull().TransitionKey.ShouldBe("go");
        }
    }

    [Fact]
    public void UseEpisode_without_an_ambient_episode_seeds_one_starting_now()
    {
        var before = DateTimeOffset.UtcNow;

        using (WorkflowTraceLane.UseEpisode(TelemetryConstants.ActivationTriggers.Scheduled, "tick"))
        {
            var episode = WorkflowTraceLane.Episode.ShouldNotBeNull();
            episode.StartedAt.ShouldBeGreaterThanOrEqualTo(before);
            episode.StartedAt.ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow);
            episode.Trigger.ShouldBe(TelemetryConstants.ActivationTriggers.Scheduled);
            episode.TransitionKey.ShouldBe("tick");
            episode.Partial.ShouldBeFalse();
        }
    }

    [Fact]
    public async Task Episode_flows_across_await_and_into_Task_Run()
    {
        using (WorkflowTraceLane.Use(AnchorA, episode: Carried))
        {
            await Task.Yield();
            WorkflowTraceLane.Episode.ShouldBe(Carried);

            var observed = await Task.Run(() => WorkflowTraceLane.Episode);
            observed.ShouldBe(Carried);
        }
    }

    [Fact]
    public void FromCarrier_yields_null_without_a_start_and_defaults_a_missing_trigger()
    {
        ActivationEpisode.FromCarrier(null, "manual", "go").ShouldBeNull();

        var rebuilt = ActivationEpisode.FromCarrier(Carried.StartedAt, null, "go").ShouldNotBeNull();
        rebuilt.Trigger.ShouldBe(TelemetryConstants.ActivationTriggers.Http);
        rebuilt.TransitionKey.ShouldBe("go");
        rebuilt.Partial.ShouldBeFalse();
    }
}
