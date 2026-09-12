using ContentPilot.Domain.Evals;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ContentPilot.Infrastructure.Persistence.Configurations;

public sealed class EvalRunConfiguration : IEntityTypeConfiguration<EvalRun>
{
    public void Configure(EntityTypeBuilder<EvalRun> builder)
    {
        builder.ToTable("eval_runs");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Mode).HasConversion<short>().IsRequired();
        builder.Property(r => r.RunAt).IsRequired();
        builder.Ignore(r => r.AllPassed);

        // The trend page's query: every run, newest first.
        builder.HasIndex(r => r.RunAt);
    }
}

public sealed class EvalResultConfiguration : IEntityTypeConfiguration<EvalResult>
{
    public void Configure(EntityTypeBuilder<EvalResult> builder)
    {
        builder.ToTable("eval_results");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.ScenarioName).HasMaxLength(200).IsRequired();
        builder.Property(r => r.Kind).HasConversion<short>().IsRequired();
        builder.Property(r => r.Detail).HasMaxLength(2000).IsRequired();

        builder.HasOne<EvalRun>().WithMany().HasForeignKey(r => r.EvalRunId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(r => new { r.EvalRunId, r.ScenarioName });

        // One scenario's history over time, regardless of which run it was part of —
        // the trend a scenario going from green to red matters more than any single run.
        builder.HasIndex(r => r.ScenarioName);
    }
}
