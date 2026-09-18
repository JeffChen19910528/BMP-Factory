using BPM.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BPM.Infrastructure.Persistence.Configurations;

public class ProcessInstanceConfiguration : IEntityTypeConfiguration<ProcessInstance>
{
    public void Configure(EntityTypeBuilder<ProcessInstance> builder)
    {
        builder.ToTable("ProcessInstances");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.BusinessKey).HasMaxLength(256);
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(p => p.RowVersion).IsConcurrencyToken().ValueGeneratedNever();

        builder.HasIndex(p => p.Status);
        builder.HasIndex(p => p.InitiatorId);

        // Phase 10 — Process Start idempotency. Scoped by (TenantId, ProcessDefinitionId,
        // BusinessKey), not globally: the same BusinessKey (e.g. a purchase order number) is
        // legitimately reusable across two unrelated process definitions, mirroring the existing
        // SlaPolicy precedent of scoping by (ProcessDefinitionId, NodeId) rather than globally.
        // A standard Postgres unique index on a nullable column already treats every NULL as
        // distinct from every other NULL, so this needs no filtered/partial-index workaround —
        // requests with no BusinessKey are completely unaffected and can start unlimited
        // instances, exactly preserving current behavior.
        builder.HasIndex(p => new { p.TenantId, p.ProcessDefinitionId, p.BusinessKey }).IsUnique();

        builder.HasOne(p => p.ProcessDefinition)
            .WithMany()
            .HasForeignKey(p => p.ProcessDefinitionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(p => p.ProcessVersion)
            .WithMany()
            .HasForeignKey(p => p.ProcessVersionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(p => p.Tasks)
            .WithOne(t => t.ProcessInstance)
            .HasForeignKey(t => t.ProcessInstanceId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
