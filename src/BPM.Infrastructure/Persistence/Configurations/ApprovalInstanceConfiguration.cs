using BPM.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BPM.Infrastructure.Persistence.Configurations;

public class ApprovalInstanceConfiguration : IEntityTypeConfiguration<ApprovalInstance>
{
    public void Configure(EntityTypeBuilder<ApprovalInstance> builder)
    {
        builder.ToTable("ApprovalInstances");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Policy).HasConversion<string>().HasMaxLength(32);
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(a => a.RowVersion).IsConcurrencyToken().ValueGeneratedNever();

        builder.HasIndex(a => a.TaskInstanceId).IsUnique();
        builder.HasIndex(a => a.Status);

        builder.HasOne(a => a.TaskInstance)
            .WithOne()
            .HasForeignKey<ApprovalInstance>(a => a.TaskInstanceId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasMany(a => a.Assignments)
            .WithOne(x => x.ApprovalInstance)
            .HasForeignKey(x => x.ApprovalInstanceId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class ApprovalAssignmentConfiguration : IEntityTypeConfiguration<ApprovalAssignment>
{
    public void Configure(EntityTypeBuilder<ApprovalAssignment> builder)
    {
        builder.ToTable("ApprovalAssignments");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(a => a.RowVersion).IsConcurrencyToken().ValueGeneratedNever();

        builder.HasIndex(a => new { a.ApprovalInstanceId, a.Order });
        builder.HasIndex(a => a.UserId);
        builder.HasIndex(a => a.DelegatedToUserId);
        builder.HasIndex(a => a.Status);
    }
}
