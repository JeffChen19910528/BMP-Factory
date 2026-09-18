using BPM.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BPM.Infrastructure.Persistence.Configurations;

public class NotificationDeliveryConfiguration : IEntityTypeConfiguration<NotificationDelivery>
{
    public void Configure(EntityTypeBuilder<NotificationDelivery> builder)
    {
        builder.ToTable("NotificationDeliveries");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Channel).HasConversion<string>().HasMaxLength(32);
        builder.Property(d => d.Status).HasConversion<string>().HasMaxLength(32);
        builder.Property(d => d.RecipientAddress).HasMaxLength(320); // RFC 5321 max mailbox length
        builder.Property(d => d.LastError).HasMaxLength(1000);

        // Matches the worker's actual query shape (NotificationDeliveryProcessor.ClaimBatchAsync):
        // "eligible rows" filters on Status + NextAttemptAt together, so a composite index on
        // exactly those two columns is what the claim query needs — not a bare Status index alone.
        builder.HasIndex(d => new { d.Status, d.NextAttemptAt });
        builder.HasIndex(d => new { d.Channel, d.Status });
        builder.HasIndex(d => d.NotificationId);
    }
}
