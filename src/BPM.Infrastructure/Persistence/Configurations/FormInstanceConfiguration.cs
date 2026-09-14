using BPM.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BPM.Infrastructure.Persistence.Configurations;

public class FormInstanceConfiguration : IEntityTypeConfiguration<FormInstance>
{
    public void Configure(EntityTypeBuilder<FormInstance> builder)
    {
        builder.ToTable("FormInstances");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(f => f.RowVersion).IsConcurrencyToken().ValueGeneratedNever();

        builder.HasIndex(f => f.ProcessInstanceId);
        builder.HasIndex(f => f.TaskInstanceId).IsUnique().HasFilter("\"TaskInstanceId\" IS NOT NULL");
        builder.HasIndex(f => f.CreatedByUserId);
        builder.HasIndex(f => f.Status);

        builder.HasOne(f => f.FormDefinition)
            .WithMany()
            .HasForeignKey(f => f.FormDefinitionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(f => f.FormVersion)
            .WithMany()
            .HasForeignKey(f => f.FormVersionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(f => f.TaskInstance)
            .WithMany()
            .HasForeignKey(f => f.TaskInstanceId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(f => f.Data)
            .WithOne(d => d.FormInstance)
            .HasForeignKey<FormData>(d => d.FormInstanceId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(f => f.Attachments)
            .WithOne(a => a.FormInstance)
            .HasForeignKey(a => a.FormInstanceId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class FormDataConfiguration : IEntityTypeConfiguration<FormData>
{
    public void Configure(EntityTypeBuilder<FormData> builder)
    {
        builder.ToTable("FormData");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.DataJson).IsRequired();
        builder.Property(d => d.RowVersion).IsConcurrencyToken().ValueGeneratedNever();

        builder.HasIndex(d => d.FormInstanceId).IsUnique();
    }
}

public class AttachmentConfiguration : IEntityTypeConfiguration<Attachment>
{
    public void Configure(EntityTypeBuilder<Attachment> builder)
    {
        builder.ToTable("Attachments");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.FileName).IsRequired().HasMaxLength(512);
        builder.Property(a => a.ContentType).IsRequired().HasMaxLength(256);
        builder.Property(a => a.StorageKey).IsRequired().HasMaxLength(1024);
        builder.Property(a => a.Hash).IsRequired().HasMaxLength(128);
        builder.Property(a => a.RowVersion).IsConcurrencyToken().ValueGeneratedNever();

        builder.HasIndex(a => a.FormInstanceId);
        builder.HasIndex(a => a.StorageKey).IsUnique();
    }
}
