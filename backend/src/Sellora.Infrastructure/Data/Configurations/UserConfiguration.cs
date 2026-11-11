using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.Domain.Entities;

namespace Sellora.Infrastructure.Data.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.HasKey(u => u.Id);
        builder.Property(u => u.FirstName).HasMaxLength(80).IsRequired();
        builder.Property(u => u.LastName).HasMaxLength(80).IsRequired();
        builder.Property(u => u.Username).HasMaxLength(60).IsRequired();
        builder.Property(u => u.Email).HasMaxLength(200).IsRequired();
        builder.Property(u => u.PhoneNumber).HasMaxLength(30);
        builder.Property(u => u.PasswordHash).IsRequired();
        builder.Property(u => u.Status).HasConversion<string>().HasMaxLength(20);

        builder.HasIndex(u => u.Username).IsUnique();
        builder.HasIndex(u => u.Email).IsUnique();

        builder.HasOne(u => u.Role)
            .WithMany(r => r.Users)
            .HasForeignKey(u => u.RoleId)
            .IsRequired(false) // deprecated column (Phase 7, D4): nullable, FK kept one release
            .OnDelete(DeleteBehavior.Restrict);
    }
}
