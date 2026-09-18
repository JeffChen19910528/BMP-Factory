using BPM.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BPM.Infrastructure.Persistence.Configurations;

public class SlaPolicyConfiguration : IEntityTypeConfiguration<SlaPolicy>
{
    public void Configure(EntityTypeBuilder<SlaPolicy> builder)
    {
        builder.ToTable("SlaPolicies");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.NodeId).IsRequired().HasMaxLength(128);
        builder.Property(p => p.RowVersion).IsConcurrencyToken().ValueGeneratedNever();

        // One policy per (process definition, node) — matches RoleConfiguration's own
        // (TenantId, Name) unique-pair precedent. Editing means updating this row, not inserting a
        // second one for the same step (Part I: "duplicate conflicting policy where applicable").
        builder.HasIndex(p => new { p.ProcessDefinitionId, p.NodeId }).IsUnique();
    }
}
