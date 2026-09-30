using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Aether.Users;
using BBT.Workflow.Authorization;
using BBT.Workflow.Controllers.Instances;
using BBT.Workflow.Instances;
using BBT.Workflow.Instances.DTOs;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Functions;

/// <summary>
/// <see cref="GetInstanceDataInput.SystemRead"/> is SERVER-ONLY: the public data function must never
/// produce a system read, whatever the request carries. The DTO is not model-bound and the property is
/// init-only, so the handler is the one place the value is decided — pinned here.
/// </summary>
public sealed class DataFunctionHandlerSystemReadTests
{
    [Theory]
    [InlineData("systemRead", "true")]
    [InlineData("SystemRead", "True")]
    [InlineData("x-system-read", "1")]
    public async Task PublicDataFunction_NeverBuildsASystemRead(string name, string value)
    {
        var queryService = Substitute.For<IInstanceQueryAppService>();
        GetInstanceDataInput? captured = null;
        queryService.GetInstanceDataAsync(Arg.Do<GetInstanceDataInput>(i => captured = i), Arg.Any<CancellationToken>())
            .Returns(ConditionalResult<GetInstanceDataOutput>.NotModified());
        var roles = Substitute.For<ICallerRoleResolver>();
        roles.ResolveRolesAsync(Arg.Any<IReadOnlyDictionary<string, string?>?>(), Arg.Any<CancellationToken>())
            .Returns(Result<string[]?>.Ok(["viewer"]));
        var handler = new DataFunctionHandler(queryService, roles);

        var request = new InstanceFunctionRequest(
            "core", "flow", "instance-1", new FunctionQueryParameters(), null,
            Headers: new Dictionary<string, string?> { [name] = value },
            QueryParameters: new Dictionary<string, string?> { [name] = value },
            CurrentUser: Substitute.For<ICurrentUser>(),
            HttpContext: new DefaultHttpContext());

        await handler.HandleAsync(request, CancellationToken.None);

        captured.ShouldNotBeNull();
        captured!.SystemRead.ShouldBeFalse();
    }
}
