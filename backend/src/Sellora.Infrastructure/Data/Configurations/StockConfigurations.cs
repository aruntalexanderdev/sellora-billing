using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.Domain.Entities;

namespace Sellora.Infrastructure.Data.Configurations;

public class StockMovementConfiguration : IEntityTypeConfiguration<StockMovement>
{
    public void Configure(EntityTypeBuilder<StockMovement> builder)
    {
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Type).HasConversion<string>().HasMaxLength(20);
        builder.Property(m => m.QuantityBaseUnits).HasColumnType("numeric(18,3)");
        builder.Property(m => m.ReferenceType).HasMaxLength(40).IsRequired();
        builder.Property(m => m.ReferenceNo).HasMaxLength(30).IsRequired();
        builder.Property(m => m.Notes).HasMaxLength(300);
        builder.Property(m => m.ReasonCode).HasConversion<string>().HasMaxLength(20);
        builder.Property(m => m.EnteredQuantity).HasColumnType("numeric(18,3)");
        builder.Property(m => m.EnteredUnitCode).HasMaxLength(10);
        builder.Property(m => m.ConversionFactorSnapshot).HasColumnType("numeric(28,10)");

        builder.HasOne(m => m.Product).WithMany().HasForeignKey(m => m.ProductId).OnDelete(DeleteBehavior.Restrict);
        // Same-company guarantees (ledger row, its product and its batch share the company).
        builder.HasOne<Product>().WithMany().HasForeignKey(m => new { m.CompanyId, m.ProductId })
            .HasPrincipalKey(p => new { p.CompanyId, p.Id }).OnDelete(DeleteBehavior.Restrict);
        // Optional: MATCH SIMPLE skips the check while PurchaseBatchId is NULL. Services set the scalar only.
        builder.HasOne<PurchaseBatch>().WithMany().HasForeignKey(m => new { m.CompanyId, m.PurchaseBatchId })
            .HasPrincipalKey(b => new { b.CompanyId, b.Id }).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(m => m.User).WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(m => m.PurchaseBatch).WithMany().HasForeignKey(m => m.PurchaseBatchId).OnDelete(DeleteBehavior.Restrict);

        // Same-product composite FK (alternate key on ProductUnits (ProductId, Id)), as StockAdjustmentConfiguration.
        builder.HasOne(m => m.EnteredProductUnit).WithMany()
            .HasForeignKey(m => new { m.ProductId, m.EnteredProductUnitId })
            .HasPrincipalKey(u => new { u.ProductId, u.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(m => new { m.ProductId, m.OccurredAt });
        builder.HasIndex(m => m.PurchaseBatchId);
        builder.HasIndex(m => new { m.ReferenceType, m.ReferenceId });

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_StockMovements_EnteredUnit_AllOrNone",
                "(\"EnteredProductUnitId\" IS NULL) = (\"EnteredUnitCode\" IS NULL) AND (\"EnteredProductUnitId\" IS NULL) = (\"ConversionFactorSnapshot\" IS NULL)");
            t.HasCheckConstraint("CK_StockMovements_EnteredQuantity_RequiresUnit", "\"EnteredQuantity\" IS NULL OR \"EnteredProductUnitId\" IS NOT NULL");
            t.HasCheckConstraint("CK_StockMovements_EnteredQuantity_Positive", "\"EnteredQuantity\" IS NULL OR \"EnteredQuantity\" > 0");
            t.HasCheckConstraint("CK_StockMovements_Factor_Positive", "\"ConversionFactorSnapshot\" IS NULL OR \"ConversionFactorSnapshot\" > 0");
            t.HasCheckConstraint("CK_StockMovements_UnitCode_NotEmpty", "\"EnteredUnitCode\" IS NULL OR char_length(\"EnteredUnitCode\") > 0");
            t.HasCheckConstraint("CK_StockMovements_Entered_Arithmetic", "\"EnteredQuantity\" IS NULL OR \"EnteredQuantity\" * \"ConversionFactorSnapshot\" = abs(\"QuantityBaseUnits\")");
            t.HasCheckConstraint("CK_StockMovements_ReasonCode",
                "(\"Type\" NOT IN ('Adjustment', 'OpeningStock') OR \"ReasonCode\" IS NOT NULL) AND (\"Type\" <> 'OpeningStock' OR \"ReasonCode\" = 'OpeningStock') AND (\"Type\" <> 'Adjustment' OR \"ReasonCode\" <> 'OpeningStock')");
            t.HasCheckConstraint("CK_StockMovements_Type_Sign",
                "(\"Type\" NOT IN ('Purchase', 'SaleReturn', 'SaleCancellation', 'OpeningStock') OR \"QuantityBaseUnits\" > 0) AND (\"Type\" NOT IN ('Sale', 'PurchaseReturn', 'PurchaseCancellation') OR \"QuantityBaseUnits\" < 0)");
        });
    }
}

public class StockItemConfiguration : IEntityTypeConfiguration<StockItem>
{
    public void Configure(EntityTypeBuilder<StockItem> builder)
    {
        builder.HasKey(s => s.ProductId);
        builder.Property(s => s.QuantityOnHand).HasColumnType("numeric(18,3)");
        builder.Property(s => s.AverageCost).HasColumnType("numeric(18,6)");

        builder.HasOne(s => s.Product).WithOne().HasForeignKey<StockItem>(s => s.ProductId).OnDelete(DeleteBehavior.Cascade);
        // Same-company guarantee: the stock row and its product share the company.
        builder.HasOne<Product>().WithMany().HasForeignKey(s => new { s.CompanyId, s.ProductId })
            .HasPrincipalKey(p => new { p.CompanyId, p.Id }).OnDelete(DeleteBehavior.Cascade);
    }
}
