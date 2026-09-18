using BPM.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BPM.Infrastructure.Persistence.Configurations;

public class TaskInstanceConfiguration : IEntityTypeConfiguration<TaskInstance>
{
    public void Configure(EntityTypeBuilder<TaskInstance> builder)
    {
        builder.ToTable("TaskInstances");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.NodeId).IsRequired().HasMaxLength(128);
        builder.Property(t => t.NodeName).IsRequired().HasMaxLength(256);
        builder.Property(t => t.AssigneeRole).HasMaxLength(128);
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(t => t.RowVersion).IsConcurrencyToken().ValueGeneratedNever();

        builder.HasIndex(t => t.AssigneeId);
        builder.HasIndex(t => t.Status);
        builder.HasIndex(t => t.DueAt);
        // Phase 12 — GetMyTasksAsync now does ORDER BY CreatedAt DESC over a potentially large,
        // multi-source (assignee + approval-participant) id set before paginating; confirmed via
        // inspection no existing index (AssigneeId/Status/DueAt above) covers CreatedAt, and a
        // single-column index matches this file's own existing pattern rather than a speculative
        // composite one.
        builder.HasIndex(t => t.CreatedAt);
    }
}
