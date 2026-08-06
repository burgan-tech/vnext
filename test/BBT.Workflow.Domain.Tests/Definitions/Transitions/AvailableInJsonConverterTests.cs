using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BBT.Workflow.Definitions;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Definitions.Transitions;

/// <summary>
/// Unit tests for <see cref="AvailableInJsonConverter"/> — the string-or-object union that lets
/// <c>availableIn</c> carry per-state role grants without breaking definitions authored as plain
/// state-key arrays.
/// </summary>
public class AvailableInJsonConverterTests
{
    private static readonly JsonSerializerOptions Options = JsonSerializerConstants.JsonOptions;

    private static List<AvailableInEntry> Read(string json) =>
        JsonSerializer.Deserialize<Holder>($"{{\"availableIn\":{json}}}", Options)!.AvailableIn;

    private static string Write(List<AvailableInEntry> entries) =>
        JsonSerializer.Serialize(new Holder { AvailableIn = entries }, Options);

    #region Read

    [Fact]
    public void Read_LegacyStringArray_ProducesRoleLessEntries()
    {
        var entries = Read("""["review", "approval"]""");

        entries.Select(e => e.State).ShouldBe(["review", "approval"]);
        entries.ShouldAllBe(e => !e.HasRoles);
    }

    [Fact]
    public void Read_ObjectForm_BindsStateAndRoles()
    {
        var entries = Read("""
            [ { "state": "approval", "roles": [ { "role": "backoffice.supervisor", "grant": "allow" },
                                                { "role": "$InstanceStarter", "grant": "deny" } ] } ]
            """);

        var entry = entries.ShouldHaveSingleItem();
        entry.State.ShouldBe("approval");
        entry.HasRoles.ShouldBeTrue();
        entry.Roles.Select(r => r.Role).ShouldBe(["backoffice.supervisor", "$InstanceStarter"]);
        entry.Roles.Single(r => r.Role == "$InstanceStarter").IsDeny.ShouldBeTrue();
    }

    [Fact]
    public void Read_MixedFormsInOneArray_AreBothHonoured()
    {
        var entries = Read("""
            [ "review",
              { "state": "approval", "roles": [ { "role": "backoffice.supervisor", "grant": "allow" } ] },
              { "state": "final" } ]
            """);

        entries.Select(e => e.State).ShouldBe(["review", "approval", "final"]);
        entries[0].HasRoles.ShouldBeFalse();
        entries[1].HasRoles.ShouldBeTrue();
        // Object form with no roles is equivalent to the bare string form
        entries[2].HasRoles.ShouldBeFalse();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    public void Read_NullOrEmpty_ProducesEmptyList(string json)
    {
        Read(json).ShouldBeEmpty();
    }

    [Fact]
    public void Read_UnknownShape_IsTreatedAsAbsentRatherThanThrowing()
    {
        // Consistent with ViewDisplayJsonConverter: a malformed value must not fail the whole definition.
        Read("\"review\"").ShouldBeEmpty();
        Read("123").ShouldBeEmpty();
    }

    [Fact]
    public void Read_SkipsBlankAndUnrecognisedItems()
    {
        var entries = Read("""[ "review", "", "   ", 42, "approval" ]""");

        entries.Select(e => e.State).ShouldBe(["review", "approval"]);
    }

    #endregion

    #region Write — authored shape round-trips

    [Fact]
    public void Write_RoleLessEntries_RoundTripAsBareStrings()
    {
        var json = Write([AvailableInEntry.FromState("review"), AvailableInEntry.FromState("approval")]);

        json.ShouldContain("""["review","approval"]""");
    }

    [Fact]
    public void Write_EntryWithRoles_RoundTripsAsObject()
    {
        var entry = AvailableInEntry.FromState("approval", [new RoleGrant("backoffice.supervisor", "allow")]);

        var json = Write([entry]);

        json.ShouldContain("\"state\":\"approval\"");
        json.ShouldContain("\"role\":\"backoffice.supervisor\"");
        json.ShouldContain("\"grant\":\"allow\"");
    }

    [Fact]
    public void Write_MixedEntries_PreserveEachEntrysShape()
    {
        var json = Write([
            AvailableInEntry.FromState("review"),
            AvailableInEntry.FromState("approval", [new RoleGrant("backoffice.supervisor", "allow")])
        ]);

        // First entry stays a bare string, second becomes an object
        json.ShouldContain("""["review",{""");
    }

    /// <summary>
    /// Byte-exact round-trip: a definition re-serialized after deserialization must be re-authorable.
    /// This also pins RoleGrant's <c>[JsonIgnore]</c> on IsDeny/IsAllow — without it the grant is
    /// written with "isDeny"/"isAllow" siblings, which <c>roleGrant</c>'s
    /// <c>additionalProperties: false</c> rejects on re-validation.
    /// </summary>
    [Fact]
    public void RoundTrip_PreservesBothFormsExactly()
    {
        const string authored = """
            ["review",{"state":"approval","roles":[{"role":"backoffice.supervisor","grant":"allow"}]}]
            """;

        var reserialized = Write(Read(authored));

        reserialized.ShouldBe($"{{\"availableIn\":{authored.Trim()}}}");
    }

    #endregion

    private sealed class Holder
    {
        [System.Text.Json.Serialization.JsonInclude]
        [System.Text.Json.Serialization.JsonConverter(typeof(AvailableInJsonConverter))]
        public List<AvailableInEntry> AvailableIn { get; set; } = [];
    }
}
