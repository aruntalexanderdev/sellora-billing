using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.Domain.Entities;

namespace Sellora.Infrastructure.Data.Configurations;

public class ExpenseCategoryConfiguration : IEntityTypeConfiguration<ExpenseCategory>
{
    public void Configure(EntityTypeBuilder<ExpenseCategory> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Name).HasMaxLength(60).IsRequired();
        builder.HasAlternateKey(c => new { c.CompanyId, c.Id });
        builder.HasIndex(c => new { c.CompanyId, c.Name }, "IX_ExpenseCategories_CompanyId_Name").IsUnique();
    }
}

public class ExpenseConfiguration : IEntityTypeConfiguration<Expense>
{
    public void Configure(EntityTypeBuilder<Expense> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.ExpenseNo).HasMaxLength(30).IsRequired();
        builder.Property(e => e.Reference).HasMaxLength(60);
        builder.Property(e => e.Notes).HasMaxLength(500);
        builder.Property(e => e.CancellationReason).HasMaxLength(200);
        builder.Property(e => e.PaymentMethod).HasConversion<string>().HasMaxLength(20);
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);

        builder.Property(e => e.Amount).HasColumnType("numeric(18,4)");
        builder.Property(e => e.ExchangeRateToBase).HasColumnType("numeric(18,6)");
        builder.Property(e => e.BaseAmount).HasColumnType("numeric(18,2)");

        builder.ToTable(t => t.HasCheckConstraint("CK_Expenses_Amount_Positive", "\"Amount\" > 0"));
        builder.HasIndex(e => new { e.CompanyId, e.ExpenseNo }, "IX_Expenses_CompanyId_ExpenseNo").IsUnique();
        builder.HasIndex(e => e.ExpenseDate);
        builder.HasIndex(e => e.CategoryId);

        builder.HasOne(e => e.Category).WithMany().HasForeignKey(e => e.CategoryId).OnDelete(DeleteBehavior.Restrict);
        // Same-company guarantee: an expense's category belongs to the expense's company.
        builder.HasOne<ExpenseCategory>().WithMany().HasForeignKey(e => new { e.CompanyId, e.CategoryId })
            .HasPrincipalKey(c => new { c.CompanyId, c.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.Currency).WithMany().HasForeignKey(e => e.CurrencyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.CreatedByUser).WithMany().HasForeignKey(e => e.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(e => e.CancelledByUser).WithMany().HasForeignKey(e => e.CancelledByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
