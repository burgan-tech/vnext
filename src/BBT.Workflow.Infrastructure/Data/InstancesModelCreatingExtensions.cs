using BBT.Aether.Domain.EntityFrameworkCore.Modeling;
using BBT.Workflow.Data.ValueConverters;
using BBT.Workflow.Definitions;
using BBT.Workflow.Instances;
using Microsoft.EntityFrameworkCore;

namespace BBT.Workflow.Data;

public static class InstancesModelCreatingExtensions
{
    public static void ConfigureInstances(
        this ModelBuilder builder, string? schema = null)
    {
        /* Configure all entities here. */

        builder.Entity<Instance>(b =>
        {
            b.ToTable("Instances", schema);
            b.ConfigureByConvention();

            b.Ignore(p => p.IsTransient);

            b.Property(p => p.Key)
                .HasMaxLength(InstanceConstants.MaxKeyLength);

            b.Property(p => p.Flow)
                .IsRequired()
                .HasMaxLength(WorkflowConstants.MaxKeyLength);
            
            b.Property(p => p.FlowVersion)
                .IsRequired()
                .HasMaxLength(WorkflowConstants.MaxVersionLength);

            b.Property(p => p.Tags)
                .HasColumnType("text[]");

            b.Property(p => p.CurrentState)
                .HasMaxLength(StateConstants.MaxKeyLength);

            b.Property(p => p.CurrentStateType)
                .HasConversion<int?>();

            b.Property(p => p.CurrentStateSubType)
                .HasConversion<int?>();

            b.Property(p => p.EffectiveState)
                .HasMaxLength(StateConstants.MaxKeyLength);

            // Index for EffectiveState for query performance
            b.HasIndex(p => p.EffectiveState)
                .HasDatabaseName("IX_Instances_EffectiveState");

            b.Property(p => p.Stage)
                .HasMaxLength(InstanceConstants.MaxStageLength);

            b.Property(p => p.Status)
                .IsRequired()
                .HasMaxLength(InstanceConstants.MaxStatusLength)
                .HasConversion(new InstanceStatusConverter());

            b.HasMany(m => m.DataList)
                .WithOne()
                .HasForeignKey(p => p.InstanceId)
                .OnDelete(DeleteBehavior.Cascade);

            b.Metadata
                .FindNavigation(nameof(Instance.Data))
                ?.SetPropertyAccessMode(PropertyAccessMode.Field);

            b.HasMany(m => m.ChildCorrelations)
                .WithOne()
                .HasForeignKey(p => p.ParentInstanceId)
                .OnDelete(DeleteBehavior.NoAction);

            b.Metadata
                .FindNavigation(nameof(Instance.ChildCorrelations))
                ?.SetPropertyAccessMode(PropertyAccessMode.Field);

            // STORED GENERATED column: COALESCE(ModifiedAt, CreatedAt).
            // Enables index-friendly incremental scans (GetActiveDataListSinceAsync) and
            // is paired with IX_Instances_Active_LastTouched_Id below.
            b.Property<DateTime>("LastTouchedAt")
                .HasColumnType("timestamp with time zone")
                .HasComputedColumnSql("COALESCE(\"ModifiedAt\", \"CreatedAt\")", stored: true);

            // Runtime hot-path partial indexes. Runtime DB queries only ever filter by
            // Status = 'A' (Active); 'B' (Busy) is consumed in-memory and 'F' (Faulted)
            // retry path performs a PK lookup. Keeping the partial filter narrow lets the
            // planner pick these indexes for Status == InstanceStatus.Active LINQ queries.
            b.HasIndex(new[] { "Key" }, "IX_Instances_Active_Key")
                .HasFilter("\"Status\" = 'A'");

            // Full (non-partial) Key index. FindByIdentifierAsync /
            // FindByIdentifierAsReadOnlyAsync probe by Key without a Status filter, so the
            // partial IX_Instances_Active_Key above cannot serve those lookups. This index
            // covers Key probes across all statuses while the partial index keeps the
            // active-only fast path compact. The named-overload form is required so EF
            // Core does not deduplicate the two Key indexes into a single model entry.
            b.HasIndex(new[] { "Key" }, "IX_Instances_Key");

            b.HasIndex("LastTouchedAt", "Id")
                .HasFilter("\"Status\" = 'A'")
                .HasDatabaseName("IX_Instances_Active_LastTouched_Id");
        });

        builder.Entity<InstanceCorrelation>(b =>
        {
            b.ToTable("InstancesCorrelations", schema);
            b.ConfigureByConvention();

            b.Property(p => p.ParentState)
                .IsRequired()
                .HasMaxLength(StateConstants.MaxKeyLength);

            b.Property(p => p.SubFlowType)
                .IsRequired()
                .HasMaxLength(WorkflowConstants.MaxTypeLength)
                .HasConversion(new SubFlowTypeConverter())
                .HasComment("SubFlow Type: S (SubFlow - blocking) or P (SubProcess - non-blocking)");

            b.Property(p => p.SubFlowDomain)
                .IsRequired()
                .HasMaxLength(WorkflowConstants.MaxDomainLength);

            b.Property(p => p.SubFlowName)
                .IsRequired()
                .HasMaxLength(WorkflowConstants.MaxKeyLength);

            b.Property(p => p.SubFlowVersion)
                .HasMaxLength(WorkflowConstants.MaxVersionLength);

            b.Property(p => p.SubFlowCurrentState)
                .HasMaxLength(StateConstants.MaxKeyLength);

            // Partial covering index for the runtime hot-path. The WithDetailsAsync
            // include now filters c => !c.IsCompleted, so this partial set matches the
            // include precisely and serves it as an index-only scan.
            // CompletedAt is intentionally omitted from INCLUDE: it is NULL within the
            // partial set by definition.
            b.HasIndex(p => p.ParentInstanceId)
                .HasFilter("\"IsCompleted\" = false")
                .IncludeProperties(p => new
                {
                    p.SubFlowType,
                    p.SubFlowInstanceId,
                    p.SubFlowDomain,
                    p.SubFlowName,
                    p.SubFlowVersion,
                    p.SubFlowCurrentState,
                    p.SubFlowStateChangedAt,
                    p.ParentState
                })
                .HasDatabaseName("IX_InstancesCorrelations_ActiveByParent_Covering");

            // Tiny partial index dedicated to the blocking-subflow predicate evaluated on
            // every transition (AnyActiveSubFlowByParentAsync / FindActiveSubFlowByParentAsync).
            b.HasIndex(p => p.ParentInstanceId)
                .HasFilter("\"IsCompleted\" = false AND \"SubFlowType\" = 'S'")
                .HasDatabaseName("IX_InstancesCorrelations_ActiveBlockingSubFlow");

            // SubFlowInstanceId is 1-1 with the started SubFlow instance (subflow start is
            // unique per parent state). UNIQUE both enforces this invariant and gives the
            // hot SubFlow-completion lookup (FindBySubInstanceIdAsync) a direct B-tree probe.
            b.HasIndex(p => p.SubFlowInstanceId)
                .IsUnique()
                .HasDatabaseName("UX_InstancesCorrelations_SubFlowInstanceId");
        });

        builder.Entity<InstanceData>(b =>
        {
            b.ToTable("InstancesData", schema);
            b.ConfigureByConvention();

            b.Property(p => p.Version)
                .IsRequired()
                .HasMaxLength(WorkflowConstants.MaxVersionLength);

            b.Property(p => p.HistorySequence)
                .IsRequired()
                .HasDefaultValue(0);

            b.Property(p => p.VersionNo)
                .IsRequired()
                .HasDefaultValue(0L);

            b.Property(p => p.IsLatest)
                .IsRequired()
                .HasDefaultValue(false);

            b.Property(p => p.ETag)
                .IsRequired()
                .HasMaxLength(WorkflowConstants.MaxETagLength);
            
            b.Property(p => p.DataHash)
                .IsRequired()
                .HasDefaultValue("99914b932bd37a50b983c5e7c90ae93b") // Default Value: {}
                .HasMaxLength(WorkflowConstants.MaxDataHashLength);

            b.OwnsOne(p => p.Data, d =>
            {
                d.Ignore(g => g.JsonElement);
                d.Property(g => g.Json)
                    .HasColumnType("jsonb")
                    .HasColumnName(nameof(InstanceData.Data));
            });

            b.HasIndex(p => p.InstanceId);

            // Unique index: Instance-based VersionNo (for concurrency control)
            b.HasIndex(p => new { p.InstanceId, p.VersionNo })
                .IsUnique()
                .HasDatabaseName("UX_InstancesData_Instance_VersionNo");

            // Partial unique index: Only one record per instance can have IsLatest = true.
            // INCLUDE adds the meta columns the runtime reads alongside the latest snapshot
            // so most reads can be served as index-only scans. The Data jsonb payload is
            // intentionally NOT included to keep the index compact; the planner falls back
            // to a heap fetch only when the JSON body is needed.
            b.HasIndex(p => p.InstanceId)
                .IsUnique()
                .HasFilter("\"IsLatest\" = true")
                .IncludeProperties(p => new
                {
                    p.Version,
                    p.VersionNo,
                    p.HistorySequence,
                    p.ETag,
                    p.DataHash,
                    p.EnteredAt
                })
                .HasDatabaseName("UX_InstancesData_Instance_IsLatest");
        });

        builder.Entity<InstanceTransition>(b =>
        {
            b.ToTable("InstanceTransitions", schema);
            b.ConfigureByConvention();

            b.Property(p => p.TransitionId)
                .IsRequired()
                .HasMaxLength(TransitionConstants.MaxKeyLength);

            b.Property(p => p.FromState)
                .IsRequired()
                .HasMaxLength(StateConstants.MaxKeyLength);

            b.Property(p => p.ToState)
                .HasMaxLength(StateConstants.MaxKeyLength);

            b.Property(p => p.TriggerType)
                .IsRequired()
                .HasConversion<int>()
                .HasDefaultValue(TriggerType.Manual);

            b.OwnsOne(p => p.Body, d =>
            {
                d.Ignore(g => g.JsonElement);
                d.Property(g => g.Json)
                    .HasColumnType("jsonb")
                    .HasColumnName(nameof(InstanceTransition.Body));
            });

            b.OwnsOne(p => p.Header, d =>
            {
                d.Ignore(g => g.JsonElement);
                d.Property(g => g.Json)
                    .HasColumnType("jsonb")
                    .HasColumnName(nameof(InstanceTransition.Header));
            });

            b.HasOne<Instance>()
                .WithMany()
                .HasForeignKey(p => p.InstanceId)
                .OnDelete(DeleteBehavior.Cascade);

            // Partial index for GetLatestIncompleteAsync (Retry path & in-flight checks).
            // Most transitions complete quickly, so the IS NULL subset stays small/hot.
            b.HasIndex(p => new { p.InstanceId, p.StartedAt })
                .HasFilter("\"FinishedAt\" IS NULL")
                .HasDatabaseName("IX_InstanceTransitions_Incomplete");

            // Partial index for GetLastCompletedManualTransitionAsync. TriggerType = 0 is
            // Manual (default value, see TriggerType.Manual mapping above).
            b.HasIndex(p => new { p.InstanceId, p.FinishedAt })
                .HasFilter("\"FinishedAt\" IS NOT NULL AND \"TriggerType\" = 0")
                .HasDatabaseName("IX_InstanceTransitions_CompletedManual");
        });

        builder.Entity<InstanceTask>(b =>
        {
            b.ToTable("InstanceTasks", schema);
            b.ConfigureByConvention();

            b.Property(p => p.TaskId)
                .IsRequired()
                .HasMaxLength(TaskConstants.MaxKeyLength);

            b.OwnsOne(p => p.Request, d =>
            {
                d.Ignore(g => g.JsonElement);
                d.Property(g => g.Json)
                    .HasColumnType("jsonb")
                    .HasColumnName(nameof(InstanceTask.Request));
            });

            b.OwnsOne(p => p.Response, d =>
            {
                d.Ignore(g => g.JsonElement);
                d.Property(g => g.Json)
                    .HasColumnType("jsonb")
                    .HasColumnName(nameof(InstanceTask.Response));
            });

            b.OwnsOne(p => p.InvocationResult, d =>
            {
                d.Ignore(g => g.JsonElement);
                d.Property(g => g.Json)
                    .HasColumnType("jsonb")
                    .HasColumnName(nameof(InstanceTask.InvocationResult));
            });

            b.HasOne<InstanceTransition>()
                .WithMany()
                .HasForeignKey(p => p.TransitionId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasOne<InstanceTask>()
                .WithMany()
                .HasForeignKey(p => p.FaultedTaskId)
                .OnDelete(DeleteBehavior.NoAction);

            // Covering index for the Status-filtered TaskId selection queries
            // (GetCompletedTaskIdsAsync, GetTaskIdsByStatusAsync, GetSuccessfulTaskIdsAsync).
            // GetByTransitionIdAsync (no Status filter) can also use this index via the
            // (TransitionId, ...) leftmost prefix.
            b.HasIndex(p => new { p.TransitionId, p.Status })
                .IncludeProperties(p => new { p.TaskId, p.BusinessStatus, p.StartedAt })
                .HasDatabaseName("IX_InstanceTasks_Transition_Status_Covering");
        });

        builder.Entity<InstanceAction>(b =>
        {
            b.ToTable("InstanceActions", schema);
            b.ConfigureByConvention();

            b.Property(p => p.Status)
                .IsRequired()
                .HasMaxLength(InstanceActionConstants.MaxStatusLength);

            b.OwnsOne(p => p.Detail, d =>
            {
                d.Ignore(g => g.JsonElement);
                d.Property(g => g.Json)
                    .HasColumnType("jsonb")
                    .HasColumnName(nameof(InstanceAction.Detail));
            });

            b.HasOne<InstanceTask>()
                .WithMany()
                .HasForeignKey(p => p.TaskId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<InstanceJob>(b =>
        {
            b.ToTable("InstanceJobs", schema);
            b.ConfigureByConvention();

            b.Property(p => p.JobName)
                .IsRequired()
                .HasMaxLength(InstanceJobConstants.MaxJobNameLength);

            b.Property(p => p.JobId)
                .IsRequired();

            b.Property(p => p.Domain)
                .IsRequired()
                .HasMaxLength(WorkflowConstants.MaxDomainLength);

            b.Property(p => p.FlowName)
                .IsRequired()
                .HasMaxLength(WorkflowConstants.MaxFlowLength);

            b.HasIndex(i => i.JobId)
                .IsUnique();
        });
    }
}