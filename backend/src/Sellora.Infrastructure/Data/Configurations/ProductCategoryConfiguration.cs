using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.Domain.Entities;

namespace Sellora.Infrastructure.Data.Configurations;

public class ProductCategoryConfiguration : IEntityTypeConfiguration<ProductCategory>
{
    public void Configure(EntityTypeBuilder<ProductCategory> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Code).HasMaxLength(20).IsRequired();
        builder.Property(c => c.Name).HasMaxLength(60).IsRequired();
        // Target of the same-company FKs from ProductSubcategories and Products.
        builder.HasAlternateKey(c => new { c.CompanyId, c.Id });
        builder.HasIndex(c => new { c.CompanyId, c.Code }, "IX_ProductCategories_CompanyId_Code").IsUnique();
        // Case-insensitive name uniqueness is the expression index UX_ProductCategories_CompanyId_Name_Lower
        // ((CompanyId, lower(Name))). EF cannot model expression indexes: it lives only in migration AddProductCategoryMasters
        // (migrationBuilder.Sql); keep it when touching these indexes.
        builder.ToTable(t => t.HasCheckConstraint("CK_ProductCategories_Code_Format", "\"Code\" ~ '^[A-Z0-9][A-Z0-9-]{0,19}$'"));
    }
}

public class ProductSubcategoryConfiguration : IEntityTypeConfiguration<ProductSubcategory>
{
    public void Configure(EntityTypeBuilder<ProductSubcategory> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Code).HasMaxLength(20).IsRequired();
        builder.Property(s => s.Name).HasMaxLength(60).IsRequired();
        // Target of the Products composite FK (a product's subcategory must belong to the product's category).
        builder.HasAlternateKey(s => new { s.CompanyId, s.CategoryId, s.Id });
        builder.HasIndex(s => new { s.CompanyId, s.CategoryId, s.Code }, "IX_ProductSubcategories_CompanyId_CategoryId_Code").IsUnique();
        // Case-insensitive name uniqueness: expression index UX_ProductSubcategories_Category_Name_Lower
        // ((CompanyId, CategoryId, lower(Name))), created only in migration AddProductCategoryMasters.

        // Same-company parent: (CompanyId, CategoryId) -> ProductCategories (CompanyId, Id); the only FK to the parent.
        builder.HasOne(s => s.Category).WithMany(c => c.Subcategories).HasForeignKey(s => new { s.CompanyId, s.CategoryId })
            .HasPrincipalKey(c => new { c.CompanyId, c.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_ProductSubcategories_ProductCategories_CompanyId_CategoryId");
        builder.Property(s => s.HsnCode).HasMaxLength(8);
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_ProductSubcategories_Code_Format", "\"Code\" ~ '^[A-Z0-9][A-Z0-9-]{0,19}$'");
            t.HasCheckConstraint("CK_ProductSubcategories_HsnCode_Format", TaxValues.HsnFormat);
        });
    }
}
