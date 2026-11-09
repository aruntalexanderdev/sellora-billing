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
        builder.HasAlternateKey(a => new { a.CompanyId, a.Id });
        builder.HasIndex(a => new { a.CompanyId, a.Name }, "IX_FinancialAccounts_CompanyId_Name").IsUnique();
        builder.HasOne(a => a.CreatedByUser).WithMany().HasForeignKey(a => a.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
    }
}
