using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.Domain.Entities;

namespace Sellora.Infrastructure.Data.Configurations;

public class CurrencyConfiguration : IEntityTypeConfiguration<Currency>
{
    public void Configure(EntityTypeBuilder<Currency> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Code).HasMaxLength(3).IsRequired();
        builder.Property(c => c.Name).HasMaxLength(80).IsRequired();
        builder.Property(c => c.Symbol).HasMaxLength(8).IsRequired();
        builder.HasIndex(c => c.Code).IsUnique();
    }
}

public class ExchangeRateConfiguration : IEntityTypeConfiguration<ExchangeRate>
{
    public void Configure(EntityTypeBuilder<ExchangeRate> builder)
    {
        builder.HasKey(r => r.Id);
        builder.Property(r => r.RateToBase).HasColumnType("numeric(18,6)");

        builder.HasOne(r => r.Currency).WithMany().HasForeignKey(r => r.CurrencyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(r => r.CreatedByUser).WithMany().HasForeignKey(r => r.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(r => new { r.CompanyId, r.CurrencyId, r.EffectiveDate }, "IX_ExchangeRates_CompanyId_CurrencyId_EffectiveDate").IsUnique();
    }
}
