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
        builder.Property(a => a.QuantityBaseUnits).HasColumnType("numeric(18,3)");
        builder.Property(a => a.UnitCost).HasColumnType("numeric(18,4)");
        builder.Property(a => a.BeforeQty).HasColumnType("numeric(18,3)");
        builder.Property(a => a.AfterQty).HasColumnType("numeric(18,3)");
        builder.Property(a => a.Reason).HasMaxLength(300);

        builder.HasIndex(a => a.AdjustmentNo).IsUnique();

        builder.HasOne(a => a.Product).WithMany().HasForeignKey(a => a.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(a => a.User).WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Restrict);
    }
}
