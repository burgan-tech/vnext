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
    /// Byte-exact round-trip for the two shapes that carry distinct information: a bare string and a
    /// roles-bearing object. A definition re-serialized after deserialization must be re-authorable.
    /// This also pins RoleGrant's <c>[JsonIgnore]</c> on IsDeny/IsAllow — without it the grant is
    /// written with "isDeny"/"isAllow" siblings, which <c>roleGrant</c>'s
    /// <c>additionalProperties: false</c> rejects on re-validation.
    /// </summary>
    [Fact]
    public void RoundTrip_IsByteExact_ForStringAndRolesBearingObject()
    {
        const string authored = """
            ["review",{"state":"approval","roles":[{"role":"backoffice.supervisor","grant":"allow"}]}]
            """;

        var reserialized = Write(Read(authored));

        reserialized.ShouldBe($"{{\"availableIn\":{authored.Trim()}}}");
    }

    /// <summary>
    /// A role-less <i>object</i> is normalized to the equivalent bare string rather than preserved.
    /// The written shape is derived from <see cref="AvailableInEntry.HasRoles"/>, not remembered, so
    /// `{state}` and `{state, roles: []}` both collapse — they encode exactly what `"state"` encodes.
    /// Same rule as <see cref="ViewDisplayJsonConverter"/> writing an SDI-only display as a bare string.
    /// <para>
    /// Asserted explicitly so the normalization is a pinned decision rather than an accident: if someone
    /// later adds an "authored as object" flag to defeat it, this test fails and forces the discussion.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("""[{"state":"review"}]""")]
    [InlineData("""[{"state":"review","roles":[]}]""")]
    public void Write_RoleLessObject_NormalizesToBareString(string authored)
    {
        var entry = Read(authored).ShouldHaveSingleItem();
        entry.State.ShouldBe("review");
        entry.HasRoles.ShouldBeFalse();

        Write(Read(authored)).ShouldBe("""{"availableIn":["review"]}""");
    }

    /// <summary>
    /// Normalization is idempotent and semantically lossless: whichever of the three equivalent role-less
    /// spellings is authored, the deserialized model and the re-serialized JSON are identical.
    /// </summary>
    [Fact]
    public void RoleLessForms_AreSemanticallyIdentical()
    {
        string[] spellings =
        [
            """["review"]""",
            """[{"state":"review"}]""",
            """[{"state":"review","roles":[]}]"""
        ];

        var outputs = spellings.Select(s => Write(Read(s))).Distinct().ToList();
        outputs.ShouldHaveSingleItem().ShouldBe("""{"availableIn":["review"]}""");

        // ...and the models agree, so nothing downstream can tell them apart either.
        foreach (var spelling in spellings)
        {
            var entry = Read(spelling).ShouldHaveSingleItem();
            entry.State.ShouldBe("review");
            entry.HasRoles.ShouldBeFalse();
        }
    }

    #endregion

    private sealed class Holder
    {
        [System.Text.Json.Serialization.JsonInclude]
        [System.Text.Json.Serialization.JsonConverter(typeof(AvailableInJsonConverter))]
        public List<AvailableInEntry> AvailableIn { get; set; } = [];
    }
}
