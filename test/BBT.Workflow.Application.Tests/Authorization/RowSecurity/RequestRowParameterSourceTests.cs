using System.Collections.Generic;
using System.Text.Json;
using BBT.Aether.Users;
using BBT.Workflow.Definitions.RowSecurity;
using BBT.Workflow.Instances;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Authorization.RowSecurity;

public class RequestRowParameterSourceTests
{
    private static RowParameterPath H(string n) => new(RowParameterSource.Headers, n);

    private static RequestRowParameterSource Src(
        Dictionary<string, string[]>? raw = null, Dictionary<string, string?>? flat = null,
        Dictionary<string, string[]>? rawQuery = null, Dictionary<string, string?>? query = null,
        ICurrentUser? user = null) =>
        new(raw, flat, rawQuery, query, user ?? Substitute.For<ICurrentUser>());

    [Fact]
    public void Source_PrefersRaw_OverFlattened() =>
        Src(raw: new() { ["x-b"] = ["A", "B"] }, flat: new() { ["x-b"] = "A" })
            .Get(H("x-b")).ShouldBe(["A", "B"]);

    [Fact]
    public void Source_FallsBackToFlattened_ForTaskInputs() =>
        Src(flat: new() { ["x-b"] = "A" }).Get(H("x-b")).ShouldBe(["A"]);

    [Fact]
    public void Source_HeaderNames_AreCaseInsensitive()
    {
        Src(raw: new() { ["x-b"] = ["A"] }).Get(H("X-B")).ShouldBe(["A"]);
        Src(flat: new() { ["x-b"] = "A" }).Get(H("X-B")).ShouldBe(["A"]);
    }

    [Fact]
    public void Source_Missing_IsEmpty() => Src().Get(H("x-b")).ShouldBeEmpty();

    [Fact]
    public void Source_Values_AreReturnedUntrimmedAndUnsplit() =>
        Src(raw: new() { ["x-b"] = [" A,B "] }).Get(H("x-b")).ShouldBe([" A,B "]);

    [Fact]
    public void Source_Query_PrefersRawThenFlat()
    {
        var q = new RowParameterPath(RowParameterSource.QueryParameters, "branch");
        Src(rawQuery: new() { ["branch"] = ["A", "B"] }, query: new() { ["branch"] = "A" }).Get(q).ShouldBe(["A", "B"]);
        Src(query: new() { ["BRANCH"] = "A" }).Get(q).ShouldBe(["A"]);
    }

    [Fact]
    public void Source_FlatNullValue_IsEmpty() =>
        Src(flat: new() { ["x-b"] = null }).Get(H("x-b")).ShouldBeEmpty();

    [Fact]
    public void Source_CurrentUser_ActorUserName()
    {
        var user = Substitute.For<ICurrentUser>();
        user.ActorUserName.Returns("alice");
        Src(user: user).Get(new(RowParameterSource.CurrentUser, "ActorUserName")).ShouldBe(["alice"]);

        user.ActorUserName.Returns((string?)null);
        Src(user: user).Get(new(RowParameterSource.CurrentUser, "ActorUserName")).ShouldBeEmpty();
    }

    [Fact]
    public void Source_CurrentUser_OtherFields()
    {
        var user = Substitute.For<ICurrentUser>();
        user.ActorUserId.Returns("id1");
        user.UserName.Returns("bob");
        user.Position.Returns("mgr");
        var s = Src(user: user);
        s.Get(new(RowParameterSource.CurrentUser, "ActorUserId")).ShouldBe(["id1"]);
        s.Get(new(RowParameterSource.CurrentUser, "UserName")).ShouldBe(["bob"]);
        s.Get(new(RowParameterSource.CurrentUser, "Position")).ShouldBe(["mgr"]);
        s.Get(new(RowParameterSource.CurrentUser, "Nope")).ShouldBeEmpty();
    }

    [Fact]
    public void GetInstanceInput_RawMembers_AreNotSerialized()
    {
        JsonSerializer.Serialize(new GetInstanceInput { RawHeaders = new Dictionary<string, string[]> { ["x"] = ["1"] } })
            .ShouldNotContain("RawHeaders");
        JsonSerializer.Serialize(new GetInstanceListInput { RawQueryParameters = new Dictionary<string, string[]> { ["x"] = ["1"] } })
            .ShouldNotContain("RawQueryParameters");
    }
}
