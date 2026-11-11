using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.Domain.Entities;

namespace Sellora.Infrastructure.Data.Configurations;

public class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Code).HasMaxLength(80).IsRequired();
        builder.Property(p => p.Module).HasMaxLength(60).IsRequired();
        builder.Property(p => p.Action).HasMaxLength(40).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(300).IsRequired();
        builder.HasIndex(p => p.Code).IsUnique();
        // Phase 6 ADR-13: governance codes live in GovernancePermissions only; never in the operational catalogue.
        builder.ToTable(t => t.HasCheckConstraint("CK_Permissions_NotGovernance", "\"Code\" NOT LIKE 'Company.Governance.%'"));
    }
}
