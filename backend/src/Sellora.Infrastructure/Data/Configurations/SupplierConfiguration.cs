using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.Domain.Entities;

namespace Sellora.Infrastructure.Data.Configurations;

public class SupplierConfiguration : IEntityTypeConfiguration<Supplier>
{
    public void Configure(EntityTypeBuilder<Supplier> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Name).HasMaxLength(150).IsRequired();
        builder.Property(s => s.City).HasMaxLength(80);
        builder.Property(s => s.Gstin).HasMaxLength(20);
        builder.Property(s => s.PaymentTerms).HasMaxLength(40);
        builder.HasAlternateKey(s => new { s.CompanyId, s.Id });
        builder.HasIndex(s => s.Name);

        builder.HasOne(s => s.Currency).WithMany().HasForeignKey(s => s.CurrencyId).OnDelete(DeleteBehavior.Restrict);
    }
}
