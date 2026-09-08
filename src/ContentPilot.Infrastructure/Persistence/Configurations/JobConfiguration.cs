using ContentPilot.Infrastructure.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ContentPilot.Infrastructure.Persistence.Configurations;

public sealed class JobConfiguration : IEntityTypeConfiguration<Job>
{
    public void Configure(EntityTypeBuilder<Job> builder)
    {
        builder.ToTable("jobs");
        builder.HasKey(j => j.Id);

        builder.Property(j => j.Type).HasMaxLength(120).IsRequired();
        builder.Property(j => j.PayloadJson).HasColumnType("jsonb").IsRequired();
        builder.Property(j => j.State).HasConversion<short>().IsRequired();
        builder.Property(j => j.Priority).IsRequired();
        builder.Property(j => j.Attempts).IsRequired();
        builder.Property(j => j.MaxAttempts).IsRequired();
        builder.Property(j => j.LockedBy).HasMaxLength(120);
        builder.Property(j => j.LastError).HasMaxLength(4000);
        builder.Property(j => j.IdempotencyKey).HasMaxLength(200);
        builder.Property(j => j.CreatedAt).IsRequired();
        builder.Property(j => j.RunAt).IsRequired();

        // Absorbs double-fired triggers: the second insert loses on the unique index.
        builder.HasIndex(j => j.IdempotencyKey)
            .IsUnique()
            .HasFilter("idempotency_key IS NOT NULL");

        // The dispatcher's only query. Partial so the index stays small no matter how
        // much completed history accumulates in the table.
        builder.HasIndex(j => new { j.RunAt, j.Priority })
            .HasDatabaseName("ix_jobs_pending")
            .HasFilter("state = 0");

        // The reaper's only query.
        builder.HasIndex(j => j.LeaseUntil)
            .HasDatabaseName("ix_jobs_leased")
            .HasFilter("state = 1");

        builder.HasIndex(j => new { j.TenantId, j.Type, j.State });
    }
}
