using BPM.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BPM.Infrastructure.Persistence.Configurations;

public class ProcessVersionConfiguration : IEntityTypeConfiguration<ProcessVersion>
{
    public void Configure(EntityTypeBuilder<ProcessVersion> builder)
    {
        builder.ToTable("ProcessVersions");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.DefinitionJson).IsRequired();
        builder.Property(v => v.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(v => v.RowVersion).IsConcurrencyToken().ValueGeneratedNever();
        builder.Property(v => v.ChangeReason).HasMaxLength(1000);

        builder.HasIndex(v => new { v.ProcessDefinitionId, v.VersionNumber }).IsUnique();
    }
}
