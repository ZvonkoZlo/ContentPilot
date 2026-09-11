using ContentPilot.Domain.Content;
using ContentPilot.Domain.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ContentPilot.Infrastructure.Persistence.Configurations;

public sealed class ContentCampaignConfiguration : IEntityTypeConfiguration<ContentCampaign>
{
    public void Configure(EntityTypeBuilder<ContentCampaign> builder)
    {
        builder.ToTable("content_campaigns");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.TenantId).IsRequired();
        builder.Property(c => c.Status).HasConversion<short>().IsRequired();
        builder.Property(c => c.Trigger).HasConversion<short>().IsRequired();
        builder.Property(c => c.Theme).HasMaxLength(300);
        builder.Property(c => c.FailureReason).HasMaxLength(1000);
        builder.Property(c => c.CreatedAt).IsRequired();
        builder.Ignore(c => c.IsTerminal);

        builder.HasOne<Domain.Branding.Brand>()
            .WithMany().HasForeignKey(c => c.BrandId).OnDelete(DeleteBehavior.Cascade);

        // One campaign per brand per week. A second would mean two agents planning the same
        // seven days without knowing about each other.
        builder.HasIndex(c => new { c.BrandId, c.WeekStart }).IsUnique();
        builder.HasIndex(c => new { c.TenantId, c.Status });
    }
}

public sealed class ContentItemConfiguration : IEntityTypeConfiguration<ContentItem>
{
    public void Configure(EntityTypeBuilder<ContentItem> builder)
    {
        builder.ToTable("content_items");
        builder.HasKey(i => i.Id);

        builder.Property(i => i.TenantId).IsRequired();
        builder.Property(i => i.Type).HasConversion<short>().IsRequired();
        builder.Property(i => i.Status).HasConversion<short>().IsRequired();
        builder.Property(i => i.Topic).HasMaxLength(300).IsRequired();
        builder.Property(i => i.Pillar).HasMaxLength(80).IsRequired();
        builder.Property(i => i.Objective).HasMaxLength(300).IsRequired();
        builder.Property(i => i.RepetitionRisk).HasMaxLength(500);
        builder.Property(i => i.FailureReason).HasMaxLength(1000);
        builder.Property(i => i.CreatedAt).IsRequired();
        builder.Ignore(i => i.IsTerminal);

        builder.HasOne<ContentCampaign>()
            .WithMany().HasForeignKey(i => i.CampaignId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(i => new { i.CampaignId, i.Ordinal }).IsUnique();
        builder.HasIndex(i => new { i.TenantId, i.Status });
    }
}

public sealed class ContentHistoryEntryConfiguration : IEntityTypeConfiguration<ContentHistoryEntry>
{
    public void Configure(EntityTypeBuilder<ContentHistoryEntry> builder)
    {
        builder.ToTable("content_history");
        builder.HasKey(h => h.Id);

        builder.Property(h => h.TenantId).IsRequired();
        builder.Property(h => h.Type).HasConversion<short>().IsRequired();
        builder.Property(h => h.Topic).HasMaxLength(300).IsRequired();
        builder.Property(h => h.Pillar).HasMaxLength(80).IsRequired();
        builder.Property(h => h.Hook).HasMaxLength(500).IsRequired();
        builder.Property(h => h.TemplateId).HasMaxLength(80);
        builder.Property(h => h.ApprovedAt).IsRequired();

        builder.HasOne<Domain.Branding.Brand>()
            .WithMany().HasForeignKey(h => h.BrandId).OnDelete(DeleteBehavior.Cascade);

        // The strategist's context query: this brand, most recent first.
        builder.HasIndex(h => new { h.BrandId, h.ApprovedAt });
    }
}

public sealed class AgentRunConfiguration : IEntityTypeConfiguration<AgentRun>
{
    public void Configure(EntityTypeBuilder<AgentRun> builder)
    {
        builder.ToTable("agent_runs");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.TenantId).IsRequired();
        builder.Property(r => r.AgentName).HasMaxLength(80).IsRequired();
        builder.Property(r => r.AgentVersion).HasMaxLength(20).IsRequired();
        builder.Property(r => r.ModelId).HasMaxLength(120).IsRequired();
        builder.Property(r => r.Outcome).HasConversion<short>().IsRequired();
        builder.Property(r => r.ValidationFailure).HasMaxLength(2000);
        builder.Property(r => r.Error).HasMaxLength(2000);
        builder.Property(r => r.InputRef).HasMaxLength(500);
        builder.Property(r => r.OutputRef).HasMaxLength(500);
        builder.Property(r => r.TraceId).HasMaxLength(64);
        builder.Property(r => r.SpanId).HasMaxLength(32);
        builder.Property(r => r.StartedAt).IsRequired();

        builder.HasOne<ContentCampaign>()
            .WithMany().HasForeignKey(r => r.CampaignId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(r => new { r.CampaignId, r.StartedAt });

        // "Which prompt version produced bad output, and how often?" — the query that makes
        // a prompt change measurable rather than a matter of opinion.
        builder.HasIndex(r => new { r.AgentName, r.PromptVersionId, r.Outcome });
    }
}

public sealed class CostEntryConfiguration : IEntityTypeConfiguration<CostEntry>
{
    public void Configure(EntityTypeBuilder<CostEntry> builder)
    {
        builder.ToTable("cost_entries");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.TenantId).IsRequired();
        builder.Property(c => c.Kind).HasConversion<short>().IsRequired();
        builder.Property(c => c.Provider).HasMaxLength(60).IsRequired();
        builder.Property(c => c.Resource).HasMaxLength(120).IsRequired();
        builder.Property(c => c.IncurredAt).IsRequired();
        builder.Ignore(c => c.AmountUsd);

        builder.HasOne<ContentCampaign>()
            .WithMany().HasForeignKey(c => c.CampaignId).OnDelete(DeleteBehavior.Cascade);

        // The budget check sums this on every call, so it has to be an index seek.
        builder.HasIndex(c => c.CampaignId);
        builder.HasIndex(c => new { c.TenantId, c.IncurredAt });
    }
}

/// <summary>
/// Not tenant-owned: prompts are platform assets shared by every tenant, and a run in one
/// tenant must still resolve the version that produced it.
/// </summary>
public sealed class PromptVersionConfiguration : IEntityTypeConfiguration<PromptVersion>
{
    public void Configure(EntityTypeBuilder<PromptVersion> builder)
    {
        builder.ToTable("prompt_versions");
        builder.HasKey(p => p.Id);

        builder.Property(p => p.PromptId).HasMaxLength(80).IsRequired();
        builder.Property(p => p.Body).IsRequired();
        builder.Property(p => p.ContentHash).HasMaxLength(64).IsRequired();
        builder.Property(p => p.ModelProfile).HasMaxLength(60).IsRequired();
        builder.Property(p => p.SchemaName).HasMaxLength(80);
        builder.Property(p => p.RegisteredAt).IsRequired();
        builder.Ignore(p => p.Reference);

        builder.HasIndex(p => new { p.PromptId, p.Version }).IsUnique();
    }
}
