using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Sellora.Domain.Entities;

namespace Sellora.Infrastructure.Data.Configurations;

public class BranchConfiguration : IEntityTypeConfiguration<Branch>
{
    public void Configure(EntityTypeBuilder<Branch> builder)
    {
        builder.HasKey(b => b.Id);
        // Target of the composite same-company FKs from documents and DocumentSequences.
        builder.HasAlternateKey(b => new { b.CompanyId, b.Id });

        builder.Property(b => b.Code).HasMaxLength(6).IsRequired();
        builder.Property(b => b.Name).HasMaxLength(150).IsRequired();
        builder.Property(b => b.AddressLine).HasMaxLength(300);
        builder.Property(b => b.City).HasMaxLength(80);
        builder.Property(b => b.State).HasMaxLength(80);
        builder.Property(b => b.Phone).HasMaxLength(30);
        builder.Property(b => b.Email).HasMaxLength(200);
        builder.Property(b => b.Gstin).HasMaxLength(20);
        builder.Property(b => b.InvoicePrefix).HasMaxLength(10);

        builder.HasIndex(b => new { b.CompanyId, b.Code }, "IX_Branches_CompanyId_Code").IsUnique();
        builder.HasIndex(b => new { b.CompanyId, b.Name }, "IX_Branches_CompanyId_Name").IsUnique();
        // At most one default branch per company.
        builder.HasIndex(b => b.CompanyId, "IX_Branches_CompanyId_IsDefault").IsUnique().HasFilter("\"IsDefault\"");

        builder.HasOne<User>().WithMany().HasForeignKey(b => b.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(b => b.UpdatedByUserId).OnDelete(DeleteBehavior.Restrict);

        builder.ToTable(t =>
        {
            t.HasCheckConstraint("CK_Branches_Default_Active", "\"IsActive\" OR NOT \"IsDefault\"");
            t.HasCheckConstraint("CK_Branches_Id_NotEmpty", "\"Id\" <> '00000000-0000-0000-0000-000000000000'");
        });
    }
}
