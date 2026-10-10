using System;
using BBT.Aether.MultiSchema;
using BBT.Workflow.Runtime;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Runtime;

/// <summary>
/// Pins <see cref="DomainSchemaNameFormatter"/>: identical names for a single domain and for the primary
/// of a pool (backward compatibility of existing databases), <c>&lt;domain&gt;_</c> for a co-hosted
/// domain, shared <c>sys_queues</c>/<c>sys_metrics</c>, idempotence and the length guard.
/// </summary>
public sealed class DomainSchemaNameFormatterTests
{
    private static readonly DefaultSchemaNameFormatter Default = new();

    private static DomainSchemaNameFormatter Pool() =>
        new(new RuntimeInfoProvider("1.0.0", domains: "mobile,fraud", domain: "staff"));

    [Theory]
    [InlineData("navigations")]
    [InlineData("sys-flows")]
    [InlineData("account-opening")]
    [InlineData("sys_queues")]
    public void SingleDomain_ProducesExactlyTheDefaultNames(string schema)
    {
        var sut = new DomainSchemaNameFormatter(new RuntimeInfoProvider("1.0.0", domains: null, domain: "core"));

        sut.Format(schema).ShouldBe(Default.Format(schema));
    }

    [Theory]
    [InlineData("navigations")]
    [InlineData("sys-flows")]
    public void PrimaryDomainOfAPool_ProducesExactlyTheDefaultNames(string schema)
    {
        var sut = Pool();

        sut.Format(schema).ShouldBe(Default.Format(schema));
        using (DomainScope.Begin("staff"))
        {
            sut.Format(schema).ShouldBe(Default.Format(schema));
        }
    }

    [Fact]
    public void CoHostedDomain_GetsItsPrefix_ForFlowAndDefinitionSchemas()
    {
        var sut = Pool();

        using (DomainScope.Begin("Mobile"))
        {
            sut.Format("navigations").ShouldBe("mobile_navigations");
            sut.Format("sys-flows").ShouldBe("mobile_sys_flows");
        }

        using (DomainScope.Begin("fraud"))
        {
            sut.Format("navigations").ShouldBe("fraud_navigations");
        }
    }

    [Theory]
    [InlineData("sys_queues")]
    [InlineData("sys_metrics")]
    public void SharedSchemas_AreNeverPrefixed(string schema)
    {
        using (DomainScope.Begin("mobile"))
        {
            Pool().Format(schema).ShouldBe(schema);
        }
    }

    [Fact]
    public void Format_IsIdempotent()
    {
        var sut = Pool();

        using (DomainScope.Begin("mobile"))
        {
            sut.Format(sut.Format("navigations")).ShouldBe("mobile_navigations");
        }
    }

    [Fact]
    public void ForeignDomainScope_FallsBackToThePrimary()
    {
        using (DomainScope.Begin("sales"))
        {
            Pool().Format("navigations").ShouldBe("navigations");
        }
    }

    [Fact]
    public void PrefixedNameOverThePostgresLimit_Throws()
    {
        using (DomainScope.Begin("mobile"))
        {
            Should.Throw<InvalidOperationException>(() => Pool().Format(new string('a', 60)));
        }
    }
}
