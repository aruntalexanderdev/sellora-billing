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
        builder.HasIndex(p => p.Sku).IsUnique();

        builder.HasOne(p => p.BaseUnit).WithMany().HasForeignKey(p => p.BaseUnitId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(p => p.Tax).WithMany().HasForeignKey(p => p.TaxId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class ProductSellingModeConfiguration : IEntityTypeConfiguration<ProductSellingMode>
{
    public void Configure(EntityTypeBuilder<ProductSellingMode> builder)
    {
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Label).HasMaxLength(60).IsRequired();
        builder.Property(m => m.ConversionFactorToBase).HasColumnType("numeric(18,3)");

        builder.HasOne(m => m.Product).WithMany(p => p.SellingModes).HasForeignKey(m => m.ProductId).OnDelete(DeleteBehavior.Cascade);
    }
}
