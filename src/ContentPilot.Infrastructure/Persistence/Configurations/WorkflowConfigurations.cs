using ContentPilot.Domain.Content;
using ContentPilot.Domain.Workflow;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ContentPilot.Infrastructure.Persistence.Configurations;

public sealed class WorkflowRunConfiguration : IEntityTypeConfiguration<WorkflowRun>
{
    public void Configure(EntityTypeBuilder<WorkflowRun> builder)
    {
        builder.ToTable("workflow_runs");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.TenantId).IsRequired();
        builder.Property(r => r.Scope).HasConversion<short>().IsRequired();
        builder.Property(r => r.State).HasConversion<short>().IsRequired();
        builder.Property(r => r.LeaseOwner).HasMaxLength(200);
        builder.Property(r => r.LastError).HasMaxLength(1000);
        builder.Property(r => r.StartedAt).IsRequired();
        builder.Property(r => r.Deadline).IsRequired();
        builder.Property(r => r.CreatedAt).IsRequired();
        builder.Ignore(r => r.IsTerminal);
        builder.Ignore(r => r.SchemaRepairExhausted);
        builder.Ignore(r => r.TransientExhausted);
        builder.Ignore(r => r.QualityExhausted);
        builder.Ignore(r => r.StepBudgetExhausted);

        builder.HasOne<ContentCampaign>()
            .WithMany().HasForeignKey(r => r.CampaignId).OnDelete(DeleteBehavior.Cascade);

        // One live run per entity: the orchestrator's core invariant that a campaign or item
        // is being advanced by exactly one run at a time.
        builder.HasIndex(r => new { r.Scope, r.EntityId })
            .IsUnique()
            .HasFilter("state = 0");

        // The reaper's query: leases nobody renewed past their deadline.
        builder.HasIndex(r => new { r.State, r.LeaseUntil });

        builder.HasIndex(r => new { r.TenantId, r.CampaignId });
    }
}

public sealed class WorkflowStepConfiguration : IEntityTypeConfiguration<WorkflowStep>
{
    public void Configure(EntityTypeBuilder<WorkflowStep> builder)
    {
        builder.ToTable("workflow_steps");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.TenantId).IsRequired();
        builder.Property(s => s.StepName).HasMaxLength(80).IsRequired();
        builder.Property(s => s.IdempotencyKey).HasMaxLength(200).IsRequired();
        builder.Property(s => s.Outcome).HasConversion<short>().IsRequired();
        builder.Property(s => s.ResultJson).HasColumnType("jsonb");
        builder.Property(s => s.Error).HasMaxLength(2000);
        builder.Property(s => s.StartedAt).IsRequired();

        builder.HasOne<WorkflowRun>()
            .WithMany().HasForeignKey(s => s.WorkflowRunId).OnDelete(DeleteBehavior.Cascade);

        // The whole idempotency mechanism: a replayed job for the same key finds its row
        // already here and becomes a no-op instead of executing twice.
        builder.HasIndex(s => s.IdempotencyKey).IsUnique();

        builder.HasIndex(s => new { s.WorkflowRunId, s.StartedAt });
    }
}

public sealed class ContentRevisionConfiguration : IEntityTypeConfiguration<ContentRevision>
{
    public void Configure(EntityTypeBuilder<ContentRevision> builder)
    {
        builder.ToTable("content_revisions");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.TenantId).IsRequired();
        builder.Property(r => r.Reason).HasMaxLength(1000).IsRequired();
        builder.Property(r => r.RemediationAction).HasMaxLength(200).IsRequired();
        builder.Property(r => r.RestartAtStep).HasMaxLength(80);
        builder.Property(r => r.FindingsJson).HasColumnType("jsonb");
        builder.Property(r => r.CreatedAt).IsRequired();

        builder.HasOne<ContentItem>()
            .WithMany().HasForeignKey(r => r.ContentItemId).OnDelete(DeleteBehavior.Cascade);

        // The review UI's query: this item's attempt history, in order.
        builder.HasIndex(r => new { r.ContentItemId, r.Attempt });
    }
}

public sealed class BudgetReservationConfiguration : IEntityTypeConfiguration<BudgetReservation>
{
    public void Configure(EntityTypeBuilder<BudgetReservation> builder)
    {
        builder.ToTable("budget_reservations");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.TenantId).IsRequired();
        builder.Property(r => r.ReservedAt).IsRequired();
        builder.Property(r => r.ExpiresAt).IsRequired();

        builder.HasOne<ContentCampaign>()
            .WithMany().HasForeignKey(r => r.CampaignId).OnDelete(DeleteBehavior.Cascade);

        // BudgetGuard's query, at both scopes: live reservations for this item, or this
        // whole campaign. A partial index would need "IsLive" in SQL, not C#, so the
        // reaper's cleanup query filters on ReleasedAt/ExpiresAt directly instead.
        builder.HasIndex(r => new { r.CampaignId, r.ReleasedAt, r.ExpiresAt });
        builder.HasIndex(r => new { r.ContentItemId, r.ReleasedAt, r.ExpiresAt });
    }
}
