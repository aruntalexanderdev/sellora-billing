using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.Domain.Entities;

namespace Sellora.Infrastructure.Data.Configurations;

public class UserBranchAssignmentConfiguration : IEntityTypeConfiguration<UserBranchAssignment>
{
    public void Configure(EntityTypeBuilder<UserBranchAssignment> builder)
    {
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(a => a.AssignmentReason).HasMaxLength(500);
        builder.Property(a => a.EndReason).HasMaxLength(500);

        // Same-company membership (AK (CompanyId, Id), CompanyConfigurations) and branch; navigation-less on Branch (phase-2 D3).
        builder.HasOne(a => a.Membership).WithMany(m => m.BranchAssignments)
            .HasForeignKey(a => new { a.CompanyId, a.UserCompanyMembershipId })
            .HasPrincipalKey(m => new { m.CompanyId, m.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_UserBranchAssignments_Memberships_Company");
        builder.HasOne<Branch>().WithMany().HasForeignKey(a => new { a.CompanyId, a.BranchId })
            .HasPrincipalKey(b => new { b.CompanyId, b.Id }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_UserBranchAssignments_Branches_CompanyId_BranchId");
        builder.HasOne<User>().WithMany().HasForeignKey(a => a.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(a => a.EndedByUserId).OnDelete(DeleteBehavior.Restrict);

        // One active row per (membership, branch); one active primary per membership; branch -> users lookup.
        builder.HasIndex(a => new { a.UserCompanyMembershipId, a.BranchId }, "IX_UserBranchAssignments_Membership_Branch_Active")
            .IsUnique().HasFilter("\"Status\" = 'Active'");
        builder.HasIndex(a => a.UserCompanyMembershipId, "IX_UserBranchAssignments_Membership_Primary")
            .IsUnique().HasFilter("\"Status\" = 'Active' AND \"IsPrimary\"");
        builder.HasIndex(a => new { a.CompanyId, a.BranchId }, "IX_UserBranchAssignments_Branch_Active")
            .HasFilter("\"Status\" = 'Active'");
        // History per membership (the composite FK's referencing columns).
        builder.HasIndex(a => new { a.CompanyId, a.UserCompanyMembershipId, a.StartAt }, "IX_UserBranchAssignments_Membership_History");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_UserBranchAssignments_Status", "\"Status\" IN ('Active', 'Ended')");
            t.HasCheckConstraint("CK_UserBranchAssignments_Active_EndAt", "(\"Status\" = 'Active') = (\"EndAt\" IS NULL)");
            t.HasCheckConstraint("CK_UserBranchAssignments_EndAt_After_StartAt", "\"EndAt\" IS NULL OR \"EndAt\" >= \"StartAt\"");
            t.HasCheckConstraint("CK_UserBranchAssignments_Id_NotEmpty", "\"Id\" <> '00000000-0000-0000-0000-000000000000'");
        });
    }
}

public class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Action).HasMaxLength(64).IsRequired();
        builder.Property(e => e.TargetType).HasMaxLength(64).IsRequired();
        builder.Property(e => e.Reason).HasMaxLength(500);
        builder.Property(e => e.Before).HasColumnType("jsonb");
        builder.Property(e => e.After).HasColumnType("jsonb");
        builder.Property(e => e.CorrelationId).HasMaxLength(64);

        builder.HasOne<User>().WithMany().HasForeignKey(e => e.ActorUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(e => e.SubjectUserId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => new { e.CompanyId, e.TargetType, e.TargetId, e.OccurredAt }, "IX_AuditEvents_Target");
        builder.HasIndex(e => new { e.CompanyId, e.SubjectUserId, e.OccurredAt }, "IX_AuditEvents_Subject");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_AuditEvents_Id_NotEmpty", "\"Id\" <> '00000000-0000-0000-0000-000000000000'");
        });
    }
}
