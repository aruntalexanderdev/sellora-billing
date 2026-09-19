using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.Domain.Entities;

namespace Sellora.Infrastructure.Data.Configurations;

public class FinancialTransactionConfiguration : IEntityTypeConfiguration<FinancialTransaction>
{
    public void Configure(EntityTypeBuilder<FinancialTransaction> builder)
    {
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Type).HasConversion<string>().HasMaxLength(30);
        builder.Property(t => t.Direction).HasConversion<string>().HasMaxLength(10);
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(t => t.PaymentMethod).HasConversion<string>().HasMaxLength(20);

        builder.Property(t => t.Amount).HasColumnType("numeric(18,4)");
        builder.Property(t => t.ExchangeRateToBase).HasColumnType("numeric(18,6)");
        builder.Property(t => t.BaseAmount).HasColumnType("numeric(18,2)");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_FinancialTransactions_Amount_Positive", "\"Amount\" > 0");
            t.HasCheckConstraint("CK_FinancialTransactions_OneSource",
                "(CASE WHEN \"PurchasePaymentId\" IS NULL THEN 0 ELSE 1 END + CASE WHEN \"SalesPaymentId\" IS NULL THEN 0 ELSE 1 END + CASE WHEN \"ExpenseId\" IS NULL THEN 0 ELSE 1 END) = 1");
        });

        // A source record produces at most one transaction (nulls are not compared by a unique index).
        builder.HasIndex(t => t.PurchasePaymentId).IsUnique();
        builder.HasIndex(t => t.SalesPaymentId).IsUnique();
        builder.HasIndex(t => t.ExpenseId).IsUnique();
        builder.HasIndex(t => new { t.TransactionDate, t.CreatedAt });
        builder.HasIndex(t => new { t.Type, t.Status });

        builder.HasOne(t => t.Currency).WithMany().HasForeignKey(t => t.CurrencyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(t => t.PurchasePayment).WithMany().HasForeignKey(t => t.PurchasePaymentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(t => t.SalesPayment).WithMany().HasForeignKey(t => t.SalesPaymentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(t => t.Expense).WithMany().HasForeignKey(t => t.ExpenseId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(t => t.CreatedByUser).WithMany().HasForeignKey(t => t.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
