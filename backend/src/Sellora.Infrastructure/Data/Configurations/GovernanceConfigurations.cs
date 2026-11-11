using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.Domain.Entities;

namespace Sellora.Infrastructure.Data.Configurations;

// Phase 6 D7. All constraint and index names are explicit and <= 63 bytes (EF defaults for these composite FKs exceed the limit).
public class CompanyGovernanceMemberConfiguration : IEntityTypeConfiguration<CompanyGovernanceMember>
{
    public void Configure(EntityTypeBuilder<CompanyGovernanceMember> builder)
    {
        builder.HasKey(m => m.Id);
        builder.HasAlternateKey(m => new { m.CompanyId, m.Id });
        builder.Property(m => m.Title).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(m => m.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(m => m.EndReason).HasMaxLength(500);

        // Same-company membership (AK (CompanyId, Id), CompanyConfigurations); no UserId copy.
        builder.HasOne(m => m.Membership).WithMany()
            .HasForeignKey(m => new { m.CompanyId, m.UserCompanyMembershipId })
            .HasPrincipalKey(x => new { x.CompanyId, x.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_CompanyGovernanceMembers_Memberships_Company");
        builder.HasOne<User>().WithMany().HasForeignKey(m => m.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(m => m.EndedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(m => m.UpdatedByUserId).OnDelete(DeleteBehavior.Restrict);

        // At most one Active governance row per membership; history lookup (the composite FK's referencing columns).
        builder.HasIndex(m => m.UserCompanyMembershipId, "IX_GovMembers_Membership_Active").IsUnique().HasFilter("\"Status\" = 'Active'");
        builder.HasIndex(m => new { m.CompanyId, m.UserCompanyMembershipId }, "IX_GovMembers_Company_Membership");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_CompanyGovernanceMembers_Title", "\"Title\" IN ('Owner', 'BoardMember', 'Director', 'DelegatedAdministrator')");
            t.HasCheckConstraint("CK_CompanyGovernanceMembers_Status", "\"Status\" IN ('Active', 'Ended')");
            t.HasCheckConstraint("CK_CompanyGovernanceMembers_Active_EndAt", "(\"Status\" = 'Active') = (\"EndAt\" IS NULL)");
            t.HasCheckConstraint("CK_CompanyGovernanceMembers_Id_NotEmpty", "\"Id\" <> '00000000-0000-0000-0000-000000000000'");
        });
    }
}

public class GovernancePermissionConfiguration : IEntityTypeConfiguration<GovernancePermission>
{
    public void Configure(EntityTypeBuilder<GovernancePermission> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Code).HasMaxLength(100).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(300).IsRequired();
        builder.HasIndex(p => p.Code, "IX_GovernancePermissions_Code").IsUnique();
    }
}

public class GovernanceMemberPermissionConfiguration : IEntityTypeConfiguration<GovernanceMemberPermission>
{
    public void Configure(EntityTypeBuilder<GovernanceMemberPermission> builder)
    {
        builder.HasKey(g => g.Id);

        builder.HasOne(g => g.Member).WithMany(m => m.Permissions)
            .HasForeignKey(g => new { g.CompanyId, g.GovernanceMemberId })
            .HasPrincipalKey(m => new { m.CompanyId, m.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_GovMemberPerms_Members_Company");
        builder.HasOne(g => g.GovernancePermission).WithMany().HasForeignKey(g => g.GovernancePermissionId).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_GovMemberPerms_GovPermissions_Permission");
        builder.HasOne<User>().WithMany().HasForeignKey(g => g.GrantedByUserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(g => new { g.GovernanceMemberId, g.GovernancePermissionId }, "IX_GovMemberPerms_Member_Permission").IsUnique();
        builder.HasIndex(g => new { g.CompanyId, g.GovernanceMemberId }, "IX_GovMemberPerms_Company_Member");
        builder.HasIndex(g => g.GovernancePermissionId, "IX_GovMemberPerms_Permission");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_GovernanceMemberPermissions_Id_NotEmpty", "\"Id\" <> '00000000-0000-0000-0000-000000000000'");
        });
    }
}
