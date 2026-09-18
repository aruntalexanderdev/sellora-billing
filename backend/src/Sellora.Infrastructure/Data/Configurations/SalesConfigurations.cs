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

        foreach (var money in new[] { nameof(SalesInvoice.PaidAmount), nameof(SalesInvoice.Subtotal), nameof(SalesInvoice.TaxTotal), nameof(SalesInvoice.DiscountTotal), nameof(SalesInvoice.GrandTotal) })
        {
            builder.Property(money).HasColumnType("numeric(18,2)");
        }

        builder.Ignore(i => i.DueAmount);
        builder.Ignore(i => i.PaymentStatus);

        builder.HasIndex(i => i.InvoiceNo).IsUnique();

        builder.HasOne(i => i.Customer).WithMany().HasForeignKey(i => i.CustomerId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(i => i.CreatedByUser).WithMany().HasForeignKey(i => i.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
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
        builder.Property(c => c.UnitCost).HasColumnType("numeric(18,4)");

        builder.HasOne(c => c.SalesInvoiceLine).WithMany(l => l.BatchConsumptions).HasForeignKey(c => c.SalesInvoiceLineId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(c => c.PurchaseBatch).WithMany().HasForeignKey(c => c.PurchaseBatchId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(c => c.PurchaseBatchId);
    }
}
