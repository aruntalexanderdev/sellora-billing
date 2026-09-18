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

        builder.HasOne(p => p.ProductSellingMode).WithMany().HasForeignKey(p => p.ProductSellingModeId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(p => p.CreatedByUser).WithMany().HasForeignKey(p => p.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(p => new { p.ProductSellingModeId, p.EffectiveFrom });
    }
}
