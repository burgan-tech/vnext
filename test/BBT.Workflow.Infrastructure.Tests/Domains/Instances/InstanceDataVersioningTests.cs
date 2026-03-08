using System;
using System.Linq;
using System.Threading.Tasks;
using BBT.Workflow.Data;
using BBT.Workflow.Instances;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace BBT.Workflow.Domains.Instances;

/// <summary>
/// Integration tests for InstanceData versioning and concurrency control.
/// Tests the PostgreSQL trigger and advisory lock mechanism for concurrent inserts.
/// </summary>
public sealed class InstanceDataVersioningTests : IAsyncLifetime
{
    private PostgreSqlContainer _postgresContainer = null!;
    private string _connectionString = null!;

    async Task IAsyncLifetime.InitializeAsync()
    {
        _postgresContainer = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("testdb")
            .WithUsername("test")
            .WithPassword("test")
            .Build();

        await _postgresContainer.StartAsync();
        _connectionString = _postgresContainer.GetConnectionString();

        // Create schema and apply trigger
        await using var context = CreateContext();
        await context.Database.EnsureCreatedAsync();
        await ApplyVersioningTrigger(context);
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _postgresContainer.StopAsync();
        await _postgresContainer.DisposeAsync();
    }

    private WorkflowDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<WorkflowDbContext>()
            .UseNpgsql(_connectionString)
            .Options;

        return new WorkflowDbContext(options);
    }

    /// <summary>
    /// Applies the versioning trigger and function to the test database.
    /// </summary>
    private static async Task ApplyVersioningTrigger(WorkflowDbContext context)
    {
        // Create function
        await context.Database.ExecuteSqlRawAsync(@"
CREATE OR REPLACE FUNCTION set_instance_data_version_and_latest()
RETURNS trigger AS $$
DECLARE
    next_version_no bigint;
BEGIN
    PERFORM pg_advisory_xact_lock(hashtext(NEW.""InstanceId""::text));

    SELECT COALESCE(MAX(""VersionNo""), 0) + 1
      INTO next_version_no
      FROM ""InstancesData""
     WHERE ""InstanceId"" = NEW.""InstanceId"";

    NEW.""VersionNo"" := next_version_no;

    UPDATE ""InstancesData""
       SET ""IsLatest"" = FALSE
     WHERE ""InstanceId"" = NEW.""InstanceId""
       AND ""IsLatest"" = TRUE;

    NEW.""IsLatest"" := TRUE;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;
        ");

        // Create trigger (if not exists)
        await context.Database.ExecuteSqlRawAsync(@"
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_trigger WHERE tgname = 'trg_instancesdata_set_version_and_latest'
    ) THEN
        CREATE TRIGGER trg_instancesdata_set_version_and_latest
        BEFORE INSERT ON ""InstancesData""
        FOR EACH ROW
        EXECUTE FUNCTION set_instance_data_version_and_latest();
    END IF;
END;
$$;
        ");
    }

    [Fact]
    public async Task Concurrent_Inserts_For_Same_Instance_Should_Have_Strictly_Increasing_VersionNo_And_Single_Latest()
    {
        // Arrange
        var instanceId = Guid.NewGuid();
        const int count = 20;
        var baseline = DateTime.UtcNow;

        // First create an Instance (parent) record
        await using (var ctx = CreateContext())
        {
            var instance = Instance.Create(instanceId, "test-flow", "1.0.0");
            ctx.Instances.Add(instance);
            await ctx.SaveChangesAsync();
        }

        // Act - Parallel inserts for the same InstanceId
        var tasks = Enumerable.Range(0, count)
            .Select(async i =>
            {
                // Add small jitter to increase concurrency likelihood
                await Task.Delay(Random.Shared.Next(0, 50));

                await using var ctx = CreateContext();
                var data = new JsonData("{}");
                
                // We need to add InstanceData directly since Instance is already created
                await ctx.Database.ExecuteSqlRawAsync(@"
                    INSERT INTO ""InstancesData"" (""Id"", ""InstanceId"", ""Version"", ""HistorySequence"", ""ETag"", ""DataHash"", ""Data"", ""EnteredAt"", ""VersionNo"", ""IsLatest"")
                    VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}::jsonb, {7}, 0, false)",
                    Guid.NewGuid(),
                    instanceId,
                    "1.0.0",
                    i,
                    Ulid.NewUlid().ToString(),
                    "da39a3ee5e6b4b0d3255bfef95601890afd80709",
                    "{}",
                    baseline.AddMilliseconds(i));
            });

        await Task.WhenAll(tasks);

        // Assert
        await using (var ctx = CreateContext())
        {
            var list = await ctx.InstancesData
                .Where(x => x.InstanceId == instanceId)
                .OrderBy(x => x.VersionNo)
                .ToListAsync();

            // a) Record count should match
            list.Count.ShouldBe(count);

            // b) VersionNo should be sequential 1..count
            var versionNos = list.Select(x => x.VersionNo).ToArray();
            for (var i = 0; i < count; i++)
            {
                versionNos[i].ShouldBe(i + 1);
            }

            // c) Only one record should have IsLatest = true
            var latestList = list.Where(x => x.IsLatest).ToList();
            latestList.Count.ShouldBe(1);

            var latest = latestList.Single();

            // d) Latest record should have the highest VersionNo
            var maxVersion = versionNos.Max();
            latest.VersionNo.ShouldBe(maxVersion);
        }
    }

    [Fact]
    public async Task Different_Instances_Should_Have_Independent_Version_Sequences()
    {
        // Arrange
        var instanceId1 = Guid.NewGuid();
        var instanceId2 = Guid.NewGuid();
        var baseline = DateTime.UtcNow;

        // Create parent Instance records
        await using (var ctx = CreateContext())
        {
            ctx.Instances.Add(Instance.Create(instanceId1, "test-flow-1", "1.0.0"));
            ctx.Instances.Add(Instance.Create(instanceId2, "test-flow-2","1.0.0"));
            await ctx.SaveChangesAsync();
        }

        // Act - Insert records for both instances
        await using (var ctx = CreateContext())
        {
            // Instance 1: 3 records
            for (int i = 0; i < 3; i++)
            {
                await ctx.Database.ExecuteSqlRawAsync(@"
                    INSERT INTO ""InstancesData"" (""Id"", ""InstanceId"", ""Version"", ""HistorySequence"", ""ETag"", ""DataHash"", ""Data"", ""EnteredAt"", ""VersionNo"", ""IsLatest"")
                    VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}::jsonb, {7}, 0, false)",
                    Guid.NewGuid(),
                    instanceId1,
                    "1.0.0",
                    i,
                    Ulid.NewUlid().ToString(),
                    "da39a3ee5e6b4b0d3255bfef95601890afd80709",
                    "{}",
                    baseline.AddSeconds(i));
            }

            // Instance 2: 2 records
            for (int i = 0; i < 2; i++)
            {
                await ctx.Database.ExecuteSqlRawAsync(@"
                    INSERT INTO ""InstancesData"" (""Id"", ""InstanceId"", ""Version"", ""HistorySequence"", ""ETag"", ""DataHash"", ""Data"", ""EnteredAt"", ""VersionNo"", ""IsLatest"")
                    VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}::jsonb, {7}, 0, false)",
                    Guid.NewGuid(),
                    instanceId2,
                    "1.0.0",
                    i,
                    Ulid.NewUlid().ToString(),
                    "da39a3ee5e6b4b0d3255bfef95601890afd80709",
                    "{}",
                    baseline.AddSeconds(i));
            }
        }

        // Assert
        await using (var ctx = CreateContext())
        {
            var list1 = await ctx.InstancesData
                .Where(x => x.InstanceId == instanceId1)
                .OrderBy(x => x.VersionNo)
                .ToListAsync();

            var list2 = await ctx.InstancesData
                .Where(x => x.InstanceId == instanceId2)
                .OrderBy(x => x.VersionNo)
                .ToListAsync();

            // Instance 1: VersionNo should be 1, 2, 3
            list1.Select(x => x.VersionNo).ShouldBe(new long[] { 1, 2, 3 });

            // Instance 2: VersionNo should be 1, 2
            list2.Select(x => x.VersionNo).ShouldBe(new long[] { 1, 2 });

            // Each instance should have exactly one IsLatest = true
            var latest1 = list1.Single(x => x.IsLatest);
            var latest2 = list2.Single(x => x.IsLatest);

            latest1.VersionNo.ShouldBe(3);
            latest2.VersionNo.ShouldBe(2);
        }
    }

    [Fact]
    public async Task Sequential_Inserts_Should_Maintain_Version_Order()
    {
        // Arrange
        var instanceId = Guid.NewGuid();

        await using (var ctx = CreateContext())
        {
            ctx.Instances.Add(Instance.Create(instanceId, "test-flow", "1.0.0"));
            await ctx.SaveChangesAsync();
        }

        // Act - Sequential inserts
        for (int i = 0; i < 5; i++)
        {
            await using var ctx = CreateContext();
            await ctx.Database.ExecuteSqlRawAsync(@"
                INSERT INTO ""InstancesData"" (""Id"", ""InstanceId"", ""Version"", ""HistorySequence"", ""ETag"", ""DataHash"", ""Data"", ""EnteredAt"", ""VersionNo"", ""IsLatest"")
                VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}::jsonb, {7}, 0, false)",
                Guid.NewGuid(),
                instanceId,
                "1.0.0",
                i,
                Ulid.NewUlid().ToString(),
                "da39a3ee5e6b4b0d3255bfef95601890afd80709",
                "{}",
                DateTime.UtcNow);
        }

        // Assert
        await using (var ctx = CreateContext())
        {
            var list = await ctx.InstancesData
                .Where(x => x.InstanceId == instanceId)
                .OrderBy(x => x.VersionNo)
                .ToListAsync();

            list.Count.ShouldBe(5);
            list.Select(x => x.VersionNo).ShouldBe(new long[] { 1, 2, 3, 4, 5 });

            // Only the last one should be latest
            var latestCount = list.Count(x => x.IsLatest);
            latestCount.ShouldBe(1);

            var latest = list.Single(x => x.IsLatest);
            latest.VersionNo.ShouldBe(5);
        }
    }
}

