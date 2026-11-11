using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.Domain.Entities;

namespace Sellora.Infrastructure.Data.Configurations;

public class TaxConfiguration : IEntityTypeConfiguration<Tax>
{
    public void Configure(EntityTypeBuilder<Tax> builder)
    {
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Name).HasMaxLength(60).IsRequired();
        builder.Property(t => t.RatePercent).HasColumnType("numeric(6,3)");
        builder.HasAlternateKey(t => new { t.CompanyId, t.Id });
        builder.HasIndex(t => new { t.CompanyId, t.Name }, "IX_Taxes_CompanyId_Name").IsUnique();
    }
}
