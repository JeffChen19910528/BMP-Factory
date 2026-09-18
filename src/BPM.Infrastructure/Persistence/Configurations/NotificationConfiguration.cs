using BPM.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BPM.Infrastructure.Persistence.Configurations;

public class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("Notifications");
        builder.HasKey(n => n.Id);
        builder.Property(n => n.Type).HasConversion<string>().HasMaxLength(64);
        builder.Property(n => n.Title).IsRequired().HasMaxLength(256);
        builder.Property(n => n.Message).IsRequired().HasMaxLength(2000);
        builder.Property(n => n.RelatedEntityType).IsRequired().HasMaxLength(128);
        builder.Property(n => n.RelatedEntityId).HasMaxLength(128);

        // Matches AuditLogConfiguration's own index rationale: every real query filters by
        // recipient (GetForUserAsync), by recipient+unread (unread count/filter), or orders by
        // CreatedAt (newest first) — no other access pattern exists, so no other index was added.
        builder.HasIndex(n => n.RecipientUserId);
        builder.HasIndex(n => new { n.RecipientUserId, n.IsRead });
        builder.HasIndex(n => n.CreatedAt);
    }
}
