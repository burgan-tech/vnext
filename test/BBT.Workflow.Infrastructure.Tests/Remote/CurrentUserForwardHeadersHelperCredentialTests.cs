using System.Collections.Generic;
using System.Net.Http;
using BBT.Workflow.Remote;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Remote;

/// <summary>
/// Pins the cross-domain exit of the instance-call credential rule: a blank input credential removes
/// the ambient user's header and sends nothing.
/// </summary>
public class CurrentUserForwardHeadersHelperCredentialTests
{
    [Fact]
    public void BlankInputCredential_RemovesTheAmbientUsersHeader()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "http://x/");

        CurrentUserForwardHeadersHelper.MergeIntoRequest(
            request,
            new Dictionary<string, string?> { ["sub"] = "ambient", ["act_sub"] = "ambient-actor" },
            new Dictionary<string, string?> { ["sub"] = "" });

        request.Headers.Contains("sub").ShouldBeFalse();
        request.Headers.GetValues("act_sub").ShouldBe(new[] { "ambient-actor" });
    }
}
