using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.Domain.Entities;

namespace Sellora.Infrastructure.Data.Configurations;

public class IdempotencyKeyConfiguration : IEntityTypeConfiguration<IdempotencyKey>
{
    public void Configure(EntityTypeBuilder<IdempotencyKey> builder)
    {
        builder.HasKey(k => k.Id);
        builder.Property(k => k.Scope).HasMaxLength(30).IsRequired();
        builder.Property(k => k.Key).HasMaxLength(100).IsRequired();
        builder.HasIndex(k => new { k.UserId, k.Scope, k.Key }).IsUnique();
    }
}
