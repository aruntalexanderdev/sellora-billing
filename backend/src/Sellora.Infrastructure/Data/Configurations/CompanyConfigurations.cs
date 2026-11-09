using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.Domain.Entities;

namespace Sellora.Infrastructure.Data.Configurations;

public class CompanyConfiguration : IEntityTypeConfiguration<Company>
{
    public void Configure(EntityTypeBuilder<Company> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Code).HasMaxLength(20).IsRequired();
        builder.Property(c => c.Name).HasMaxLength(150).IsRequired();
        builder.HasIndex(c => c.Code, "IX_Companies_Code").IsUnique();
        // Guid.Empty is the "no company" sentinel of the query filter: no company may use it.
        builder.ToTable(t => t.HasCheckConstraint("CK_Companies_Id_NotEmpty", "\"Id\" <> '00000000-0000-0000-0000-000000000000'"));
    }
}

public class UserCompanyMembershipConfiguration : IEntityTypeConfiguration<UserCompanyMembership>
{
    public void Configure(EntityTypeBuilder<UserCompanyMembership> builder)
    {
        builder.HasKey(m => m.Id);
        // Target of the Phase 5/7 composite FKs (branch assignments, role assignments).
        builder.HasAlternateKey(m => new { m.CompanyId, m.Id });
        builder.Property(m => m.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(m => m.AccessScope).HasConversion<string>().HasMaxLength(20);

        builder.HasIndex(m => new { m.UserId, m.CompanyId }, "IX_UserCompanyMemberships_UserId_CompanyId").IsUnique();
        builder.HasIndex(m => m.CompanyId, "IX_UserCompanyMemberships_CompanyId");

        builder.HasOne(m => m.User).WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(m => m.Company).WithMany().HasForeignKey(m => m.CompanyId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(m => m.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(m => m.UpdatedByUserId).OnDelete(DeleteBehavior.Restrict);

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_UserCompanyMemberships_Status", "\"Status\" IN ('Active', 'Suspended', 'Revoked')");
            t.HasCheckConstraint("CK_UserCompanyMemberships_AccessScope", "\"AccessScope\" IN ('CompanyWide', 'AssignedBranches')");
        });
    }
}

public class DocumentSequenceConfiguration : IEntityTypeConfiguration<DocumentSequence>
{
    public void Configure(EntityTypeBuilder<DocumentSequence> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Series).HasMaxLength(10).IsRequired();

        // Two partial uniques instead of one over nullable BranchId (a NULL would never collide). The FK from BranchId to
        // Branches is added by Phase 2.
        builder.HasIndex(s => new { s.CompanyId, s.Series }, "IX_DocumentSequences_CompanyId_Series")
            .IsUnique().HasFilter("\"BranchId\" IS NULL");
        builder.HasIndex(s => new { s.CompanyId, s.BranchId, s.Series }, "IX_DocumentSequences_CompanyId_BranchId_Series")
            .IsUnique().HasFilter("\"BranchId\" IS NOT NULL");

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_DocumentSequences_NextValue_Positive", "\"NextValue\" >= 1");
            t.HasCheckConstraint("CK_DocumentSequences_Series_NotEmpty", "char_length(\"Series\") > 0");
        });
    }
}
