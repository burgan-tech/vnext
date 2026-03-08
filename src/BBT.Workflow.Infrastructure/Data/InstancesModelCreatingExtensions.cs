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

            b.Property(p => p.EffectiveState)
                .HasMaxLength(StateConstants.MaxKeyLength);

            // Index for EffectiveState for query performance
            b.HasIndex(p => p.EffectiveState)
                .HasDatabaseName("IX_Instances_EffectiveState");

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

            // Create index for performance on blocking SubFlow queries
            b.HasIndex(p => new { p.ParentInstanceId, p.IsCompleted, p.SubFlowType })
                .HasDatabaseName("IX_InstancesCorrelations_Performance");
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

            // Partial unique index: Only one record per instance can have IsLatest = true
            b.HasIndex(p => p.InstanceId)
                .IsUnique()
                .HasFilter("\"IsLatest\" = true")
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

            b.HasOne<InstanceTransition>()
                .WithMany()
                .HasForeignKey(p => p.TransitionId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasOne<InstanceTask>()
                .WithMany()
                .HasForeignKey(p => p.FaultedTaskId)
                .OnDelete(DeleteBehavior.NoAction);
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