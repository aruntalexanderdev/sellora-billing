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
        builder.Property(t => t.FundingSource).HasConversion<string>().HasMaxLength(20);
        builder.Property(t => t.SourceReference).HasMaxLength(100);
        builder.Property(t => t.Counterparty).HasMaxLength(120);
        builder.Property(t => t.Reason).HasMaxLength(300);
        builder.Property(t => t.PaymentMethod).HasConversion<string>().HasMaxLength(20);

        builder.Property(t => t.Amount).HasColumnType("numeric(18,4)");
        builder.Property(t => t.ExchangeRateToBase).HasColumnType("numeric(18,6)");
        builder.Property(t => t.BaseAmount).HasColumnType("numeric(18,2)");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_FinancialTransactions_Amount_Positive", "\"Amount\" > 0");
            // Payment/expense movements come from exactly one source record; opening balances and transfers
            // are entered directly, have no source, and must name their account.
            t.HasCheckConstraint("CK_FinancialTransactions_Source",
                "(CASE WHEN \"PurchasePaymentId\" IS NULL THEN 0 ELSE 1 END + CASE WHEN \"SalesPaymentId\" IS NULL THEN 0 ELSE 1 END + CASE WHEN \"ExpenseId\" IS NULL THEN 0 ELSE 1 END) = "
                + "CASE WHEN \"Type\" IN ('OpeningBalance', 'Transfer', 'Funding', 'Reversal') THEN 0 ELSE 1 END");
            t.HasCheckConstraint("CK_FinancialTransactions_ManualHasAccount",
                "\"Type\" NOT IN ('OpeningBalance', 'Transfer', 'Funding', 'Reversal') OR \"AccountId\" IS NOT NULL");
            t.HasCheckConstraint("CK_FinancialTransactions_Funding",
                "(\"Type\" = 'Funding') = (\"FundingSource\" IS NOT NULL) AND (\"Type\" <> 'Funding' OR \"Direction\" = 'In')");
            t.HasCheckConstraint("CK_FinancialTransactions_CustomerReceipt",
                "(\"FundingSource\" IS DISTINCT FROM 'CustomerReceipt' OR (\"CustomerId\" IS NOT NULL AND \"SourceReference\" IS NOT NULL)) "
                + "AND (\"CustomerId\" IS NULL OR \"FundingSource\" = 'CustomerReceipt')");
            t.HasCheckConstraint("CK_FinancialTransactions_OtherReason",
                "\"FundingSource\" IS DISTINCT FROM 'Other' OR \"Reason\" IS NOT NULL");
            t.HasCheckConstraint("CK_FinancialTransactions_Reversal",
                "(\"Type\" = 'Reversal') = (\"ReversalOfTransactionId\" IS NOT NULL) AND (\"Type\" <> 'Reversal' OR \"Reason\" IS NOT NULL)");
        });

        // A reversal is posted at most once per entry.
        builder.HasIndex(t => t.ReversalOfTransactionId).IsUnique().HasFilter("\"ReversalOfTransactionId\" IS NOT NULL");
        builder.HasOne<FinancialTransaction>().WithMany().HasForeignKey(t => t.ReversalOfTransactionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Customer>().WithMany().HasForeignKey(t => new { t.CompanyId, t.CustomerId })
            .HasPrincipalKey(c => new { c.CompanyId, c.Id }).IsRequired(false).OnDelete(DeleteBehavior.Restrict);

        // A source record produces at most one transaction (nulls are not compared by a unique index).
        builder.HasIndex(t => t.PurchasePaymentId).IsUnique();
        builder.HasIndex(t => t.SalesPaymentId).IsUnique();
        builder.HasIndex(t => t.ExpenseId).IsUnique();
        builder.HasIndex(t => new { t.TransactionDate, t.CreatedAt });
        builder.HasIndex(t => new { t.Type, t.Status });
        builder.HasIndex(t => new { t.AccountId, t.TransactionDate });
        // Release readiness (FI-D43 e): ledger list order, transfer-leg lookup and per-account balance aggregation.
        builder.HasIndex(t => new { t.CompanyId, t.TransactionDate, t.CreatedAt, t.Id }, "IX_FinancialTransactions_CompanyId_Date_CreatedAt_Id")
            .IsDescending(false, true, true, false);
        builder.HasIndex(t => new { t.CompanyId, t.TransferGroupId }, "IX_FinancialTransactions_CompanyId_TransferGroupId")
            .HasFilter("\"TransferGroupId\" IS NOT NULL");
        builder.HasIndex(t => new { t.CompanyId, t.AccountId, t.Status }, "IX_FinancialTransactions_CompanyId_AccountId_Status")
            .IncludeProperties(t => new { t.Direction, t.BaseAmount, t.ReconciledAt });
        builder.Property(t => t.Notes).HasMaxLength(500);

        builder.HasOne(t => t.Currency).WithMany().HasForeignKey(t => t.CurrencyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(t => t.PurchasePayment).WithMany().HasForeignKey(t => t.PurchasePaymentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(t => t.SalesPayment).WithMany().HasForeignKey(t => t.SalesPaymentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(t => t.Expense).WithMany().HasForeignKey(t => t.ExpenseId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(t => t.Account).WithMany().HasForeignKey(t => t.AccountId).OnDelete(DeleteBehavior.Restrict);
        // Same-company guarantee. Optional: MATCH SIMPLE skips the check while AccountId is NULL. Services set the scalar only.
        builder.HasOne<FinancialAccount>().WithMany().HasForeignKey(t => new { t.CompanyId, t.AccountId })
            .HasPrincipalKey(a => new { a.CompanyId, a.Id }).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(t => t.ReconciledByUser).WithMany().HasForeignKey(t => t.ReconciledByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(t => t.CreatedByUser).WithMany().HasForeignKey(t => t.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
