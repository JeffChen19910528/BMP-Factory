using BPM.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BPM.Infrastructure.Persistence.Configurations;

public class TaskSlaConfiguration : IEntityTypeConfiguration<TaskSla>
{
    public void Configure(EntityTypeBuilder<TaskSla> builder)
    {
        builder.ToTable("TaskSlas");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Status).HasConversion<string>().HasMaxLength(32);

        // One TaskInstance has at most one TaskSla (see TaskSla.cs's own comment on why).
        builder.HasIndex(s => s.TaskInstanceId).IsUnique();
        builder.HasIndex(s => s.PolicyId);

        // Not queried by anything in this phase (no scheduler yet), but this is exactly the shape
        // Phase 6.4's scheduler will need ("find Active rows whose DueAt/WarningAt has passed") —
        // added now per Part U's own "likely useful indexes" list, cheap to carry, matching
        // NotificationDeliveryConfiguration's (Status, NextAttemptAt) composite-index precedent.
        builder.HasIndex(s => new { s.Status, s.DueAt });
        builder.HasIndex(s => new { s.Status, s.WarningAt });
    }
}
