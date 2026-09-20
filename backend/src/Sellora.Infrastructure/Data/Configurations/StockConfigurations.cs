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

        builder.HasOne(m => m.Product).WithMany().HasForeignKey(m => m.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(m => m.User).WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(m => m.PurchaseBatch).WithMany().HasForeignKey(m => m.PurchaseBatchId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(m => new { m.ProductId, m.OccurredAt });
        builder.HasIndex(m => m.PurchaseBatchId);
    }
}

public class StockItemConfiguration : IEntityTypeConfiguration<StockItem>
{
    public void Configure(EntityTypeBuilder<StockItem> builder)
    {
        builder.HasKey(s => s.ProductId);
        builder.Property(s => s.QuantityOnHand).HasColumnType("numeric(18,3)");
        builder.Property(s => s.AverageCost).HasColumnType("numeric(18,4)");

        builder.HasOne(s => s.Product).WithOne().HasForeignKey<StockItem>(s => s.ProductId).OnDelete(DeleteBehavior.Cascade);
    }
}
