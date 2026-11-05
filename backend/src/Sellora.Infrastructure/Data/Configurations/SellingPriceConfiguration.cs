using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.Domain.Entities;

namespace Sellora.Infrastructure.Data.Configurations;

public class SellingPriceConfiguration : IEntityTypeConfiguration<SellingPrice>
{
    public void Configure(EntityTypeBuilder<SellingPrice> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Price).HasColumnType("numeric(18,4)");

        // Restrict: price history must never be deleted silently with its product unit.
        builder.HasOne(p => p.ProductUnit).WithMany().HasForeignKey(p => p.ProductUnitId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(p => p.CreatedByUser).WithMany().HasForeignKey(p => p.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(p => new { p.ProductUnitId, p.EffectiveFrom });
    }
}
