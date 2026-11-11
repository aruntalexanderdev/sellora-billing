using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.Domain.Entities;

namespace Sellora.Infrastructure.Data.Configurations;

public class StockAdjustmentConfiguration : IEntityTypeConfiguration<StockAdjustment>
{
    public void Configure(EntityTypeBuilder<StockAdjustment> builder)
    {
        builder.HasKey(a => a.Id);
        builder.Property(a => a.AdjustmentNo).HasMaxLength(30).IsRequired();
        builder.Property(a => a.Type).HasConversion<string>().HasMaxLength(20);
        builder.Property(a => a.Direction).HasConversion<string>().HasMaxLength(20);
        builder.Property(a => a.EnteredQuantity).HasColumnType("numeric(18,3)");
        builder.Property(a => a.UnitCode).HasMaxLength(10).IsRequired();
        builder.Property(a => a.ConversionFactorToBase).HasColumnType("numeric(28,10)");
        builder.Property(a => a.QuantityBaseUnits).HasColumnType("numeric(18,3)");
        builder.Property(a => a.EnteredUnitCost).HasColumnType("numeric(18,4)");
        builder.Property(a => a.UnitCost).HasColumnType("numeric(18,6)");
        builder.Property(a => a.BeforeQty).HasColumnType("numeric(18,3)");
        builder.Property(a => a.AfterQty).HasColumnType("numeric(18,3)");
        builder.Property(a => a.Reason).HasMaxLength(300);

        builder.HasIndex(a => new { a.CompanyId, a.AdjustmentNo }, "IX_StockAdjustments_CompanyId_AdjustmentNo").IsUnique();
        builder.HasIndex(a => new { a.CompanyId, a.BranchId, a.CreatedAt }, "IX_StockAdjustments_CompanyId_BranchId_CreatedAt");
        // Same-company branch: (CompanyId, BranchId) -> Branches (CompanyId, Id). Navigation-less (no filtered required navigation, Phase 1 section 8).
        builder.HasOne<Branch>().WithMany().HasForeignKey(a => new { a.CompanyId, a.BranchId })
            .HasPrincipalKey(b => new { b.CompanyId, b.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_StockAdjustments_Branches_CompanyId_BranchId");
        // Phase 8: the one location this document moves stock at. Same-company composite FK, navigation-less; FK index leads with CompanyId.
        builder.HasOne<StockLocation>().WithMany().HasForeignKey(a => new { a.CompanyId, a.StockLocationId })
            .HasPrincipalKey(l => new { l.CompanyId, l.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_StockAdjustments_StockLocations_CompanyId_StockLocationId");
        builder.HasIndex(a => new { a.CompanyId, a.StockLocationId }, "IX_StockAdjustments_CompanyId_StockLocationId");
        builder.HasIndex(a => a.CreatedAt);

        builder.HasOne(a => a.Product).WithMany().HasForeignKey(a => a.ProductId).OnDelete(DeleteBehavior.Restrict);
        // Same-company guarantee: the adjusted product belongs to the adjustment's company.
        builder.HasOne<Product>().WithMany().HasForeignKey(a => new { a.CompanyId, a.ProductId })
            .HasPrincipalKey(p => new { p.CompanyId, p.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(a => a.User).WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Restrict);
        // (ProductId, ProductUnitId) -> ProductUnits (ProductId, Id) via AK_ProductUnits_ProductId_Id: the entered unit
        // must belong to the adjusted product. Required, so the overlapping-FK null hazard does not apply.
        builder.HasOne(a => a.ProductUnit).WithMany()
            .HasForeignKey(a => new { a.ProductId, a.ProductUnitId })
            .HasPrincipalKey(u => new { u.ProductId, u.Id })
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(a => a.Unit).WithMany().HasForeignKey(a => a.UnitId).OnDelete(DeleteBehavior.Restrict);

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_StockAdjustments_EnteredQuantity_Positive", "\"EnteredQuantity\" > 0");
            t.HasCheckConstraint("CK_StockAdjustments_Factor_Positive", "\"ConversionFactorToBase\" > 0");
            // Exact: numeric multiplication is exact, and StockQuantity.EnsureStorable guarantees the 3-dp fit.
            t.HasCheckConstraint("CK_StockAdjustments_BaseQty_Arithmetic",
                "\"EnteredQuantity\" * \"ConversionFactorToBase\" = \"QuantityBaseUnits\"");
            t.HasCheckConstraint("CK_StockAdjustments_Cost_Pairing", "(\"UnitCost\" IS NULL) = (\"EnteredUnitCost\" IS NULL)");
        });
    }
}
