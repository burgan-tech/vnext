using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BBT.Aether;
using BBT.Aether.Results;
using BBT.Workflow.Instances;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Scripting.Related;

public class RelatedInstanceAccessorParentTests
{
    private static readonly Guid ChildId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ParentId = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static Instance ChildWithParentMetadata(object parentIdValue)
    {
        var instance = Instance.Create(ChildId, "kyc-flow", "1.0.0", "customer-42");
        instance.SetMetaData(new ExtraPropertyDictionary
        {
            [DomainConsts.MetaDataKeys.Id] = parentIdValue,
            [DomainConsts.MetaDataKeys.Key] = "customer-42",
            [DomainConsts.MetaDataKeys.Domain] = "lending",
            [DomainConsts.MetaDataKeys.Flow] = "loan-application",
            [DomainConsts.MetaDataKeys.Version] = "2.1.0"
        });
        return instance;
    }

    private static RelatedInstanceSnapshot ParentSnapshot() => new()
    {
        InstanceId = ParentId,
        Key = "customer-42",
        Domain = "lending",
        Flow = "loan-application",
        FlowVersion = "2.1.0",
        Status = "A",
        CurrentState = "awaiting-kyc",
        IsCompleted = false,
        Data = null
    };

    private static RelatedInstanceAccessor CreateAccessor(
        Instance instance,
        IRelatedInstanceReader reader,
        IInstanceCorrelationRepository? correlationRepository = null) =>
        new(
            instance,
            reader,
            correlationRepository ?? Substitute.For<IInstanceCorrelationRepository>(),
            new RelatedAccessOptions(),
            NullLogger.Instance);

    [Fact]
    public void HasParent_ShouldBeFalse_WhenNoParentMetadata()
    {
        var instance = Instance.Create(ChildId, "kyc-flow", "1.0.0");
        var accessor = CreateAccessor(instance, Substitute.For<IRelatedInstanceReader>());

        accessor.HasParent.ShouldBeFalse();
    }

    [Fact]
    public async Task ParentAsync_ShouldReturnNull_WhenNoParentMetadata()
    {
        var instance = Instance.Create(ChildId, "kyc-flow", "1.0.0");
        var reader = Substitute.For<IRelatedInstanceReader>();
        var accessor = CreateAccessor(instance, reader);

        var parent = await accessor.ParentAsync(CancellationToken.None);

        parent.ShouldBeNull();
        await reader.DidNotReceiveWithAnyArgs().ReadAsync(default!, default);
    }

    [Fact]
    public async Task ParentAsync_ShouldReturnNull_WhenParentIdIsUnparsable()
    {
        var instance = ChildWithParentMetadata("not-a-guid");
        var reader = Substitute.For<IRelatedInstanceReader>();
        var accessor = CreateAccessor(instance, reader);

        accessor.HasParent.ShouldBeFalse();
        (await accessor.ParentAsync(CancellationToken.None)).ShouldBeNull();
    }

    public static TheoryData<object> ParentIdRepresentations() =>
        new()
        {
            ParentId,
            ParentId.ToString(),
            JsonSerializer.SerializeToElement(ParentId)
        };

    [Theory]
    [MemberData(nameof(ParentIdRepresentations))]
    public async Task ParentAsync_ShouldResolveParent_ForEveryStoredIdRepresentation(object parentIdValue)
    {
        var instance = ChildWithParentMetadata(parentIdValue);
        var reader = Substitute.For<IRelatedInstanceReader>();
        reader.ReadAsync(Arg.Any<RelatedInstanceRef>(), Arg.Any<CancellationToken>())
            .Returns(Result<RelatedInstanceSnapshot?>.Ok(ParentSnapshot()));
        var accessor = CreateAccessor(instance, reader);

        accessor.HasParent.ShouldBeTrue();
        var parent = await accessor.ParentAsync(CancellationToken.None);

        parent.ShouldNotBeNull();
        parent!.InstanceId.ShouldBe(ParentId);
        parent.Domain.ShouldBe("lending");
        parent.Flow.ShouldBe("loan-application");
        parent.Status.ShouldBe("A");
        parent.CurrentState.ShouldBe("awaiting-kyc");
    }

    [Fact]
    public async Task ParentAsync_ShouldLeaveCorrelationFieldsNull()
    {
        var instance = ChildWithParentMetadata(ParentId);
        var reader = Substitute.For<IRelatedInstanceReader>();
        reader.ReadAsync(Arg.Any<RelatedInstanceRef>(), Arg.Any<CancellationToken>())
            .Returns(Result<RelatedInstanceSnapshot?>.Ok(ParentSnapshot()));
        var accessor = CreateAccessor(instance, reader);

        var parent = await accessor.ParentAsync(CancellationToken.None);

        parent!.CorrelationCompleted.ShouldBeNull();
        parent.TerminalOutcome.ShouldBeNull();
        parent.SubFlowType.ShouldBeNull();
    }

    [Fact]
    public async Task ParentAsync_ShouldPassTheRefFromMetadata()
    {
        var instance = ChildWithParentMetadata(ParentId);
        var reader = Substitute.For<IRelatedInstanceReader>();
        reader.ReadAsync(Arg.Any<RelatedInstanceRef>(), Arg.Any<CancellationToken>())
            .Returns(Result<RelatedInstanceSnapshot?>.Ok(ParentSnapshot()));
        var accessor = CreateAccessor(instance, reader);

        await accessor.ParentAsync(CancellationToken.None);

        await reader.Received(1).ReadAsync(
            Arg.Is<RelatedInstanceRef>(r =>
                r.InstanceId == ParentId &&
                r.Domain == "lending" &&
                r.Flow == "loan-application" &&
                r.FlowVersion == "2.1.0"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void HasParent_ShouldBeFalse_WhenParentIdIsEmptyGuid()
    {
        var instance = ChildWithParentMetadata(Guid.Empty);
        var accessor = CreateAccessor(instance, Substitute.For<IRelatedInstanceReader>());

        accessor.HasParent.ShouldBeFalse();
    }

    [Fact]
    public void HasParent_ShouldBeFalse_WhenParentIdIsNonStringJson()
    {
        // Exercises the `when element.ValueKind == JsonValueKind.String` guard in ReadGuid.
        // Without the guard, TryGetGuid throws InvalidOperationException on a non-string element.
        var instance = ChildWithParentMetadata(JsonSerializer.SerializeToElement(42));
        var accessor = CreateAccessor(instance, Substitute.For<IRelatedInstanceReader>());

        accessor.HasParent.ShouldBeFalse();
    }

    [Fact]
    public void HasParent_ShouldBeFalse_WhenParentFlowIsMissing()
    {
        // parent.id and parent.domain resolve, parent.flow does not — forces the
        // `IsNullOrWhiteSpace(domain) || IsNullOrWhiteSpace(flow)` branch in BuildParentRef.
        var instance = Instance.Create(ChildId, "kyc-flow", "1.0.0");
        instance.SetMetaData(new ExtraPropertyDictionary
        {
            [DomainConsts.MetaDataKeys.Id] = ParentId,
            [DomainConsts.MetaDataKeys.Domain] = "lending"
        });
        var accessor = CreateAccessor(instance, Substitute.For<IRelatedInstanceReader>());

        accessor.HasParent.ShouldBeFalse();
    }

    [Fact]
    public void HasParent_ShouldBeFalse_WhenParentDomainHasUnexpectedStoredType()
    {
        // ReadString must fail closed rather than fabricating a ToString() value, which would
        // otherwise pass BuildParentRef's non-empty check and point at a domain that never existed.
        var instance = Instance.Create(ChildId, "kyc-flow", "1.0.0");
        instance.SetMetaData(new ExtraPropertyDictionary
        {
            [DomainConsts.MetaDataKeys.Id] = ParentId,
            [DomainConsts.MetaDataKeys.Domain] = new object(),
            [DomainConsts.MetaDataKeys.Flow] = "loan-application"
        });
        var accessor = CreateAccessor(instance, Substitute.For<IRelatedInstanceReader>());

        accessor.HasParent.ShouldBeFalse();
    }

    [Fact]
    public async Task ParentAsync_ShouldReturnNull_WhenReaderReportsNotFound()
    {
        var instance = ChildWithParentMetadata(ParentId);
        var reader = Substitute.For<IRelatedInstanceReader>();
        reader.ReadAsync(Arg.Any<RelatedInstanceRef>(), Arg.Any<CancellationToken>())
            .Returns(Result<RelatedInstanceSnapshot?>.Ok(null));
        var accessor = CreateAccessor(instance, reader);

        (await accessor.ParentAsync(CancellationToken.None)).ShouldBeNull();
    }

    [Fact]
    public async Task ParentAsync_ShouldThrow_WhenReaderFails()
    {
        var instance = ChildWithParentMetadata(ParentId);
        var reader = Substitute.For<IRelatedInstanceReader>();
        reader.ReadAsync(Arg.Any<RelatedInstanceRef>(), Arg.Any<CancellationToken>())
            .Returns(Result<RelatedInstanceSnapshot?>.Fail(
                Error.Failure("RELATED_READ", "endpoint unreachable")));
        var accessor = CreateAccessor(instance, reader);

        var exception = await Should.ThrowAsync<RelatedInstanceAccessException>(
            () => accessor.ParentAsync(CancellationToken.None));

        exception.Message.ShouldContain("endpoint unreachable");
    }
}
