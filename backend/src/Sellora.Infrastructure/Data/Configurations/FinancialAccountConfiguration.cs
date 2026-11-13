using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.Domain.Entities;

namespace Sellora.Infrastructure.Data.Configurations;

public class FinancialAccountConfiguration : IEntityTypeConfiguration<FinancialAccount>
{
    public void Configure(EntityTypeBuilder<FinancialAccount> builder)
    {
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Name).HasMaxLength(60).IsRequired();
        builder.Property(a => a.Reference).HasMaxLength(60);
        builder.Property(a => a.Kind).HasConversion<string>().HasMaxLength(10);
        builder.Property(a => a.OverdraftLimit).HasColumnType("numeric(18,2)").HasDefaultValue(0m);
        builder.ToTable(t => t.HasCheckConstraint("CK_FinancialAccounts_Overdraft", "\"OverdraftLimit\" >= 0 AND (\"Kind\" <> 'Cash' OR \"OverdraftLimit\" = 0)"));
        builder.HasAlternateKey(a => new { a.CompanyId, a.Id });
        builder.HasIndex(a => new { a.CompanyId, a.Name }, "IX_FinancialAccounts_CompanyId_Name").IsUnique();
        builder.HasIndex(a => new { a.CompanyId, a.BranchId }, "IX_FinancialAccounts_CompanyId_BranchId");
        // Same-company branch: (CompanyId, BranchId) -> Branches (CompanyId, Id). Navigation-less (no filtered required navigation, Phase 1 section 8).
        builder.HasOne<Branch>().WithMany().HasForeignKey(a => new { a.CompanyId, a.BranchId })
            .HasPrincipalKey(b => new { b.CompanyId, b.Id }).IsRequired(false).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_FinancialAccounts_Branches_CompanyId_BranchId");
        builder.HasOne(a => a.CreatedByUser).WithMany().HasForeignKey(a => a.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
