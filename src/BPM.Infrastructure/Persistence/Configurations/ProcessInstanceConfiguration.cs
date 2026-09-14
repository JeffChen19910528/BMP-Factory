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
