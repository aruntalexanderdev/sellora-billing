using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.Domain.Entities;

namespace Sellora.Infrastructure.Data.Configurations;

public class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.HasKey(r => r.Id);
        // Targets of the Phase 7 composite FKs: (CompanyId, Id) for same-company references, (CompanyId, Id, ScopeType) for assignments (FK_URA_Role).
        builder.HasAlternateKey(r => new { r.CompanyId, r.Id });
        builder.HasAlternateKey(r => new { r.CompanyId, r.Id, r.ScopeType });

        builder.Property(r => r.Name).HasMaxLength(80).IsRequired();
        builder.Property(r => r.Description).HasMaxLength(400);
        builder.Property(r => r.ScopeType).HasConversion<string>().HasMaxLength(20).HasDefaultValue(RoleScope.Company);

        // Nullable composite FK (MATCH SIMPLE: a NULL BranchId = company role is not checked); navigation-less (phase-2 D3).
        builder.HasOne<Branch>().WithMany().HasForeignKey(r => new { r.CompanyId, r.BranchId })
            .HasPrincipalKey(b => new { b.CompanyId, b.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_Roles_Branches_CompanyId_BranchId");
        builder.HasOne<User>().WithMany().HasForeignKey(r => r.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(r => r.UpdatedByUserId).OnDelete(DeleteBehavior.Restrict);

        // Exactly one system (Administrator) role per company (D1/D9).
        builder.HasIndex(r => r.CompanyId, "IX_Roles_CompanyId_System").IsUnique().HasFilter("\"IsSystem\"");
        // Case-insensitive name uniqueness is enforced by the expression indexes UX_Roles_Company_Name_Lower ((CompanyId, lower(Name)) WHERE
        // ScopeType = 'Company') and UX_Roles_Branch_Name_Lower ((CompanyId, BranchId, lower(Name)) WHERE ScopeType = 'Branch'), plus the unique
        // UX_Roles_Id_BranchId (Id, BranchId) that FK_URA_RoleBranch references. EF cannot model expression indexes or a nullable alternate key, so
        // they live only in migration AddScopedRoles (migrationBuilder.Sql); keep them when touching Roles (precedent: UnitConfiguration).

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_Roles_Scope",
                "(\"ScopeType\" = 'Company' AND \"BranchId\" IS NULL) OR (\"ScopeType\" = 'Branch' AND \"BranchId\" IS NOT NULL)");
            t.HasCheckConstraint("CK_Roles_SystemIsCompany", "NOT \"IsSystem\" OR \"ScopeType\" = 'Company'");
            t.HasCheckConstraint("CK_Roles_Id_NotEmpty", "\"Id\" <> '00000000-0000-0000-0000-000000000000'");
        });
    }
}

public class UserRoleAssignmentConfiguration : IEntityTypeConfiguration<UserRoleAssignment>
{
    public void Configure(EntityTypeBuilder<UserRoleAssignment> builder)
    {
        builder.ToTable("UserRoleAssignments");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.ScopeType).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(a => a.AssignmentReason).HasMaxLength(500);
        builder.Property(a => a.EndReason).HasMaxLength(500);

        // All identifiers <= 63 characters (explicit short names, "URA").
        builder.HasOne(a => a.Membership).WithMany(m => m.RoleAssignments)
            .HasForeignKey(a => new { a.CompanyId, a.UserCompanyMembershipId })
            .HasPrincipalKey(m => new { m.CompanyId, m.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_URA_Membership");
        builder.HasOne(a => a.Role).WithMany()
            .HasForeignKey(a => new { a.CompanyId, a.RoleId, a.ScopeType })
            .HasPrincipalKey(r => new { r.CompanyId, r.Id, r.ScopeType }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_URA_Role");
        builder.HasOne<Branch>().WithMany().HasForeignKey(a => new { a.CompanyId, a.BranchId })
            .HasPrincipalKey(b => new { b.CompanyId, b.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_URA_Branch");
        // FK_URA_RoleBranch (RoleId, BranchId) -> Roles (Id, BranchId) is raw SQL in migration AddScopedRoles (EF cannot target a nullable alternate key):
        // together with FK_URA_Role it proves assignment scope and branch equal the role's.
        builder.HasOne<User>().WithMany().HasForeignKey(a => a.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(a => a.EndedByUserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(a => new { a.UserCompanyMembershipId, a.RoleId }, "IX_URA_Membership_Role_Active")
            .IsUnique().HasFilter("\"Status\" = 'Active'");
        builder.HasIndex(a => new { a.CompanyId, a.RoleId }, "IX_URA_Role_Active").HasFilter("\"Status\" = 'Active'");
        builder.HasIndex(a => new { a.CompanyId, a.BranchId }, "IX_URA_Branch_Active").HasFilter("\"Status\" = 'Active'");
        builder.HasIndex(a => new { a.CompanyId, a.UserCompanyMembershipId, a.StartAt }, "IX_URA_Membership_History");
        builder.HasIndex(a => new { a.CompanyId, a.RoleId, a.ScopeType }, "IX_URA_Role_Scope");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_URA_Status", "\"Status\" IN ('Active', 'Ended')");
            t.HasCheckConstraint("CK_URA_Active_EndAt", "(\"Status\" = 'Active') = (\"EndAt\" IS NULL)");
            t.HasCheckConstraint("CK_URA_EndAt_After_StartAt", "\"EndAt\" IS NULL OR \"EndAt\" >= \"StartAt\"");
            t.HasCheckConstraint("CK_URA_Scope_Branch", "(\"ScopeType\" = 'Branch') = (\"BranchId\" IS NOT NULL)");
            t.HasCheckConstraint("CK_URA_Id_NotEmpty", "\"Id\" <> '00000000-0000-0000-0000-000000000000'");
        });
    }
}
