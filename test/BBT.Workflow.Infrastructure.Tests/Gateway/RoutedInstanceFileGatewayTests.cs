using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether.Results;
using BBT.Workflow.Files;
using BBT.Workflow.Gateway;
using BBT.Workflow.Runtime;
using BBT.Workflow.Scripting.Functions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Infrastructure.Tests.Gateway;

/// <summary>
/// Covers <see cref="RoutedInstanceFileGateway"/>'s local-vs-remote decision, the DI registrations behind it and the
/// <see cref="ScriptFileReader"/> mapping on top of it.
/// </summary>
public sealed class RoutedInstanceFileGatewayTests
{
    private readonly IInstanceFileGateway _local = Substitute.For<IInstanceFileGateway>();
    private readonly IInstanceFileGateway _remote = Substitute.For<IInstanceFileGateway>();
    private readonly RoutedInstanceFileGateway _sut;

    private static readonly FileHandle Handle = new(
        "vnext-blob-local", "f-1", "p.pdf", "application/pdf", 3, "abc", new FileOwner("core", "kyc", "i-1"));

    public RoutedInstanceFileGatewayTests()
    {
        var runtime = Substitute.For<IRuntimeInfoProvider>();
        runtime.IsDomainMatch("core").Returns(true);
        runtime.IsDomainMatch("partner").Returns(false);
        _sut = new RoutedInstanceFileGateway(runtime, _local, _remote);
    }

    [Fact]
    public async Task SameDomain_GoesLocal()
    {
        await _sut.ReadAsync("core", "kyc", "i-1", "f-1", CancellationToken.None);

        await _local.Received(1).ReadAsync("core", "kyc", "i-1", "f-1", Arg.Any<CancellationToken>());
        await _remote.DidNotReceiveWithAnyArgs().ReadAsync(default!, default!, default!, default!, default);
    }

    [Fact]
    public async Task OtherDomain_GoesRemote()
    {
        await _sut.ReadAsync("partner", "kyc", "i-1", "f-1", CancellationToken.None);

        await _remote.Received(1).ReadAsync("partner", "kyc", "i-1", "f-1", Arg.Any<CancellationToken>());
        await _local.DidNotReceiveWithAnyArgs().ReadAsync(default!, default!, default!, default!, default);
    }

    [Fact]
    public void AddInstanceGatewayServices_RegistersTheRouterBothKeyedSidesAndTheScriptReader()
    {
        var services = new ServiceCollection();

        services.AddInstanceGatewayServices();

        services.ShouldContain(d => d.ServiceType == typeof(IInstanceFileGateway) && !d.IsKeyedService
                                    && d.ImplementationType == typeof(RoutedInstanceFileGateway));
        services.ShouldContain(d => d.ServiceType == typeof(IInstanceFileGateway) && d.IsKeyedService
                                    && Equals(d.ServiceKey, InstanceFileGatewayKeys.Local)
                                    && d.KeyedImplementationType == typeof(LocalInstanceFileGateway));
        var remote = services.Single(d => d.ServiceType == typeof(IInstanceFileGateway) && d.IsKeyedService
                                          && Equals(d.ServiceKey, InstanceFileGatewayKeys.Remote));
        // A factory alias onto the AddRemoteService registration (resilience stack), never a direct type.
        remote.KeyedImplementationType.ShouldBeNull();
        remote.KeyedImplementationFactory.ShouldNotBeNull();
        services.ShouldContain(d => d.ServiceType == typeof(IScriptFileReader)
                                    && d.ImplementationType == typeof(ScriptFileReader));
    }

    [Fact]
    public async Task ScriptFileReader_MapsTheHandleAndBytes()
    {
        _local.ReadAsync("core", "kyc", "i-1", "f-1", Arg.Any<CancellationToken>())
            .Returns(Result<InstanceFileContent>.Ok(new InstanceFileContent(Handle, "passport", [1, 2, 3], false)));

        var file = await new ScriptFileReader(_sut).ReadAsync("core", "kyc", "i-1", "f-1", CancellationToken.None);

        file.Content.ShouldBe(new byte[] { 1, 2, 3 });
        file.Name.ShouldBe("p.pdf");
        file.MimeType.ShouldBe("application/pdf");
        file.Size.ShouldBe(3);
        file.ETag.ShouldBe("abc");
    }

    [Fact]
    public async Task ScriptFileReader_FailureThrowsWithTheErrorCode()
    {
        _remote.ReadAsync("partner", "kyc", "i-1", "f-1", Arg.Any<CancellationToken>())
            .Returns(Result<InstanceFileContent>.Fail(Logging.WorkflowErrors.FileNotFound("f-1")));

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => new ScriptFileReader(_sut).ReadAsync("partner", "kyc", "i-1", "f-1", CancellationToken.None));

        ex.Message.ShouldContain(WorkflowErrorCodes.FileNotFound);
    }
}
