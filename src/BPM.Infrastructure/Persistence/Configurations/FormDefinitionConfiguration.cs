using BPM.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BPM.Infrastructure.Persistence.Configurations;

public class FormDefinitionConfiguration : IEntityTypeConfiguration<FormDefinition>
{
    public void Configure(EntityTypeBuilder<FormDefinition> builder)
    {
        builder.ToTable("FormDefinitions");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Key).IsRequired().HasMaxLength(200);
        builder.Property(f => f.Name).IsRequired().HasMaxLength(256);
        builder.Property(f => f.Category).HasMaxLength(128);
        builder.Property(f => f.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(f => f.RowVersion).IsConcurrencyToken().ValueGeneratedNever();

        builder.HasIndex(f => new { f.TenantId, f.Key }).IsUnique();

        builder.HasMany(f => f.Versions)
            .WithOne(v => v.FormDefinition)
            .HasForeignKey(v => v.FormDefinitionId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class FormVersionConfiguration : IEntityTypeConfiguration<FormVersion>
{
    public void Configure(EntityTypeBuilder<FormVersion> builder)
    {
        builder.ToTable("FormVersions");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.SchemaJson).IsRequired();
        builder.Property(v => v.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(v => v.RowVersion).IsConcurrencyToken().ValueGeneratedNever();

        builder.HasIndex(v => new { v.FormDefinitionId, v.VersionNumber }).IsUnique();
    }
}
