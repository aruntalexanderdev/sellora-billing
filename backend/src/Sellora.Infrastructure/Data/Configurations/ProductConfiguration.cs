using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.Domain.Entities;

namespace Sellora.Infrastructure.Data.Configurations;

public class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Sku).HasMaxLength(40).IsRequired();
        builder.Property(p => p.Name).HasMaxLength(150).IsRequired();
        builder.Property(p => p.Category).HasMaxLength(60);
        builder.Property(p => p.ReorderLevel).HasColumnType("numeric(18,3)");
        // Company-scoped unique (two companies may use the same SKU); (CompanyId, Id) is the target of same-company composite FKs.
        builder.HasAlternateKey(p => new { p.CompanyId, p.Id });
        builder.HasIndex(p => new { p.CompanyId, p.Sku }, "IX_Products_CompanyId_Sku").IsUnique();

        builder.HasOne(p => p.BaseUnit).WithMany().HasForeignKey(p => p.BaseUnitId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(p => p.Tax).WithMany().HasForeignKey(p => p.TaxId).OnDelete(DeleteBehavior.Restrict);
        // Same-company guarantee: a product's tax belongs to the product's company.
        builder.HasOne<Tax>().WithMany().HasForeignKey(p => new { p.CompanyId, p.TaxId })
            .HasPrincipalKey(t => new { t.CompanyId, t.Id }).OnDelete(DeleteBehavior.Restrict);

        // Phase 08 category masters. Navigation-less; both FKs are MATCH SIMPLE so a NULL CategoryId/SubcategoryId skips the check.
        builder.HasOne<ProductCategory>().WithMany().HasForeignKey(p => new { p.CompanyId, p.CategoryId })
            .HasPrincipalKey(c => new { c.CompanyId, c.Id }).IsRequired(false).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_Products_ProductCategories_CompanyId_CategoryId");
        // (CompanyId, CategoryId, SubcategoryId) -> (CompanyId, CategoryId, Id): the subcategory must belong to the product's category.
        builder.HasOne<ProductSubcategory>().WithMany().HasForeignKey(p => new { p.CompanyId, p.CategoryId, p.SubcategoryId })
            .HasPrincipalKey(s => new { s.CompanyId, s.CategoryId, s.Id }).IsRequired(false).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_Products_ProductSubcategories_CompanyId_CategoryId_SubcategoryId");
        builder.HasIndex(p => new { p.CompanyId, p.CategoryId }, "IX_Products_CompanyId_CategoryId");
        builder.HasIndex(p => new { p.CompanyId, p.SubcategoryId }, "IX_Products_CompanyId_SubcategoryId");
        builder.ToTable(t => t.HasCheckConstraint("CK_Products_Subcategory_Requires_Category", "\"SubcategoryId\" IS NULL OR \"CategoryId\" IS NOT NULL"));
    }
}

public class ProductUnitConfiguration : IEntityTypeConfiguration<ProductUnit>
{
    public void Configure(EntityTypeBuilder<ProductUnit> builder)
    {
        builder.HasKey(u => u.Id);
        // Target of the composite inner FK: an inner unit must belong to the same product.
        builder.HasAlternateKey(u => new { u.ProductId, u.Id });

        builder.Property(u => u.Label).HasMaxLength(60);
        builder.Property(u => u.Barcode).HasMaxLength(50);
        builder.Property(u => u.InnerQuantity).HasColumnType("numeric(18,6)");
        builder.Property(u => u.ConversionFactorToBase).HasColumnType("numeric(28,10)");

        builder.HasOne(u => u.Product).WithMany(p => p.Units).HasForeignKey(u => u.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(u => u.Unit).WithMany().HasForeignKey(u => u.UnitId).OnDelete(DeleteBehavior.Restrict);
        // Same-company guarantee: a unit row belongs to the company of its product.
        builder.HasOne<Product>().WithMany().HasForeignKey(u => new { u.CompanyId, u.ProductId })
            .HasPrincipalKey(p => new { p.CompanyId, p.Id }).OnDelete(DeleteBehavior.Restrict);
        // (ProductId, InnerProductUnitId) -> (ProductId, Id). Optional: MATCH SIMPLE skips the check while
        // InnerProductUnitId is NULL (base row). Services set the scalar only; never assign InnerProductUnit = null.
        builder.HasOne(u => u.InnerProductUnit).WithMany()
            .HasForeignKey(u => new { u.ProductId, u.InnerProductUnitId })
            .HasPrincipalKey(u => new { u.ProductId, u.Id })
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Restrict);

        // Partial unique indexes. (ProductId, UnitId) is unique among ACTIVE rows only, so a used row can be
        // deactivated and replaced by a new row of the same unit (deactivate-and-replace).
        builder.HasIndex(u => new { u.ProductId, u.UnitId }, "IX_ProductUnits_ProductId_UnitId")
            .IsUnique().HasFilter("\"IsActive\"");
        builder.HasIndex(u => u.ProductId, "IX_ProductUnits_ProductId_Base")
            .IsUnique().HasFilter("\"IsBaseUnit\"");
        builder.HasIndex(u => u.ProductId, "IX_ProductUnits_ProductId_DefaultPurchase")
            .IsUnique().HasFilter("\"IsDefaultPurchaseUnit\"");
        builder.HasIndex(u => u.ProductId, "IX_ProductUnits_ProductId_DefaultSales")
            .IsUnique().HasFilter("\"IsDefaultSalesUnit\"");
        builder.HasIndex(u => new { u.CompanyId, u.Barcode }, "IX_ProductUnits_CompanyId_Barcode")
            .IsUnique().HasFilter("\"Barcode\" IS NOT NULL AND \"IsActive\"");

        // Rules SQL cannot express (base UnitId = Product.BaseUnitId, cycles, category compatibility, factor
        // materialisation) are enforced by the product service.
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_ProductUnits_Base_NoInner", "\"IsBaseUnit\" = (\"InnerProductUnitId\" IS NULL)");
            t.HasCheckConstraint("CK_ProductUnits_InnerQuantity_Positive", "\"InnerQuantity\" > 0");
            t.HasCheckConstraint("CK_ProductUnits_Factor_Positive", "\"ConversionFactorToBase\" > 0");
            t.HasCheckConstraint("CK_ProductUnits_Base_Identity",
                "NOT \"IsBaseUnit\" OR (\"InnerQuantity\" = 1 AND \"ConversionFactorToBase\" = 1)");
            t.HasCheckConstraint("CK_ProductUnits_Base_Active", "NOT \"IsBaseUnit\" OR \"IsActive\"");
            t.HasCheckConstraint("CK_ProductUnits_Inner_NotSelf", "\"InnerProductUnitId\" IS NULL OR \"InnerProductUnitId\" <> \"Id\"");
            t.HasCheckConstraint("CK_ProductUnits_DefaultPurchase", "NOT \"IsDefaultPurchaseUnit\" OR (\"IsPurchasable\" AND \"IsActive\")");
            t.HasCheckConstraint("CK_ProductUnits_DefaultSales", "NOT \"IsDefaultSalesUnit\" OR (\"IsSellable\" AND \"IsActive\")");
        });
    }
}
