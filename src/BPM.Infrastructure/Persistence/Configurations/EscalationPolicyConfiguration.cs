using BPM.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BPM.Infrastructure.Persistence.Configurations;

public class EscalationPolicyConfiguration : IEntityTypeConfiguration<EscalationPolicy>
{
    public void Configure(EntityTypeBuilder<EscalationPolicy> builder)
    {
        builder.ToTable("EscalationPolicies");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.NodeId).IsRequired().HasMaxLength(128);
        builder.Property(p => p.TargetType).HasConversion<string>().HasMaxLength(32);
        builder.Property(p => p.TargetValue).IsRequired().HasMaxLength(256);
        builder.Property(p => p.RowVersion).IsConcurrencyToken().ValueGeneratedNever();

        // Same (ProcessDefinitionId, NodeId) scoping as SlaPolicy — one escalation policy per step.
        builder.HasIndex(p => new { p.ProcessDefinitionId, p.NodeId }).IsUnique();
    }
}
