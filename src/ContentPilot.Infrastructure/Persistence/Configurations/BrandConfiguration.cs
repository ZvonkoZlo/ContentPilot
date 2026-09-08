using ContentPilot.Domain.Branding;
using ContentPilot.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ContentPilot.Infrastructure.Persistence.Configurations;

public sealed class BrandConfiguration : IEntityTypeConfiguration<Brand>
{
    public void Configure(EntityTypeBuilder<Brand> builder)
    {
        builder.ToTable("brands");
        builder.HasKey(b => b.Id);

        builder.Property(b => b.TenantId).IsRequired();
        builder.Property(b => b.Name).HasMaxLength(200).IsRequired();
        builder.Property(b => b.Website).HasMaxLength(500);
        builder.Property(b => b.TimeZoneId).HasMaxLength(100).IsRequired();
        builder.Property(b => b.IsActive).HasDefaultValue(true);
        builder.Property(b => b.CreatedAt).IsRequired();

        builder.Property<List<string>>("_languages")
            .HasColumnName("languages")
            .HasColumnType("text[]")
            .UsePropertyAccessMode(PropertyAccessMode.Field)
            .IsRequired();

        builder.Ignore(b => b.Languages);
        builder.Ignore(b => b.PrimaryLanguage);

        builder.HasOne<Tenant>()
            .WithMany()
            .HasForeignKey(b => b.TenantId)
            .OnDelete(DeleteBehavior.Cascade);

        // Every hot query path leads with TenantId so the filtered plan stays cheap.
        builder.HasIndex(b => new { b.TenantId, b.Name });
    }
}
