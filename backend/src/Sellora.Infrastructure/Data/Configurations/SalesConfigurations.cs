using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.Domain.Entities;

namespace Sellora.Infrastructure.Data.Configurations;

public class SalesInvoiceConfiguration : IEntityTypeConfiguration<SalesInvoice>
{
    public void Configure(EntityTypeBuilder<SalesInvoice> builder)
    {
        builder.HasKey(i => i.Id);
        builder.Property(i => i.InvoiceNo).HasMaxLength(30).IsRequired();
        builder.Property(i => i.PaymentMethod).HasConversion<string>().HasMaxLength(20);
        builder.Property(i => i.Status).HasConversion<string>().HasMaxLength(20);

        foreach (var money in new[] { nameof(SalesInvoice.PaidAmount), nameof(SalesInvoice.ReturnedAmount), nameof(SalesInvoice.Subtotal), nameof(SalesInvoice.TaxTotal), nameof(SalesInvoice.DiscountTotal), nameof(SalesInvoice.GrandTotal) })
        {
            builder.Property(money).HasColumnType("numeric(18,2)");
        }

        builder.Ignore(i => i.NetTotal);
        builder.Ignore(i => i.DueAmount);
        builder.Ignore(i => i.RefundableAmount);
        builder.Ignore(i => i.PaymentStatus);

        builder.HasIndex(i => i.InvoiceNo).IsUnique();

        builder.HasOne(i => i.Customer).WithMany().HasForeignKey(i => i.CustomerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(i => i.CreatedByUser).WithMany().HasForeignKey(i => i.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class SalesPaymentConfiguration : IEntityTypeConfiguration<SalesPayment>
{
    public void Configure(EntityTypeBuilder<SalesPayment> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Amount).HasColumnType("numeric(18,2)");
        builder.Property(p => p.ExchangeRateToBase).HasColumnType("numeric(18,6)");
        builder.Property(p => p.BaseAmount).HasColumnType("numeric(18,2)");
        builder.Property(p => p.PaymentMethod).HasConversion<string>().HasMaxLength(20);

        builder.Property(p => p.Kind).HasConversion<string>().HasMaxLength(10).HasDefaultValue(PaymentEntryKind.Payment);
        builder.Property(p => p.Reason).HasMaxLength(200);

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_SalesPayments_Amount_Positive", "\"Amount\" > 0");
            // Only a reversal names the payment it undoes.
            t.HasCheckConstraint("CK_SalesPayments_ReversalLink", "(\"Kind\" = 'Reversal') = (\"ReversesPaymentId\" IS NOT NULL)");
        });
        // A payment can be reversed at most once.
        builder.HasIndex(p => p.ReversesPaymentId).IsUnique();
        builder.HasIndex(p => p.SalesReturnId);
        builder.HasIndex(p => new { p.SalesInvoiceId, p.CreatedAt });

        builder.HasOne(p => p.SalesInvoice).WithMany(i => i.Payments).HasForeignKey(p => p.SalesInvoiceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(p => p.ReversesPayment).WithMany().HasForeignKey(p => p.ReversesPaymentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(p => p.SalesReturn).WithMany().HasForeignKey(p => p.SalesReturnId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(p => p.CreatedByUser).WithMany().HasForeignKey(p => p.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class SalesReturnConfiguration : IEntityTypeConfiguration<SalesReturn>
{
    public void Configure(EntityTypeBuilder<SalesReturn> builder)
    {
        builder.HasKey(r => r.Id);
        builder.Property(r => r.ReturnNo).HasMaxLength(30).IsRequired();
        builder.Property(r => r.Reason).HasMaxLength(300);
        foreach (var money in new[] { nameof(SalesReturn.TaxableTotal), nameof(SalesReturn.TaxTotal), nameof(SalesReturn.Total) })
        {
            builder.Property(money).HasColumnType("numeric(18,2)");
        }

        builder.ToTable(t => t.HasCheckConstraint("CK_SalesReturns_Total_Positive", "\"Total\" >= 0"));
        builder.HasIndex(r => r.ReturnNo).IsUnique();
        builder.HasIndex(r => new { r.SalesInvoiceId, r.CreatedAt });

        builder.HasOne(r => r.SalesInvoice).WithMany(i => i.Returns).HasForeignKey(r => r.SalesInvoiceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(r => r.CreatedByUser).WithMany().HasForeignKey(r => r.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class SalesReturnLineConfiguration : IEntityTypeConfiguration<SalesReturnLine>
{
    public void Configure(EntityTypeBuilder<SalesReturnLine> builder)
    {
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Qty).HasColumnType("numeric(18,3)");
        builder.Property(l => l.BaseQty).HasColumnType("numeric(18,3)");
        foreach (var money in new[] { nameof(SalesReturnLine.TaxableValue), nameof(SalesReturnLine.TaxAmount), nameof(SalesReturnLine.LineTotal), nameof(SalesReturnLine.CostRestored) })
        {
            builder.Property(money).HasColumnType("numeric(18,4)");
        }

        builder.ToTable(t => t.HasCheckConstraint("CK_SalesReturnLines_Qty_Positive", "\"Qty\" > 0"));
        builder.HasIndex(l => l.SalesInvoiceLineId);

        builder.HasOne(l => l.SalesReturn).WithMany(r => r.Lines).HasForeignKey(l => l.SalesReturnId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(l => l.SalesInvoiceLine).WithMany().HasForeignKey(l => l.SalesInvoiceLineId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(l => l.Product).WithMany().HasForeignKey(l => l.ProductId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class SalesInvoiceLineConfiguration : IEntityTypeConfiguration<SalesInvoiceLine>
{
    public void Configure(EntityTypeBuilder<SalesInvoiceLine> builder)
    {
        builder.HasKey(l => l.Id);

        builder.Property(l => l.Qty).HasColumnType("numeric(18,3)");
        builder.Property(l => l.ConversionFactorToBase).HasColumnType("numeric(18,3)");
        builder.Property(l => l.BaseQty).HasColumnType("numeric(18,3)");
        builder.Property(l => l.TaxRatePercent).HasColumnType("numeric(6,3)");

        foreach (var money in new[] { nameof(SalesInvoiceLine.UnitPrice), nameof(SalesInvoiceLine.Discount), nameof(SalesInvoiceLine.TaxableValue), nameof(SalesInvoiceLine.TaxAmount), nameof(SalesInvoiceLine.LineTotal), nameof(SalesInvoiceLine.CostOfGoodsSold) })
        {
            builder.Property(money).HasColumnType("numeric(18,4)");
        }

        builder.HasOne(l => l.SalesInvoice).WithMany(i => i.Lines).HasForeignKey(l => l.SalesInvoiceId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(l => l.Product).WithMany().HasForeignKey(l => l.ProductId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(l => l.SellingMode).WithMany().HasForeignKey(l => l.SellingModeId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(l => l.Tax).WithMany().HasForeignKey(l => l.TaxId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class SalesInvoiceLineBatchConsumptionConfiguration : IEntityTypeConfiguration<SalesInvoiceLineBatchConsumption>
{
    public void Configure(EntityTypeBuilder<SalesInvoiceLineBatchConsumption> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Property(c => c.QtyBaseUnits).HasColumnType("numeric(18,3)");
        builder.Property(c => c.ReturnedQtyBaseUnits).HasColumnType("numeric(18,3)");
        builder.ToTable(t => t.HasCheckConstraint("CK_SalesConsumption_ReturnedWithinConsumed", "\"ReturnedQtyBaseUnits\" >= 0 AND \"ReturnedQtyBaseUnits\" <= \"QtyBaseUnits\""));
        builder.Property(c => c.UnitCost).HasColumnType("numeric(18,4)");

        builder.HasOne(c => c.SalesInvoiceLine).WithMany(l => l.BatchConsumptions).HasForeignKey(c => c.SalesInvoiceLineId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(c => c.PurchaseBatch).WithMany().HasForeignKey(c => c.PurchaseBatchId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(c => c.PurchaseBatchId);
    }
}
