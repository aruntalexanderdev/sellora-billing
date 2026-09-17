using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.Domain.Entities;

namespace Sellora.Infrastructure.Data.Configurations;

public class UnitConfiguration : IEntityTypeConfiguration<Unit>
{
    public void Configure(EntityTypeBuilder<Unit> builder)
    {
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Name).HasMaxLength(60).IsRequired();
        builder.Property(u => u.ShortCode).HasMaxLength(10).IsRequired();
        builder.Property(u => u.Type).HasConversion<string>().HasMaxLength(20);
        builder.HasIndex(u => u.Name).IsUnique();
        builder.HasIndex(u => u.ShortCode).IsUnique();
    }
}
