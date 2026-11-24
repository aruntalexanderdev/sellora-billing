using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.Domain.Entities;

namespace Sellora.Infrastructure.Data.Configurations;

public class BusinessSettingsConfiguration : IEntityTypeConfiguration<BusinessSettings>
{
    public void Configure(EntityTypeBuilder<BusinessSettings> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.BusinessName).HasMaxLength(150).IsRequired();
        builder.Property(s => s.InvoicePrefix).HasMaxLength(10).IsRequired();
        builder.Property(s => s.AddressLine).HasMaxLength(300);
        builder.Property(s => s.City).HasMaxLength(80);
        builder.Property(s => s.State).HasMaxLength(80);
        builder.Property(s => s.Gstin).HasMaxLength(20);
        builder.Property(s => s.Phone).HasMaxLength(30);
        builder.Property(s => s.Email).HasMaxLength(200);

        builder.Property(s => s.ReceiptPostingMode).HasConversion<string>().HasMaxLength(20).HasDefaultValue(ReceiptPostingMode.Direct);
        builder.Property(s => s.ReceiptSeparationOfDuties).HasDefaultValue(false);
        // SellingPricesIncludeTax (T18): NOT NULL, no model default; the migration adds it DEFAULT false and drops the default.
        builder.Property(s => s.MaxBillDiscountPercent).HasColumnType("numeric(5,2)");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_BusinessSettings_ReceiptPostingMode_Valid", "\"ReceiptPostingMode\" IN ('Direct', 'SupervisorApproval')");
            t.HasCheckConstraint("CK_BusinessSettings_MaxBillDiscountPercent_Range", "\"MaxBillDiscountPercent\" IS NULL OR (\"MaxBillDiscountPercent\" >= 0 AND \"MaxBillDiscountPercent\" <= 100)");
        });

        // One profile per company (1:1 with Companies, ADR-07).
        builder.HasIndex(s => s.CompanyId, "IX_BusinessSettings_CompanyId").IsUnique();

        builder.HasOne(s => s.BaseCurrency).WithMany().HasForeignKey(s => s.BaseCurrencyId).OnDelete(DeleteBehavior.Restrict);

        GeoLinkConfiguration.Apply(builder, "BusinessSettings");
    }
}
