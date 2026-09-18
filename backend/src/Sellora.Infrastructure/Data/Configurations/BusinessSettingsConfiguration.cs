using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.Domain.Entities;

namespace Sellora.Infrastructure.Data.Configurations;

public class BusinessSettingsConfiguration : IEntityTypeConfiguration<BusinessSettings>
{
    public void Configure(EntityTypeBuilder<BusinessSettings> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.BusinessName).HasMaxLength(150).IsRequired();
        builder.Property(s => s.InvoicePrefix).HasMaxLength(10).IsRequired();
        builder.Property(s => s.AddressLine).HasMaxLength(300);
        builder.Property(s => s.City).HasMaxLength(80);
        builder.Property(s => s.State).HasMaxLength(80);
        builder.Property(s => s.Gstin).HasMaxLength(20);
        builder.Property(s => s.Phone).HasMaxLength(30);
        builder.Property(s => s.Email).HasMaxLength(200);

        builder.HasOne(s => s.BaseCurrency).WithMany().HasForeignKey(s => s.BaseCurrencyId).OnDelete(DeleteBehavior.Restrict);
    }
}
