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

        foreach (var money in new[] { nameof(PurchaseOrder.PaidAmount), nameof(PurchaseOrder.ReturnedAmount), nameof(PurchaseOrder.Subtotal), nameof(PurchaseOrder.TaxTotal), nameof(PurchaseOrder.OtherChargesTotal), nameof(PurchaseOrder.DiscountTotal), nameof(PurchaseOrder.GrandTotal) })
        {
            builder.Property(money).HasColumnType("numeric(18,2)");
        }

        builder.Property(p => p.ExchangeRateToBase).HasColumnType("numeric(18,6)");
        builder.Property(p => p.OriginalGrandTotal).HasColumnType("numeric(18,4)");
        builder.Property(p => p.OriginalPaidAmount).HasColumnType("numeric(18,4)");
        builder.Property(p => p.OriginalReturnedAmount).HasColumnType("numeric(18,4)");
        builder.Ignore(p => p.NetTotal);
        builder.Ignore(p => p.OriginalNetTotal);
        builder.Ignore(p => p.OriginalRefundableAmount);
        builder.Ignore(p => p.OriginalDueAmount);

        builder.Ignore(p => p.DueAmount);
        builder.Ignore(p => p.PaymentStatus);

        builder.HasIndex(p => p.PurchaseNo).IsUnique();

        builder.HasOne(p => p.Supplier).WithMany().HasForeignKey(p => p.SupplierId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(p => p.Currency).WithMany().HasForeignKey(p => p.CurrencyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(p => p.CreatedByUser).WithMany().HasForeignKey(p => p.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class PurchasePaymentConfiguration : IEntityTypeConfiguration<PurchasePayment>
{
    public void Configure(EntityTypeBuilder<PurchasePayment> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Amount).HasColumnType("numeric(18,4)");
        builder.Property(p => p.ExchangeRateToBase).HasColumnType("numeric(18,6)");
        builder.Property(p => p.BaseAmount).HasColumnType("numeric(18,2)");
        builder.Property(p => p.PaymentMethod).HasConversion<string>().HasMaxLength(20);

        builder.Property(p => p.Kind).HasConversion<string>().HasMaxLength(10).HasDefaultValue(PaymentEntryKind.Payment);
        builder.Property(p => p.Reason).HasMaxLength(200);

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_PurchasePayments_Amount_Positive", "\"Amount\" > 0");
            // Only a reversal names the payment it undoes.
            t.HasCheckConstraint("CK_PurchasePayments_ReversalLink", "(\"Kind\" = 'Reversal') = (\"ReversesPaymentId\" IS NOT NULL)");
        });
        // A payment can be reversed at most once.
        builder.HasIndex(p => p.ReversesPaymentId).IsUnique();
        builder.HasIndex(p => p.PurchaseReturnId);
        builder.HasIndex(p => new { p.PurchaseOrderId, p.CreatedAt });

        builder.HasOne(p => p.ReversesPayment).WithMany().HasForeignKey(p => p.ReversesPaymentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(p => p.PurchaseReturn).WithMany().HasForeignKey(p => p.PurchaseReturnId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(p => p.PurchaseOrder).WithMany(o => o.Payments).HasForeignKey(p => p.PurchaseOrderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(p => p.CreatedByUser).WithMany().HasForeignKey(p => p.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class PurchaseReturnConfiguration : IEntityTypeConfiguration<PurchaseReturn>
{
    public void Configure(EntityTypeBuilder<PurchaseReturn> builder)
    {
        builder.HasKey(r => r.Id);
        builder.Property(r => r.ReturnNo).HasMaxLength(30).IsRequired();
        builder.Property(r => r.Reason).HasMaxLength(300);
        foreach (var money in new[] { nameof(PurchaseReturn.TaxableTotal), nameof(PurchaseReturn.TaxTotal), nameof(PurchaseReturn.Total) })
        {
            builder.Property(money).HasColumnType("numeric(18,2)");
        }
        builder.Property(r => r.OriginalTotal).HasColumnType("numeric(18,4)");

        builder.ToTable(t => t.HasCheckConstraint("CK_PurchaseReturns_Total_Positive", "\"Total\" >= 0"));
        builder.HasIndex(r => r.ReturnNo).IsUnique();
        builder.HasIndex(r => new { r.PurchaseOrderId, r.CreatedAt });

        builder.HasOne(r => r.PurchaseOrder).WithMany(o => o.Returns).HasForeignKey(r => r.PurchaseOrderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(r => r.CreatedByUser).WithMany().HasForeignKey(r => r.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class PurchaseReturnLineConfiguration : IEntityTypeConfiguration<PurchaseReturnLine>
{
    public void Configure(EntityTypeBuilder<PurchaseReturnLine> builder)
    {
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Qty).HasColumnType("numeric(18,3)");
        builder.Property(l => l.BaseQty).HasColumnType("numeric(18,3)");
        foreach (var money in new[] { nameof(PurchaseReturnLine.TaxableValue), nameof(PurchaseReturnLine.TaxAmount), nameof(PurchaseReturnLine.LineTotal), nameof(PurchaseReturnLine.CostRemoved) })
        {
            builder.Property(money).HasColumnType("numeric(18,4)");
        }

        builder.ToTable(t => t.HasCheckConstraint("CK_PurchaseReturnLines_Qty_Positive", "\"Qty\" > 0"));
        builder.HasIndex(l => l.PurchaseOrderLineId);

        builder.HasOne(l => l.PurchaseReturn).WithMany(r => r.Lines).HasForeignKey(l => l.PurchaseReturnId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(l => l.PurchaseOrderLine).WithMany().HasForeignKey(l => l.PurchaseOrderLineId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(l => l.Product).WithMany().HasForeignKey(l => l.ProductId).OnDelete(DeleteBehavior.Restrict);
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
