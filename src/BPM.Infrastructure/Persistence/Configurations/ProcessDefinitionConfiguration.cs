using BPM.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BPM.Infrastructure.Persistence.Configurations;

public class ProcessDefinitionConfiguration : IEntityTypeConfiguration<ProcessDefinition>
{
    public void Configure(EntityTypeBuilder<ProcessDefinition> builder)
    {
        builder.ToTable("ProcessDefinitions");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Key).IsRequired().HasMaxLength(200);
        builder.Property(p => p.Name).IsRequired().HasMaxLength(256);
        builder.Property(p => p.Category).HasMaxLength(128);
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(p => p.RowVersion).IsConcurrencyToken().ValueGeneratedNever();

        // Skill.md §7: Key must be unique within scope — one row per (tenant, key) regardless of
        // how many versions/definitions with that key have ever existed.
        builder.HasIndex(p => new { p.TenantId, p.Key }).IsUnique();

        builder.HasMany(p => p.Versions)
            .WithOne(v => v.ProcessDefinition)
            .HasForeignKey(v => v.ProcessDefinitionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
