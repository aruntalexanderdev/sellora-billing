using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.Domain.Entities;

namespace Sellora.Infrastructure.Data.Configurations;

public class PurchaseOrderConfiguration : IEntityTypeConfiguration<PurchaseOrder>
{
    public void Configure(EntityTypeBuilder<PurchaseOrder> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Property(p => p.PurchaseNo).HasMaxLength(30).IsRequired();
        builder.Property(p => p.SupplierInvoiceNumber).HasMaxLength(60);
        builder.Property(p => p.Notes).HasMaxLength(500);
        builder.Property(p => p.PaymentMethod).HasConversion<string>().HasMaxLength(20);
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);

        foreach (var money in new[] { nameof(PurchaseOrder.PaidAmount), nameof(PurchaseOrder.Subtotal), nameof(PurchaseOrder.TaxTotal), nameof(PurchaseOrder.OtherChargesTotal), nameof(PurchaseOrder.DiscountTotal), nameof(PurchaseOrder.GrandTotal) })
        {
            builder.Property(money).HasColumnType("numeric(18,2)");
        }

        builder.Ignore(p => p.DueAmount);
        builder.Ignore(p => p.PaymentStatus);

        builder.HasIndex(p => p.PurchaseNo).IsUnique();

        builder.HasOne(p => p.Supplier).WithMany().HasForeignKey(p => p.SupplierId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(p => p.CreatedByUser).WithMany().HasForeignKey(p => p.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class PurchaseOrderLineConfiguration : IEntityTypeConfiguration<PurchaseOrderLine>
{
    public void Configure(EntityTypeBuilder<PurchaseOrderLine> builder)
    {
        builder.HasKey(l => l.Id);

        builder.Property(l => l.Qty).HasColumnType("numeric(18,3)");
        builder.Property(l => l.ConversionFactorToBase).HasColumnType("numeric(18,3)");
        builder.Property(l => l.BaseQty).HasColumnType("numeric(18,3)");
        builder.Property(l => l.TaxRatePercent).HasColumnType("numeric(6,3)");

        foreach (var money in new[] { nameof(PurchaseOrderLine.Rate), nameof(PurchaseOrderLine.OtherCharges), nameof(PurchaseOrderLine.Discount), nameof(PurchaseOrderLine.TaxableValue), nameof(PurchaseOrderLine.TaxAmount), nameof(PurchaseOrderLine.LineTotal), nameof(PurchaseOrderLine.UnitCost) })
        {
            builder.Property(money).HasColumnType("numeric(18,4)");
        }

        builder.HasOne(l => l.PurchaseOrder).WithMany(p => p.Lines).HasForeignKey(l => l.PurchaseOrderId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(l => l.Product).WithMany().HasForeignKey(l => l.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(l => l.SellingMode).WithMany().HasForeignKey(l => l.SellingModeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(l => l.Tax).WithMany().HasForeignKey(l => l.TaxId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class PurchaseBatchConfiguration : IEntityTypeConfiguration<PurchaseBatch>
{
    public void Configure(EntityTypeBuilder<PurchaseBatch> builder)
    {
        builder.HasKey(b => b.Id);
        builder.Property(b => b.PurchasedBaseQty).HasColumnType("numeric(18,3)");
        builder.Property(b => b.RemainingBaseQty).HasColumnType("numeric(18,3)");
        builder.Property(b => b.UnitCost).HasColumnType("numeric(18,4)");

        builder.HasOne(b => b.PurchaseOrderLine).WithOne(l => l.Batch).HasForeignKey<PurchaseBatch>(b => b.PurchaseOrderLineId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(b => b.StockAdjustment).WithOne(a => a.Batch).HasForeignKey<PurchaseBatch>(b => b.StockAdjustmentId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(b => b.Product).WithMany().HasForeignKey(b => b.ProductId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(b => new { b.ProductId, b.CreatedAt });
    }
}
