using System;
using System.Collections.Generic;
using Xunit;

namespace BBT.Workflow.Instances;

/// <summary>
/// Pins the Katman 2 / Task 1 read memoization contract (B1-B3): <see cref="InstanceData.Attributes"/>
/// and <see cref="Instance.Data"/> materialize their dynamic tree at most once per data version,
/// and the memo invalidates exactly when a new row is appended (count-keyed self-check).
/// </summary>
public class InstanceDataMemoTests
{
    [Fact]
    public void Attributes_IsMemoized_SameExpandoReference()
    {
        // InstanceData satırı immutable — Attributes artık instance-başına tek kez kurulur.
        var instance = InstanceFactory.CreateDefault();
        var row = instance.SeedData(Guid.NewGuid(), JsonData.CreateFrom("""{"x":1}"""));

        Assert.Same((object)row.Attributes!, (object)row.Attributes!);
    }

    [Fact]
    public void InstanceData_MemoInvalidates_OnAppend()
    {
        // Instance.Data: yeni satır append edilince memo bayatlar (sayaç-anahtarlı kendini-doğrulama).
        var instance = InstanceFactory.CreateDefault();
        instance.SeedData(Guid.NewGuid(), JsonData.CreateFrom("""{"x":1}"""));

        var before = (object)instance.Data!;

        instance.SeedData(Guid.NewGuid(), JsonData.CreateFrom("""{"y":2}"""));

        var after = (object)instance.Data!;
        Assert.NotSame(before, after);
    }

    [Fact]
    public void Data_MutationVisibleWithinSameLatest_ButNotPersisted()
    {
        // KABUL EDİLMİŞ davranış değişimi (spec): aynı latest üzerindeki erişimler aynı ağacı paylaşır.
        var instance = InstanceFactory.CreateDefault();
        instance.SeedData(Guid.NewGuid(), JsonData.CreateFrom("""{"x":1}"""));

        ((IDictionary<string, object?>)instance.Data!)["injected"] = 42;
        Assert.Equal(42, ((IDictionary<string, object?>)instance.Data!)["injected"]);

        // Persist edilen içerik değişmedi: satırın JsonData'sı hâlâ orijinal.
        Assert.DoesNotContain("injected", instance.LatestData!.Data.Json);
    }
}
