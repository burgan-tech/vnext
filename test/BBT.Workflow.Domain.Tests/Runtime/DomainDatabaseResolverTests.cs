using System;
using BBT.Aether;
using BBT.Workflow.Runtime;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace BBT.Workflow.Domain.Tests.Runtime;

/// <summary>
/// Pins <see cref="DomainDatabaseResolver"/>: with no <c>ConnectionStrings:Domains</c> every domain uses the
/// default database (today's behaviour); a dedicated domain gets its own; one representative per distinct
/// database; misconfiguration fails at startup.
/// </summary>
public sealed class DomainDatabaseResolverTests
{
    private const string Default = "Host=db;Database=vNext_Staff";
    private const string Mobile = "Host=db;Database=vNext_Mobile";

    private static DomainDatabaseResolver Create(Action<DomainDatabaseOptions>? configure = null, string? domains = "mobile,fraud")
    {
        var options = new DomainDatabaseOptions { DefaultConnectionString = Default };
        configure?.Invoke(options);
        return new DomainDatabaseResolver(new RuntimeInfoProvider("1.0.0", domains, "staff"), Options.Create(options));
    }

    [Fact]
    public void WithoutDedicatedDatabases_EveryDomainUsesTheDefault()
    {
        var sut = Create();

        sut.ConnectionStringFor("mobile").ShouldBe(Default);
        sut.HasDedicatedDatabase("mobile").ShouldBeFalse();
        sut.SameDatabase("staff", "mobile").ShouldBeTrue();
        sut.DatabaseRepresentatives.ShouldBe(["staff"]);
    }

    [Fact]
    public void DedicatedDomain_GetsItsOwnDatabase_AndIsARepresentative()
    {
        var sut = Create(o => o.Domains["Mobile"] = Mobile);

        sut.ConnectionStringFor("mobile").ShouldBe(Mobile);
        sut.HasDedicatedDatabase("mobile").ShouldBeTrue();
        sut.ConnectionStringFor("fraud").ShouldBe(Default);
        sut.SameDatabase("staff", "fraud").ShouldBeTrue();
        sut.SameDatabase("staff", "mobile").ShouldBeFalse();
        sut.DatabaseRepresentatives.ShouldBe(["staff", "mobile"]);
    }

    [Fact]
    public void CurrentDomain_IsReadFromTheScope()
    {
        var sut = Create(o => o.Domains["mobile"] = Mobile);

        sut.ConnectionStringFor().ShouldBe(Default);
        using (DomainScope.Begin("mobile"))
        {
            sut.ConnectionStringFor().ShouldBe(Mobile);
            sut.HasDedicatedDatabase().ShouldBeTrue();
        }
    }

    [Fact]
    public void TwoDomainsOnTheSameDedicatedDatabase_ShareOneRepresentative()
    {
        var sut = Create(o =>
        {
            o.Domains["mobile"] = Mobile;
            o.Domains["fraud"] = Mobile;
        });

        sut.DatabaseRepresentatives.ShouldBe(["staff", "mobile"]);
    }

    [Fact]
    public void DedicatedDatabaseForAnUnhostedDomain_FailsAtStartup()
    {
        Should.Throw<AetherException>(() => Create(o => o.Domains["sales"] = Mobile));
    }

    [Fact]
    public void DedicatedDatabaseForThePrimary_FailsAtStartup()
    {
        Should.Throw<AetherException>(() => Create(o => o.Domains["staff"] = Mobile));
    }
}
